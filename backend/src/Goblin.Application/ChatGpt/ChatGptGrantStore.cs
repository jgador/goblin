using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.Work;
using Goblin.Core.Work;
using Goblin.Persistence;
using Goblin.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Goblin.Application.ChatGpt;

// Administration is called only after the HTTP adapter verifies a local owner
// session and same-origin POST. Bearer tokens never authorize these operations.
public sealed class ChatGptGrantStore
{
    private readonly IDbContextFactory<GoblinDbContext> _factory;

    public ChatGptGrantStore(IDbContextFactory<GoblinDbContext> factory) => _factory = factory;

    public async Task<ChatgptWorkGrant[]> ListAsync(string issuer, string resource, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        return await db.ChatgptWorkGrants.AsNoTracking().Where(g => g.Issuer == issuer && g.Resource == resource && g.RevokedAt == null)
            .OrderByDescending(g => g.Id).Take(200).ToArrayAsync(token);
    }

    public async Task ApproveAsync(string issuer, string resource, string subject, long workId, int hours,
        bool shareApprovedResult, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 256 || subject != subject.Trim() || subject.Any(char.IsControl) ||
            workId <= 0 || hours is < 1 or > 168)
            throw new ApplicationFailure("invalid_command");
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        await using IDbContextTransaction transaction = await ApplicationTransaction.BeginAsync(db, token);
        DateTime now = await db.Database.SqlQuery<DateTime>($"SELECT transaction_timestamp() AS \"Value\"").SingleAsync(token);
        Persistence.Entities.WorkItem row = await db.WorkItems.SingleOrDefaultAsync(w => w.Id == workId, token) ?? throw new ApplicationFailure("work_not_found");
        long? resultId = null;
        if (shareApprovedResult)
        {
            WorkResult? result = WorkStatePersistence.Snapshot(row).Results.LastOrDefault(r => r.ApprovedAt is not null);
            resultId = result?.AttemptId ?? throw new ApplicationFailure("chatgpt_result_unavailable");
        }
        ChatgptWorkGrant? previous = await db.ChatgptWorkGrants.SingleOrDefaultAsync(g => g.Issuer == issuer &&
            g.Resource == resource && g.Subject == subject && g.WorkId == workId && g.RevokedAt == null, token);
        if (previous is not null)
        {
            Revoke(db, previous, now);
            await db.SaveChangesAsync(token);
        }
        if (await db.ChatgptWorkGrants.CountAsync(g => g.RevokedAt == null, token) >= 200)
            throw new ApplicationFailure("chatgpt_grant_limit");
        var grant = new ChatgptWorkGrant
        {
            Issuer = issuer,
            Subject = subject,
            Resource = resource,
            WorkId = workId,
            ResultAttemptId = resultId,
            ApprovedAt = now,
            ExpiresAt = now.AddHours(hours)
        };
        db.ChatgptWorkGrants.Add(grant);
        db.ChatgptGrantEvents.Add(new ChatgptGrantEvent { Grant = grant, Action = "approved", Actor = "local-owner", OccurredAt = now });
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    public async Task RevokeAsync(string issuer, string resource, long id, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        await using IDbContextTransaction transaction = await ApplicationTransaction.BeginAsync(db, token);
        ChatgptWorkGrant grant = await db.ChatgptWorkGrants.SingleOrDefaultAsync(g => g.Id == id &&
            g.Issuer == issuer && g.Resource == resource, token) ?? throw new ApplicationFailure("invalid_command");
        if (grant.RevokedAt is null)
        {
            DateTime now = await db.Database.SqlQuery<DateTime>($"SELECT transaction_timestamp() AS \"Value\"").SingleAsync(token);
            Revoke(db, grant, now);
            await db.SaveChangesAsync(token);
        }
        await transaction.CommitAsync(token);
    }

    private static void Revoke(GoblinDbContext db, ChatgptWorkGrant grant, DateTime now)
    {
        grant.RevokedAt = now;
        db.ChatgptGrantEvents.Add(new ChatgptGrantEvent { Grant = grant, Action = "revoked", Actor = "local-owner", OccurredAt = now });
    }
}
