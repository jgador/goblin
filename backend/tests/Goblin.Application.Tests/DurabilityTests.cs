using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application;
using Goblin.Application.Runtime;
using Goblin.Application.Work;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;
using Goblin.Database;
using Goblin.Execution;
using Goblin.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Wolverine;
using Xunit;

namespace Goblin.Application.Tests;

public sealed class DatabaseFactAttribute : FactAttribute
{
    public DatabaseFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("GOBLIN_TEST_POSTGRES_ADMIN") is null || Environment.GetEnvironmentVariable("GOBLIN_TEST_POSTGRES_APP") is null)
            Skip = "Set PostgreSQL test connections to exercise real durable Work.";
    }
}

public sealed class DurabilityTests
{
    [DatabaseFact]
    public async Task RepeatedCommandsConcurrentClaimsAndApprovalSurviveNewScopes()
    {
        await using var fixture = await Fixture.CreateAsync();
        Guid id = Guid.NewGuid();
        var create = new WorkCommand(Guid.NewGuid(), id, WorkAction.Create, Text: "Produce a reviewable answer");
        var created = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => fixture.Apply(create)));
        Assert.All(created, x => Assert.Equal(1, x.Version));
        Assert.Single((await fixture.Get(id)).Work.History);
        WorkView assigned = await fixture.Apply(new(Guid.NewGuid(), id, WorkAction.Assign, 1, AgentId: WorkStore.DefaultAgentId));
        WorkView queued = await fixture.Apply(new(Guid.NewGuid(), id, WorkAction.Execute, assigned.Version));
        Guid attempt = queued.Work.Attempts.Single().Id;
        await fixture.Until(id, x => x.Work.Attempts[^1].Status == AttemptStatus.Starting);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => fixture.Host.Services.GetRequiredService<ExecutionCoordinator>().DispatchAsync(new(id, attempt), CancellationToken.None)));
        Assert.Equal(1, fixture.Runtime.Starts[attempt]);
        fixture.Runtime.Observations[attempt] = new(ObservationKind.Result, new("test-model", "opaque-session", "opaque-operation"), "Reviewed outcome");
        await fixture.Host.Services.GetRequiredService<ExecutionCoordinator>().ReconcileAsync(new(id, attempt), CancellationToken.None);
        WorkView review = await fixture.Get(id);
        Assert.Equal(AttentionReason.ResultReview, review.Work.Attention!.Reason);
        var approve = new WorkCommand(Guid.NewGuid(), id, WorkAction.Approve, review.Version, AttemptId: attempt);
        WorkView approved = await fixture.Apply(approve);
        Assert.Equal(WorkStatus.Completed, approved.Work.Status);
        Assert.Equal(approved.Version, (await fixture.Apply(approve)).Version);
        await fixture.RestartAsync();
        WorkView recovered = await fixture.Get(id);
        Assert.Equal(WorkStatus.Completed, recovered.Work.Status);
        Assert.Equal("opaque-session", recovered.Work.Attempts.Single().Session!.SessionReference);
        Assert.NotNull(recovered.Work.Results.Single().ApprovedAt);
        Assert.Equal(1, fixture.Runtime.Starts[attempt]);
    }

    [DatabaseFact]
    public async Task UncertainExecutionBlocksRetryAndAccountChangeUntilStopped()
    {
        await using var fixture = await Fixture.CreateAsync();
        WorkView running = await fixture.StartWork();
        Guid id = running.Work.Id, attempt = running.Work.Attempts[^1].Id;
        fixture.Runtime.Observations[attempt] = new(ObservationKind.Uncertain, Failure: FailureKind.RuntimeDisconnected);
        await fixture.Reconcile(id, attempt);
        WorkView uncertain = await fixture.Get(id);
        await Assert.ThrowsAsync<WorkRuleException>(() => fixture.Apply(new(Guid.NewGuid(), id, WorkAction.Retry, uncertain.Version)));
        using (var scope = fixture.Host.Services.CreateScope())
            await Assert.ThrowsAsync<ApplicationFailure>(() => scope.ServiceProvider.GetRequiredService<WorkStore>().SetConnectionAsync(WorkStore.DefaultAgentId, "Changing", true));
        await fixture.RestartAsync();
        Assert.Equal(1, fixture.Runtime.Starts[attempt]);
        fixture.Runtime.Observations[attempt] = new(ObservationKind.Stopped);
        await fixture.Reconcile(id, attempt);
        WorkView failed = await fixture.Get(id);
        Assert.Equal(AttentionReason.Failure, failed.Work.Attention!.Reason);
        Assert.Single(fixture.Runtime.Starts);
        WorkView retried = await fixture.Apply(new(Guid.NewGuid(), id, WorkAction.Retry, failed.Version));
        Assert.Equal(2, retried.Work.Attempts.Length);
        Assert.NotEqual(attempt, retried.Work.Attempts[^1].Id);
    }

    [DatabaseFact]
    public async Task InvalidCommandRollsBackStateReceiptAndDispatch()
    {
        await using var fixture = await Fixture.CreateAsync();
        Guid id = Guid.NewGuid(), rejected = Guid.NewGuid();
        await fixture.Apply(new(Guid.NewGuid(), id, WorkAction.Create, Text: "No agent assigned"));
        await Assert.ThrowsAsync<ApplicationFailure>(() => fixture.Apply(new(rejected, id, WorkAction.Execute, 1)));
        using var scope = fixture.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GoblinDbContext>();
        Assert.False(await db.WorkCommands.AnyAsync(x => x.Id == rejected));
        Assert.False(await db.ExecutionAttempts.AnyAsync(x => x.WorkId == id));
        Assert.Equal(1, (await fixture.Get(id)).Version);
        Assert.Empty(fixture.Runtime.Starts);
        await Assert.ThrowsAsync<ApplicationFailure>(() => fixture.Apply(new(Guid.NewGuid(), id, WorkAction.Assign, 99, AgentId: WorkStore.DefaultAgentId)));
    }

    [DatabaseFact]
    public async Task CancellationWaitsForStoppingEvidenceAndFailureIsNeverRetried()
    {
        await using var fixture = await Fixture.CreateAsync();
        WorkView running = await fixture.StartWork();
        Guid id = running.Work.Id, attempt = running.Work.Attempts[^1].Id;
        await fixture.Apply(new(Guid.NewGuid(), id, WorkAction.Cancel, running.Version));
        Assert.Equal(WorkStatus.Cancelling, (await fixture.Get(id)).Work.Status);
        fixture.Runtime.Observations[attempt] = new(ObservationKind.Uncertain, Failure: FailureKind.CancellationFailed);
        await fixture.Reconcile(id, attempt);
        Assert.Equal(AttentionReason.UncertainExecution, (await fixture.Get(id)).Work.Attention!.Reason);
        fixture.Runtime.Observations[attempt] = new(ObservationKind.Stopped);
        await fixture.Reconcile(id, attempt);
        Assert.Equal(WorkStatus.Cancelled, (await fixture.Get(id)).Work.Status);
        Assert.Equal(1, fixture.Runtime.Starts[attempt]);
    }

    [DatabaseFact]
    public async Task PersistedDispatchFailureEvidencePreventsRedeliveryFromStarting()
    {
        await using var fixture = await Fixture.CreateAsync();
        using (var scope = fixture.Host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<WorkStore>().SetConnectionAsync(WorkStore.DefaultAgentId, "Disconnected", false);
        Guid id = Guid.NewGuid();
        WorkView w = await fixture.Apply(new(Guid.NewGuid(), id, WorkAction.Create, Text: "Unavailable connection"));
        w = await fixture.Apply(new(Guid.NewGuid(), id, WorkAction.Assign, w.Version, AgentId: WorkStore.DefaultAgentId));
        w = await fixture.Apply(new(Guid.NewGuid(), id, WorkAction.Execute, w.Version));
        await fixture.Until(id, x => x.Work.Attention?.Reason == AttentionReason.Failure);
        Assert.Empty(fixture.Runtime.Starts);
        await fixture.RestartAsync();
        Assert.Equal(AttentionReason.Failure, (await fixture.Get(id)).Work.Attention!.Reason);
        Assert.Empty(fixture.Runtime.Starts);
    }

    [DatabaseFact]
    public async Task CleanupFailureSurvivesRestartAndRequiresReconciliationBeforeApproval()
    {
        await using var fixture = await Fixture.CreateAsync();
        WorkView running = await fixture.StartWork();
        Guid id = running.Work.Id, attempt = running.Work.Attempts[^1].Id;
        fixture.Runtime.FailCleanup = true;
        fixture.Runtime.Observations[attempt] = new(ObservationKind.Result, Text: "Saved before cleanup");
        await fixture.Reconcile(id, attempt);
        WorkView blocked = await fixture.Get(id);
        Assert.Equal(AttentionReason.CleanupRequired, blocked.Work.Attention!.Reason);
        Assert.Single(blocked.Work.Results);
        await Assert.ThrowsAsync<WorkRuleException>(() => fixture.Apply(new(Guid.NewGuid(), id, WorkAction.Approve, blocked.Version, AttemptId: attempt)));
        await fixture.RestartAsync();
        fixture.Runtime.FailCleanup = false;
        Assert.Equal(AttentionReason.CleanupRequired, (await fixture.Get(id)).Work.Attention!.Reason);
        await fixture.Reconcile(id, attempt);
        WorkView review = await fixture.Get(id);
        Assert.Equal(AttentionReason.ResultReview, review.Work.Attention!.Reason);
        Assert.False(review.Work.Attempts[^1].CleanupPending);
        await fixture.Apply(new(Guid.NewGuid(), id, WorkAction.Approve, review.Version, AttemptId: attempt));
        Assert.Equal(1, fixture.Runtime.Starts[attempt]);
    }

    [DatabaseFact]
    public async Task VerificationReservationsPreventDispatchAndPreserveWaitingAccountChanges()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var scope = fixture.Host.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<WorkStore>();
        await store.BeginVerificationAsync(WorkStore.DefaultAgentId);
        await Assert.ThrowsAsync<ApplicationFailure>(() => store.BeginVerificationAsync(WorkStore.DefaultAgentId));
        Guid id = Guid.NewGuid();
        WorkView work = await fixture.Apply(new(Guid.NewGuid(), id, WorkAction.Create, Text: "Wait for verification"));
        work = await fixture.Apply(new(Guid.NewGuid(), id, WorkAction.Assign, work.Version, AgentId: WorkStore.DefaultAgentId));
        work = await fixture.Apply(new(Guid.NewGuid(), id, WorkAction.Execute, work.Version));
        var coordinator = fixture.Host.Services.GetRequiredService<ExecutionCoordinator>();
        Guid attempt = work.Work.Attempts[^1].Id;
        await coordinator.DispatchAsync(new(id, attempt), CancellationToken.None);
        Assert.Empty(fixture.Runtime.Starts);
        Assert.Equal(WorkStatus.Queued, (await fixture.Get(id)).Work.Status);
        await store.SetConnectionAsync(WorkStore.DefaultAgentId, "Changing", requireIdle: true);
        await store.EndVerificationAsync(WorkStore.DefaultAgentId, true);
        Assert.Equal("Changing", (await store.ConnectionsAsync()).Single().Availability);
        await store.SetConnectionAsync(WorkStore.DefaultAgentId, "Available", false, completeChange: true);
        await coordinator.DispatchAsync(new(id, attempt), CancellationToken.None);
        Assert.Equal(1, fixture.Runtime.Starts[attempt]);
        await Assert.ThrowsAsync<ApplicationFailure>(() => store.BeginVerificationAsync(WorkStore.DefaultAgentId));
    }

    [DatabaseFact]
    public async Task StorageOutageEvidenceIsSurfacedBeforeHostReconciliation()
    {
        await using var fixture = await Fixture.CreateAsync();
        WorkView running = await fixture.StartWork();
        Guid id = running.Work.Id, attempt = running.Work.Attempts[^1].Id;
        await fixture.SetDatabaseAvailable(false);
        try { await Assert.ThrowsAnyAsync<Exception>(() => fixture.Reconcile(id, attempt)); }
        finally { await fixture.SetDatabaseAvailable(true); }
        Assert.Contains(await fixture.Host.Services.GetRequiredService<IDispatchFailureJournal>().ReadAsync(default),
            x => x.AttemptId == attempt && x.Failure == FailureKind.StorageUnavailable);
        await fixture.RestartAsync();
        WorkView uncertain = await fixture.Until(id, x => x.Work.Attention?.Reason == AttentionReason.UncertainExecution);
        Assert.Equal(FailureKind.StorageUnavailable, uncertain.Work.Attention!.Failure);
        await Assert.ThrowsAsync<WorkRuleException>(() => fixture.Apply(new(Guid.NewGuid(), id, WorkAction.Retry, uncertain.Version)));
        Assert.Equal(1, fixture.Runtime.Starts[attempt]);
    }

    private sealed class Runtime : IExecutionHost
    {
        public ConcurrentDictionary<Guid, int> Starts { get; } = new();
        public ConcurrentDictionary<Guid, ExecutionObservation> Observations { get; } = new();
        public bool FailCleanup { get; set; }
        public RuntimeCapabilities[] Capabilities => [new("codex", true, false, true, false, false)];
        public string EnvironmentFor(Guid work, Guid attempt) => "test/" + attempt;
        public Task StartAsync(WorkSnapshot work, CancellationToken token)
        {
            Starts.AddOrUpdate(work.Attempts[^1].Id, 1, (_, count) => count + 1);
            return Task.CompletedTask;
        }
        public Task<ExecutionObservation> ObserveAsync(WorkSnapshot work, bool stop, CancellationToken token) =>
            Task.FromResult(Observations.GetValueOrDefault(work.Attempts[^1].Id) ?? new(ObservationKind.Pending));
        public Task CleanupAsync(WorkSnapshot work, CancellationToken token) => FailCleanup
            ? Task.FromException(new IOException("Fixture cleanup failure")) : Task.CompletedTask;
    }

    private sealed class Fixture(string app, string name, string directory) : IAsyncDisposable
    {
        public Runtime Runtime { get; } = new();
        public IHost Host { get; private set; } = null!;
        public static async Task<Fixture> CreateAsync()
        {
            var admin = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("GOBLIN_TEST_POSTGRES_ADMIN"));
            var app = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("GOBLIN_TEST_POSTGRES_APP"));
            string name = "goblin_work_test_" + Guid.NewGuid().ToString("N");
            await using (var db = new NpgsqlConnection(admin.ConnectionString))
            {
                await db.OpenAsync();
                await new NpgsqlCommand("CREATE DATABASE " + name, db).ExecuteNonQueryAsync();
            }
            admin.Database = app.Database = name;
            await using (var db = new NpgsqlConnection(admin.ConnectionString))
            {
                await db.OpenAsync();
                await new NpgsqlCommand("""
                    REVOKE CREATE ON SCHEMA public FROM PUBLIC;
                    GRANT USAGE ON SCHEMA public TO goblin_app;
                    ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO goblin_app;
                    ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO goblin_app;
                    """, db).ExecuteNonQueryAsync();
            }
            await SqlMigrations.ApplyAsync(admin.ConnectionString, Path.Combine(AppContext.BaseDirectory, "migrations"), TextWriter.Null);
            var fixture = new Fixture(app.ConnectionString, name, Path.Combine(Path.GetTempPath(), name));
            await fixture.StartAsync();
            using var scope = fixture.Host.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<WorkStore>().SetConnectionAsync(WorkStore.DefaultAgentId, "Available", false);
            return fixture;
        }
        private async Task StartAsync()
        {
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddGoblinPersistence(app);
            builder.Services.AddWorkApplication();
            builder.Services.AddSingleton<IExecutionHost>(Runtime);
            builder.Services.AddSingleton<IDispatchFailureJournal>(new FileDispatchFailureJournal(directory));
            builder.UseWolverine(o => ApplicationServices.ConfigureMessaging(o, app));
            Host = builder.Build();
            await Host.StartAsync();
        }
        public async Task RestartAsync() { await Host.StopAsync(); Host.Dispose(); await StartAsync(); }
        public async Task SetDatabaseAvailable(bool available)
        {
            await using var admin = new NpgsqlConnection(Environment.GetEnvironmentVariable("GOBLIN_TEST_POSTGRES_ADMIN"));
            await admin.OpenAsync();
            await new NpgsqlCommand("ALTER DATABASE " + name + " ALLOW_CONNECTIONS " + (available ? "true" : "false"), admin).ExecuteNonQueryAsync();
            if (!available) await CloseApplicationSessions();
        }
        private async Task CloseApplicationSessions()
        {
            var cleanup = new NpgsqlConnectionStringBuilder(app) { Database = "postgres", Pooling = false };
            await using var connection = new NpgsqlConnection(cleanup.ConnectionString);
            await connection.OpenAsync();
            await using var close = new NpgsqlCommand("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @name AND usename = current_user", connection);
            close.Parameters.AddWithValue("name", name);
            await close.ExecuteNonQueryAsync();
        }
        public async Task<WorkView> Apply(WorkCommand command) { using var scope = Host.Services.CreateScope(); return await scope.ServiceProvider.GetRequiredService<WorkStore>().ApplyAsync(command); }
        public async Task<WorkView> Get(Guid id) { using var scope = Host.Services.CreateScope(); return await scope.ServiceProvider.GetRequiredService<WorkStore>().GetAsync(id); }
        public Task Reconcile(Guid id, Guid attempt) => Host.Services.GetRequiredService<ExecutionCoordinator>().ReconcileAsync(new(id, attempt), CancellationToken.None);
        public async Task<WorkView> StartWork()
        {
            Guid id = Guid.NewGuid();
            WorkView w = await Apply(new(Guid.NewGuid(), id, WorkAction.Create, Text: "Test durable execution"));
            w = await Apply(new(Guid.NewGuid(), id, WorkAction.Assign, w.Version, AgentId: WorkStore.DefaultAgentId));
            await Apply(new(Guid.NewGuid(), id, WorkAction.Execute, w.Version));
            return await Until(id, x => x.Work.Attempts[^1].Status == AttemptStatus.Starting);
        }
        public async Task<WorkView> Until(Guid id, Func<WorkView, bool> ready)
        {
            for (int i = 0; i < 100; i++) { WorkView w = await Get(id); if (ready(w)) return w; await Task.Delay(100); }
            throw new TimeoutException("Expected durable transition did not arrive.");
        }
        public async ValueTask DisposeAsync()
        {
            await Host.StopAsync(); Host.Dispose();
            NpgsqlConnection.ClearAllPools();
            // Wolverine has its own data source. Retire any remaining sessions
            // as their application role, without granting the schema owner the
            // server-wide privilege to terminate another role's backends.
            await CloseApplicationSessions();
            await using var db = new NpgsqlConnection(Environment.GetEnvironmentVariable("GOBLIN_TEST_POSTGRES_ADMIN"));
            await db.OpenAsync();
            await new NpgsqlCommand("DROP DATABASE " + name + " WITH (FORCE)", db).ExecuteNonQueryAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
