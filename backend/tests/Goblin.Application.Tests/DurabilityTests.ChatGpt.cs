using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Goblin.Application.ChatGpt;
using Goblin.Application.Work;
using Goblin.Contracts;
using Goblin.Core.Work;
using Goblin.Persistence;
using Goblin.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Goblin.Application.Tests;

public sealed partial class DurabilityTests
{
    [DatabaseFact]
    public async Task ChatGptReadsAreScopedToIdentityResourceAndInstanceAndSurviveRestart()
    {
        await using Fixture first = await Fixture.CreateAsync();
        await using Fixture second = await Fixture.CreateAsync();
        const long id = 9007199254740993;
        const string issuer = "https://identity.example.test/realms/goblin";
        const string resource = "https://first.example.test/mcp";
        await first.Apply(WorkCommands.Create(NextId(), id, "Shared objective"));
        await first.Apply(WorkCommands.Create(NextId(), id + 1, "Private objective"));
        await second.Apply(WorkCommands.Create(NextId(), id, "Other instance private objective"));
        IDbContextFactory<GoblinDbContext> factory = first.Host.Services.GetRequiredService<IDbContextFactory<GoblinDbContext>>();
        var grants = new ChatGptGrantStore(factory);
        var reads = new ChatGptWorkReader(factory);
        var reader = new ChatGptReader(issuer, "reader", resource, DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Empty((await reads.ListAsync(reader, 0, default)).Items);
        await grants.ApproveAsync(issuer, resource, "reader", id, 24, false, default);
        SharedWork shared = Assert.Single((await reads.ListAsync(reader, 0, default)).Items);
        Assert.Equal(id.ToString(), shared.WorkId);
        Assert.Equal("Shared objective", shared.Objective);
        Assert.Equal(WorkStatus.Ready, shared.Status);
        Assert.Null((await reads.AttentionAsync(reader, id, default)).Reason);
        await using (GoblinDbContext attentionDb = await factory.CreateDbContextAsync())
        {
            Persistence.Entities.WorkItem row = await attentionDb.WorkItems.SingleAsync(w => w.Id == id);
            JsonNode state = JsonNode.Parse(row.State)!;
            state["attention"] = JsonSerializer.SerializeToNode(new WorkAttention(AttentionReason.Failure, FailureKind.HostUnavailable), ContractJson.Options);
            row.State = state.ToJsonString();
            await attentionDb.SaveChangesAsync();
        }
        SharedWorkAttention attention = await reads.AttentionAsync(reader, id, default);
        Assert.Equal(AttentionReason.Failure, attention.Reason);
        Assert.Equal(FailureKind.HostUnavailable, attention.Failure);
        await Assert.ThrowsAsync<ChatGptAccessDenied>(() => reads.StatusAsync(reader, id + 1, default));
        await Assert.ThrowsAsync<ChatGptAccessDenied>(() => reads.ResultAsync(reader, id, default));
        foreach (ChatGptReader denied in new[]
        {
            new ChatGptReader(issuer, "other-reader", resource, reader.ExpiresAt),
            new ChatGptReader(issuer + "-other", "reader", resource, reader.ExpiresAt),
            new ChatGptReader(issuer, "reader", "https://second.example.test/mcp", reader.ExpiresAt),
            new ChatGptReader(issuer, "reader", resource, DateTimeOffset.UtcNow.AddSeconds(-1))
        })
        {
            Assert.Empty((await reads.ListAsync(denied, 0, default)).Items);
            await Assert.ThrowsAsync<ChatGptAccessDenied>(() => reads.StatusAsync(denied, id, default));
        }
        var otherReads = new ChatGptWorkReader(second.Host.Services.GetRequiredService<IDbContextFactory<GoblinDbContext>>());
        await Assert.ThrowsAsync<ChatGptAccessDenied>(() => otherReads.StatusAsync(reader, id, default));
        await first.RestartAsync();
        factory = first.Host.Services.GetRequiredService<IDbContextFactory<GoblinDbContext>>();
        reads = new ChatGptWorkReader(factory);
        grants = new ChatGptGrantStore(factory);
        Assert.Equal("Shared objective", (await reads.StatusAsync(reader, id, default)).Objective);
        long grant = Assert.Single(await grants.ListAsync(issuer, resource, default)).Id;
        await grants.RevokeAsync(issuer, resource, grant, default);
        await grants.RevokeAsync(issuer, resource, grant, default);
        await Assert.ThrowsAsync<ChatGptAccessDenied>(() => reads.StatusAsync(reader, id, default));
        await using GoblinDbContext db = await factory.CreateDbContextAsync();
        Assert.Equal(new[] { "approved", "revoked" }, await db.ChatgptGrantEvents.OrderBy(e => e.Id).Select(e => e.Action).ToArrayAsync());
    }

    [DatabaseFact]
    public async Task ChatGptResultGrantPinsAnApprovedAttemptAndConcurrentReplacementIsAudited()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        long id = NextId();
        const string issuer = "https://identity.example.test/realms/goblin";
        const string resource = "https://goblin.example.test/mcp";
        await fixture.Apply(WorkCommands.Create(NextId(), id, "Review a result"));
        IDbContextFactory<GoblinDbContext> factory = fixture.Host.Services.GetRequiredService<IDbContextFactory<GoblinDbContext>>();
        var grants = new ChatGptGrantStore(factory);
        var reads = new ChatGptWorkReader(factory);
        var reader = new ChatGptReader(issuer, "reader", resource, DateTimeOffset.UtcNow.AddMinutes(5));
        await Assert.ThrowsAsync<ApplicationFailure>(() => grants.ApproveAsync(issuer, resource, "reader", id, 24, true, default));
        Assert.Empty(await grants.ListAsync(issuer, resource, default));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        async Task Results(params WorkResult[] results)
        {
            await using GoblinDbContext db = await factory.CreateDbContextAsync();
            Persistence.Entities.WorkItem row = await db.WorkItems.SingleAsync(w => w.Id == id);
            JsonNode state = JsonNode.Parse(row.State)!;
            state["results"] = JsonSerializer.SerializeToNode(results, ContractJson.Options);
            row.State = state.ToJsonString();
            await db.SaveChangesAsync();
        }
        var approved = new WorkResult(11, "Selected approved result", now, now);
        await Results(approved, new WorkResult(12, "Private draft result", now));
        await grants.ApproveAsync(issuer, resource, "reader", id, 24, true, default);
        Assert.Equal("Selected approved result", (await reads.ResultAsync(reader, id, default)).Text);
        await Results(approved, new WorkResult(12, "Later approved result", now, now));
        Assert.Equal("Selected approved result", (await reads.ResultAsync(reader, id, default)).Text);
        await Task.WhenAll(
            grants.ApproveAsync(issuer, resource, "reader", id, 24, false, default),
            grants.ApproveAsync(issuer, resource, "reader", id, 24, false, default));
        Assert.Single(await grants.ListAsync(issuer, resource, default));
        await Assert.ThrowsAsync<ChatGptAccessDenied>(() => reads.ResultAsync(reader, id, default));
        await using (GoblinDbContext db = await factory.CreateDbContextAsync())
        {
            Assert.Equal(3, await db.ChatgptWorkGrants.CountAsync());
            Assert.Equal(5, await db.ChatgptGrantEvents.CountAsync());
            ChatgptWorkGrant current = await db.ChatgptWorkGrants.SingleAsync(g => g.RevokedAt == null);
            current.ApprovedAt = DateTime.UtcNow.AddHours(-2);
            current.ExpiresAt = DateTime.UtcNow.AddHours(-1);
            await db.SaveChangesAsync();
        }
        Assert.Empty((await reads.ListAsync(reader, 0, default)).Items);
        await Assert.ThrowsAsync<ChatGptAccessDenied>(() => reads.AttentionAsync(reader, id, default));
    }

    [DatabaseFact]
    public async Task ChatGptPaginationAndProjectionAreBoundedAndGrantAuditRollsBackTogether()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        const string issuer = "https://identity.example.test/realms/goblin";
        const string resource = "https://goblin.example.test/mcp";
        IDbContextFactory<GoblinDbContext> factory = fixture.Host.Services.GetRequiredService<IDbContextFactory<GoblinDbContext>>();
        var grants = new ChatGptGrantStore(factory);
        var reads = new ChatGptWorkReader(factory);
        var reader = new ChatGptReader(issuer, "reader", resource, DateTimeOffset.UtcNow.AddMinutes(5));
        for (int i = 0; i < 26; i++)
        {
            long id = NextId();
            await fixture.Apply(WorkCommands.Create(NextId(), id, new string('a', 600)));
            await grants.ApproveAsync(issuer, resource, "reader", id, 1, false, default);
        }
        SharedWorkPage page = await reads.ListAsync(reader, 0, default);
        Assert.Equal(25, page.Items.Length);
        Assert.All(page.Items, item => Assert.Equal(500, item.Objective.Length));
        SharedWorkPage next = await reads.ListAsync(reader, long.Parse(page.NextAfterWorkId!), default);
        Assert.Single(next.Items);
        Assert.Null(next.NextAfterWorkId);
        Assert.DoesNotContain(next.Items[0].WorkId, page.Items.Select(w => w.WorkId));
        // Reject the next event row to prove a saved grant cannot escape its audit transaction.
        await fixture.RejectId("chatgpt_grant_event", 27);
        await Assert.ThrowsAnyAsync<Exception>(() => grants.ApproveAsync(issuer, resource, "other-reader", long.Parse(page.Items[0].WorkId), 1, false, default));
        Assert.Equal(26, (await grants.ListAsync(issuer, resource, default)).Length);
        await using GoblinDbContext db = await factory.CreateDbContextAsync();
        Assert.Equal(26, await db.ChatgptGrantEvents.CountAsync());
    }
}
