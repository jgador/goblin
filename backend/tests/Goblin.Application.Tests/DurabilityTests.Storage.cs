using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Goblin.Application.Work;
using Goblin.Contracts;
using Goblin.Persistence;
using Goblin.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Xunit;

namespace Goblin.Application.Tests;

public sealed partial class DurabilityTests
{
    private static async Task WaitForStorageAsync(Func<Task<bool>> ready)
    {
        for (int i = 0; i < 800; i++)
        {
            if (await ready()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("Expected Wolverine storage operation did not complete.");
    }

    [DatabaseFact]
    public async Task EveryIdentityKindUsesItsSingularPostgresSequence()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        IDbContextFactory<GoblinDbContext> factory = fixture.Host.Services.GetRequiredService<IDbContextFactory<GoblinDbContext>>();
        foreach ((IdentityKind kind, string sequence) in new[]
        {
            (IdentityKind.Work, "work_item_id_seq"), (IdentityKind.Command, "work_command_id_seq"),
            (IdentityKind.Conversation, "conversation_id_seq"), (IdentityKind.Message, "conversation_message_id_seq"),
            (IdentityKind.Attempt, "execution_attempt_id_seq"), (IdentityKind.Event, "work_event_id_seq"),
            (IdentityKind.Inspection, "workspace_session_id_seq"), (IdentityKind.GitRepositoryOperation, "repository_operation_id_seq")
        })
        {
            await using (GoblinDbContext db = await factory.CreateDbContextAsync())
                await db.Database.ExecuteSqlAsync($"SELECT setval({"public." + sequence}::regclass, 9007199254740993, false)");
            long[] values = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
            {
                await using GoblinDbContext db = await factory.CreateDbContextAsync();
                return await IdentitySequence.NextAsync(db, kind);
            }));
            Assert.Equal(12, values.Distinct().Count());
            Assert.All(values, value => Assert.True(value > 9007199254740992));
            Assert.Equal(9007199254740993, values.Min());
        }
    }

    [DatabaseFact]
    public async Task GeneratedEfTransactionCommitsStateAndConsumedDispatchAndRollsBackFailures()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        IMessageBus bus = fixture.Host.Services.GetRequiredService<IMessageBus>();
        IDbContextFactory<GoblinDbContext> factory = fixture.Host.Services.GetRequiredService<IDbContextFactory<GoblinDbContext>>();
        long committed = NextId();
        await bus.SendAsync(new StorageTransaction(committed));
        await WaitForStorageAsync(async () =>
        {
            await using GoblinDbContext db = await factory.CreateDbContextAsync();
            return await db.WorkItems.AnyAsync(row => row.Id == committed && row.Objective == "Consumed storage dispatch");
        });
        long rejected = NextId();
        await fixture.RejectId("work_item", rejected);
        await bus.SendAsync(new StorageTransaction(rejected));
        var storage = (MessageDatabase<NpgsqlConnection>)fixture.Host.Services.GetRequiredService<IWolverineRuntime>().Storage;
        await WaitForStorageAsync(async () =>
        {
            await using DbCommand command = storage.DataSource.CreateCommand("SELECT count(*) FROM public.wolverine_dead_letter WHERE message_type LIKE '%StorageTransaction%'");
            return (long)(await command.ExecuteScalarAsync())! == 1;
        });
        await using GoblinDbContext read = await factory.CreateDbContextAsync();
        Assert.False(await read.WorkItems.AnyAsync(row => row.Id == rejected));
        Assert.Equal(1, await read.WorkItems.CountAsync());
        // Only the committed handler may persist and deliver the cascading message.
        Assert.Equal(1, await read.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM public.wolverine_incoming_envelope WHERE message_type LIKE '%StorageConsumed%'").SingleAsync());
    }

    [DatabaseFact]
    public async Task ScheduledWolverineDispatchRecoversAndIsConsumedAfterRestart()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        IMessageBus bus = fixture.Host.Services.GetRequiredService<IMessageBus>();
        long id = NextId();
        await bus.ScheduleAsync(new StorageTransaction(id), DateTimeOffset.UtcNow.AddSeconds(3));
        var store = (MessageDatabase<NpgsqlConnection>)fixture.Host.Services.GetRequiredService<IWolverineRuntime>().Storage;
        Assert.Contains(await store.AllIncomingAsync(), row => row.MessageType!.Contains(nameof(StorageTransaction), StringComparison.Ordinal) && row.Status == EnvelopeStatus.Scheduled);
        await fixture.RestartAsync();
        IDbContextFactory<GoblinDbContext> factory = fixture.Host.Services.GetRequiredService<IDbContextFactory<GoblinDbContext>>();
        await WaitForStorageAsync(async () =>
        {
            await using GoblinDbContext db = await factory.CreateDbContextAsync();
            return await db.WorkItems.AnyAsync(row => row.Id == id && row.Objective == "Consumed storage dispatch");
        });
        await using GoblinDbContext read = await factory.CreateDbContextAsync();
        Assert.Single(await read.WorkItems.ToArrayAsync());
    }

    [Fact]
    public void BuildGraphUsesMaintainedIntegrationProjects()
    {
        string dependencies = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Goblin.Application.Tests.deps.json"));
        using JsonDocument document = JsonDocument.Parse(dependencies);
        JsonElement libraries = document.RootElement.GetProperty("libraries");
        foreach (string project in new[] { "Wolverine.RDBMS", "Wolverine.Postgresql", "Wolverine.EntityFrameworkCore" })
        {
            JsonProperty library = Assert.Single(libraries.EnumerateObject(), item => item.Name.StartsWith(project + "/", StringComparison.Ordinal));
            Assert.Equal("project", library.Value.GetProperty("type").GetString());
        }
        Assert.DoesNotContain(libraries.EnumerateObject(), item => item.Name.StartsWith("Microsoft.EntityFrameworkCore.Design/", StringComparison.Ordinal));
        Assert.DoesNotContain(libraries.EnumerateObject(), item => item.Name.StartsWith("WolverineFx.RDBMS/", StringComparison.Ordinal) || item.Name.StartsWith("WolverineFx.Postgresql/", StringComparison.Ordinal) || item.Name.StartsWith("WolverineFx.EntityFrameworkCore/", StringComparison.Ordinal));
    }

    [Fact]
    public void WolverineEfEnvelopeMappingsUseCanonicalSingularTables()
    {
        var model = new ModelBuilder();
        model.MapWolverineEnvelopeStorage("public");
        string[] names = [.. model.Model.GetEntityTypes().Select(entity => entity.GetTableName()!).Order(StringComparer.Ordinal)];
        Assert.Equal(new[] { "wolverine_incoming_envelope", "wolverine_outgoing_envelope" }, names);
    }

    [DatabaseFact]
    public async Task WolverineNodeStoragePreservesAssignmentsRestrictionsHeartbeatAndHistory()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        var store = (MessageDatabase<NpgsqlConnection>)fixture.Host.Services.GetRequiredService<IWolverineRuntime>().Storage;
        Assert.Equal(JasperFx.AutoCreate.None, store.Settings.AutoCreate);
        await store.AssertStorageProvisionedAsync(default);
        await store.AssertStorageExistsAsync(default);
        INodeAgentPersistence nodes = store.Nodes;
        var node = new WolverineNode { NodeId = Guid.NewGuid(), Description = "Storage test", Version = new Version(1, 0), ControlUri = new Uri("local://storage-test") };
        node.AssignedNodeNumber = await nodes.PersistAsync(node, default);
        Assert.True(node.AssignedNodeNumber > 0);
        var agent = new Uri("storage-test://assignment");
        await nodes.AssignAgentsAsync(node.NodeId, [agent], default);
        Assert.Contains(agent, (await nodes.LoadNodeAsync(node.NodeId, default))!.ActiveAgents);
        Assert.True(await nodes.TryClaimAssignmentAsync(node.NodeId, agent, default));
        Assert.False(await nodes.TryClaimAssignmentAsync(Guid.NewGuid(), agent, default));
        await nodes.RemoveAssignmentAsync(Guid.NewGuid(), agent, default);
        Assert.Contains(agent, (await nodes.LoadNodeAsync(node.NodeId, default))!.ActiveAgents);
        var restriction = new AgentRestriction(Guid.NewGuid(), agent, AgentRestrictionType.Pinned, node.AssignedNodeNumber);
        await nodes.PersistAgentRestrictionsAsync([restriction], default);
        Assert.Contains((await nodes.LoadNodeAgentStateAsync(default)).Restrictions.Current, value => value.Id == restriction.Id);
        await nodes.OverwriteHealthCheckTimeAsync(node.NodeId, DateTimeOffset.UtcNow.AddHours(-1));
        Assert.True(await nodes.MarkHealthCheckAsync(node, default));
        await nodes.LogRecordsAsync(new NodeRecord { NodeNumber = node.AssignedNodeNumber, RecordType = NodeRecordType.NodeStarted, Description = "Storage test history" });
        await WaitForStorageAsync(async () => (await nodes.FetchRecentRecordsAsync(100)).Any(value => value.NodeNumber == node.AssignedNodeNumber && value.Description == "Storage test history"));
        await fixture.RestartAsync();
        store = (MessageDatabase<NpgsqlConnection>)fixture.Host.Services.GetRequiredService<IWolverineRuntime>().Storage;
        nodes = store.Nodes;
        Assert.Contains((await nodes.LoadNodeAgentStateAsync(default)).Restrictions.Current, value => value.Id == restriction.Id);
        Assert.Contains(await nodes.FetchRecentRecordsAsync(100), value => value.NodeNumber == node.AssignedNodeNumber);
        await nodes.PersistAgentRestrictionsAsync([restriction with { Type = AgentRestrictionType.None }], default);
        await nodes.RemoveAssignmentAsync(node.NodeId, agent, default);
        await nodes.DeleteAsync(node.NodeId, node.AssignedNodeNumber);
        Assert.Null(await nodes.LoadNodeAsync(node.NodeId, default));
        Assert.False(await nodes.MarkHealthCheckAsync(node, default));
        await nodes.ReregisterNodeAsync(node, default);
        Assert.Equal(node.AssignedNodeNumber, (await nodes.LoadNodeAsync(node.NodeId, default))!.AssignedNodeNumber);
        await nodes.DeleteAsync(node.NodeId, node.AssignedNodeNumber);
        await nodes.DeleteOldNodeRecordsAsync(2);
        Assert.InRange((await nodes.FetchRecentRecordsAsync(100)).Count, 0, 2);
    }

    [DatabaseFact]
    public async Task WolverineControlTransportPersistsAndConsumesControlEnvelopes()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        IWolverineRuntime runtime = fixture.Host.Services.GetRequiredService<IWolverineRuntime>();
        var store = (MessageDatabase<NpgsqlConnection>)runtime.Storage;
        IMessageBus bus = fixture.Host.Services.GetRequiredService<IMessageBus>();
        Guid node = Guid.NewGuid();
        var destination = new Uri("dbcontrol://" + node);
        await bus.EndpointFor(destination).SendAsync(new DispatchWork(NextId(), NextId()));
        await WaitForStorageAsync(async () =>
        {
            await using DbCommand command = store.DataSource.CreateCommand("SELECT count(*) FROM public.wolverine_control_queue WHERE node_id = @node");
            command.Parameters.Add(new NpgsqlParameter("node", node));
            return (long)(await command.ExecuteScalarAsync())! == 1;
        });
        // Send to the live node as well: the actual control listener must consume and delete the row.
        var live = new Uri("dbcontrol://" + runtime.Options.UniqueNodeId);
        await bus.EndpointFor(live).InvokeAsync(new DispatchWork(NextId(), NextId()), timeout: TimeSpan.FromSeconds(10));
        await WaitForStorageAsync(async () =>
        {
            await using DbCommand command = store.DataSource.CreateCommand("SELECT count(*) FROM public.wolverine_control_queue WHERE node_id = @node");
            command.Parameters.Add(new NpgsqlParameter("node", runtime.Options.UniqueNodeId));
            return (long)(await command.ExecuteScalarAsync())! == 0;
        });
    }

    [DatabaseFact]
    public async Task WolverineDeadLettersSurviveRestartAndExplicitReplay()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        var store = (MessageDatabase<NpgsqlConnection>)fixture.Host.Services.GetRequiredService<IWolverineRuntime>().Storage;
        var envelope = new Envelope { Id = Guid.NewGuid(), Data = [1, 2, 3], MessageType = "StorageTest", Destination = new Uri("local://storage-test"), Status = EnvelopeStatus.Incoming, OwnerId = int.MaxValue };
        await store.StoreIncomingAsync(envelope);
        await store.MoveToDeadLetterStorageAsync(envelope, new InvalidOperationException("Storage test failure"));
        Assert.NotNull(await store.DeadLetterEnvelopeByIdAsync(envelope.Id));
        Assert.DoesNotContain(await store.AllIncomingAsync(), row => row.Id == envelope.Id);
        await fixture.RestartAsync();
        store = (MessageDatabase<NpgsqlConnection>)fixture.Host.Services.GetRequiredService<IWolverineRuntime>().Storage;
        Assert.NotNull(await store.DeadLetterEnvelopeByIdAsync(envelope.Id));
        await store.ReplayAsync(new DeadLetterEnvelopeQuery([envelope.Id]), default);
        await WaitForStorageAsync(async () => (await store.DeadLetterEnvelopeByIdAsync(envelope.Id)) is null);
        Assert.Contains(await store.AllIncomingAsync(), row => row.Id == envelope.Id);
        await store.MarkIncomingEnvelopeAsHandledAsync(envelope);
        await store.DeleteAllHandledAsync();
        Assert.DoesNotContain(await store.AllIncomingAsync(), row => row.Id == envelope.Id);
    }

    [DatabaseFact]
    public async Task WolverineBatchAndTransactionalEnvelopeStorageUsesSingularTables()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        var store = (MessageDatabase<NpgsqlConnection>)fixture.Host.Services.GetRequiredService<IWolverineRuntime>().Storage;
        Envelope Incoming() => new() { Id = Guid.NewGuid(), Data = [1, 2, 3], MessageType = "StorageTest", Destination = new Uri("local://storage-test"), Status = EnvelopeStatus.Incoming, OwnerId = int.MaxValue };
        Envelope Outgoing() => new() { Id = Guid.NewGuid(), Data = [4, 5, 6], MessageType = "StorageTest", Destination = new Uri("local://storage-test"), OwnerId = int.MaxValue };
        Envelope[] incoming = [.. Enumerable.Range(0, 4).Select(_ => Incoming())];
        Envelope[] outgoing = [.. Enumerable.Range(0, 4).Select(_ => Outgoing())];
        await store.StoreIncomingAsync(incoming);
        await Assert.ThrowsAsync<DuplicateIncomingEnvelopeException>(() => store.StoreIncomingAsync(incoming[0]));
        await store.StoreOutgoingAsync(outgoing, int.MaxValue);
        IReadOnlyList<Envelope> stored = await store.AllIncomingAsync();
        Assert.All(incoming, envelope => Assert.Contains(stored, row => row.Id == envelope.Id));
        Assert.Equal(4, (await store.AllOutgoingAsync()).Count(row => row.MessageType == "StorageTest"));
        await using (DbConnection connection = await store.DataSource.OpenConnectionAsync())
        await using (DbTransaction transaction = await connection.BeginTransactionAsync())
        {
            await store.StoreIncomingAsync(transaction, [Incoming()]);
            await store.StoreOutgoingAsync(transaction, [Outgoing()]);
            await transaction.RollbackAsync();
        }
        Assert.Equal(4, (await store.AllIncomingAsync()).Count(row => row.MessageType == "StorageTest"));
        Assert.Equal(4, (await store.AllOutgoingAsync()).Count(row => row.MessageType == "StorageTest"));
        await store.ReleaseAllOwnershipAsync(int.MaxValue);
        Assert.All((await store.AllIncomingAsync()).Where(row => row.MessageType == "StorageTest"), row => Assert.Equal(0, row.OwnerId));
        await store.MarkIncomingEnvelopeAsHandledAsync(incoming);
        await store.DeleteAllHandledAsync();
        await store.DeleteOutgoingAsync(outgoing);
        Assert.DoesNotContain(await store.AllIncomingAsync(), row => row.MessageType == "StorageTest");
        Assert.DoesNotContain(await store.AllOutgoingAsync(), row => row.MessageType == "StorageTest");
    }
}

public sealed record StorageTransaction(long Id);

public sealed record StorageConsumed(long Id);

public static class StorageTransactionHandler
{
    [Transactional(typeof(GoblinDbContext))]
    public static StorageConsumed Handle(StorageTransaction message, GoblinDbContext db)
    {
        db.WorkItems.Add(new WorkItem
        {
            Id = message.Id,
            Objective = "Storage transaction",
            State = JsonSerializer.Serialize(new Core.Work.WorkItem(message.Id, "Storage transaction", DateTimeOffset.UtcNow).Snapshot(), ContractJson.Options)
        });
        return new StorageConsumed(message.Id);
    }
}

public static class StorageConsumedHandler
{
    [Transactional(typeof(GoblinDbContext))]
    public static async Task Handle(StorageConsumed message, GoblinDbContext db)
    {
        WorkItem item = await db.WorkItems.SingleAsync(row => row.Id == message.Id);
        item.Objective = "Consumed storage dispatch";
    }
}
