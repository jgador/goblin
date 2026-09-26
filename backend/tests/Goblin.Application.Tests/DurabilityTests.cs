using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application;
using Goblin.Application.Repositories;
using Goblin.Application.Runtime;
using Goblin.Application.Work;
using Goblin.Application.Workspaces;
using Goblin.Contracts.Runtime;
using Goblin.Core.Repositories;
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
    private static long _nextId = int.MaxValue;
    private static long NextId() => System.Threading.Interlocked.Increment(ref _nextId);

    private static async Task EnableHandoffRepository(Fixture fixture)
    {
        fixture.Runtime.RepositoryExecution = true;
        using IServiceScope scope = fixture.Host.Services.CreateScope();
        GitHubStore settings = scope.ServiceProvider.GetRequiredService<GitHubStore>();
        await settings.ObserveAsync(new("handoff", "42", "owner"), "Connected");
        await settings.SetRepositoryAsync(new(22, "owner/repo", "main", true), true, "handoff");
    }

    [DatabaseFact]
    public async Task RepositoryIntentWaitsForAuthorizationAndSurvivesRestartAndDuplicateCommands()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await EnableHandoffRepository(fixture);
        long id = NextId();
        WorkView work = await fixture.Apply(new(NextId(), id, WorkAction.Create, Text: "https://github.com/owner/repo convert Python to Rust"));
        work = await fixture.Apply(new(NextId(), id, WorkAction.Assign, work.Version, AgentId: 1));
        var start = new WorkCommand(NextId(), id, WorkAction.Execute, work.Version);
        work = await fixture.Apply(start);
        Assert.Equal(AttentionReason.RepositoryRequired, work.Work.Attention!.Reason);
        Assert.Empty(work.Work.Attempts);
        Assert.Null(work.Work.Workspace);
        Assert.Equal(work.Version, (await fixture.Apply(start)).Version);
        await fixture.RestartAsync();
        work = await fixture.Get(id);
        Assert.Equal("owner/repo", Assert.Single(work.Work.RepositoryRequest!.Repositories));
        ApplicationFailure rejected = await Assert.ThrowsAsync<ApplicationFailure>(() => fixture.Apply(new(NextId(), id,
            WorkAction.AuthorizeRepository, work.Version, Repository: new("unapproved/repo", "Goblin", "agent@example.com"))));
        Assert.Equal("repository_unavailable", rejected.Code);
        Assert.Empty((await fixture.Get(id)).Work.Attempts);
        var authorize = new WorkCommand(NextId(), id, WorkAction.AuthorizeRepository, work.Version,
            Repository: new("owner/repo", "Goblin", "agent@example.com"));
        WorkView accepted = await fixture.Apply(authorize);
        Assert.Equal(accepted.Version, (await fixture.Apply(authorize)).Version);
        work = await fixture.Until(id, x => x.Work.Attempts[^1].Status == AttemptStatus.Starting);
        Assert.Single(work.Work.Attempts);
        Assert.Equal("handoff", work.Work.Attempts[0].Target.Repository!.Grant!.Generation);
        Assert.Equal(1, fixture.Runtime.Starts[work.Work.Attempts[0].Id]);
        Assert.NotNull(work.Work.Workspace);
        Assert.Null(work.Work.RepositoryRequest);
    }

    [DatabaseFact]
    public async Task RepositoryAnswerHandsOffTheSameWorkWithoutRewritingTheConversationAttempt()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await EnableHandoffRepository(fixture);
        WorkView work = await fixture.StartWork();
        long id = work.Work.Id, original = work.Work.Attempts[^1].Id;
        fixture.Runtime.Observations[original] = new(ObservationKind.Paused,
            new("chosen", "conversation-session", "turn-1"), "Which repository?");
        await fixture.Reconcile(id, original);
        work = await fixture.Until(id, x => x.Work.Attention?.Reason == AttentionReason.InputRequired && !x.Work.Attempts[^1].CleanupPending);
        var answer = new WorkCommand(NextId(), id, WorkAction.Answer, work.Version,
            Text: "Clone https://github.com/owner/repo", DecisionId: work.Work.Decisions[^1].Id);
        work = await fixture.Apply(answer);
        Assert.Equal(AttentionReason.RepositoryRequired, work.Work.Attention!.Reason);
        Assert.Single(work.Work.Attempts);
        Assert.Equal(1, work.Work.Attempts[0].TurnNumber);
        work = await fixture.Apply(new(NextId(), id, WorkAction.AuthorizeRepository, work.Version,
            Repository: new("owner/repo", "Goblin", "agent@example.com")));
        Assert.Equal(2, work.Work.Attempts.Length);
        Assert.Equal(original, work.Work.Attempts[0].Id);
        Assert.Equal("conversation-session", work.Work.Attempts[0].Session!.SessionReference);
        Assert.Null(work.Work.Attempts[0].Target.Repository);
        Assert.Equal(AttemptStatus.Succeeded, work.Work.Attempts[0].Status);
        Assert.Contains("Clone", work.Work.Decisions[^1].Answer);
        await fixture.Apply(answer); // Lost response cannot restart the old text turn.
        Assert.Equal(2, (await fixture.Get(id)).Work.Attempts.Length);
        Assert.Equal(1, fixture.Runtime.Starts[original]);
    }

    [DatabaseFact]
    public async Task RuntimeRepositoryRequestRequiresSetupAndCleanupBeforeDispatch()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await EnableHandoffRepository(fixture);
        WorkView work = await fixture.StartWork();
        long id = work.Work.Id, attempt = work.Work.Attempts[^1].Id;
        fixture.Runtime.FailCleanup = true;
        fixture.Runtime.Observations[attempt] = new(ObservationKind.WorkspaceRequired, Text: "Inspect repository files");
        await fixture.Reconcile(id, attempt);
        work = await fixture.Until(id, x => x.Work.Attention?.Reason == AttentionReason.CleanupRequired);
        Assert.NotNull(work.Work.RepositoryRequest);
        await Assert.ThrowsAsync<WorkRuleException>(() => fixture.Apply(new(NextId(), id,
            WorkAction.AuthorizeRepository, work.Version, Repository: new("owner/repo", "Goblin", "agent@example.com"))));
        fixture.Runtime.FailCleanup = false;
        await fixture.Reconcile(id, attempt);
        work = await fixture.Until(id, x => x.Work.Attention?.Reason == AttentionReason.RepositoryRequired && !x.Work.Attempts[^1].CleanupPending);
        Assert.Single(work.Work.Attempts);
        Assert.Null(work.Work.Workspace);
        await fixture.Reconcile(id, attempt); // Observing the same outcome does not dispatch or rewrite the request.
        Assert.Equal(work.Version, (await fixture.Get(id)).Version);
        await fixture.Apply(new(NextId(), id, WorkAction.Cancel, work.Version));
        work = await fixture.Until(id, x => x.Work.Status == WorkStatus.Cancelled);
        Assert.Single(work.Work.Attempts);
        Assert.Null(work.Work.Workspace);
    }

    [DatabaseFact]
    public async Task ModelCatalogSurvivesRefreshFailureAndSelectedEffortBelongsToTheAttempt()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        var source = new CatalogSource();
        IDbContextFactory<GoblinDbContext> factory = fixture.Host.Services.GetRequiredService<IDbContextFactory<GoblinDbContext>>();
        var catalogs = new ModelCatalogStore(factory, source);
        await catalogs.GetAsync(1, 3, null);
        ModelCatalogView first = await UntilCatalog(catalogs, x => !x.Refreshing && x.Models.Length == 3);
        Assert.True(first.HasMore);
        Assert.Equal("gpt-test-0", first.DefaultModel);
        Assert.Equal(10, (await catalogs.GetAsync(1, 10, "gpt-test-11")).Models.Length);
        Assert.Equal("gpt-test-11", (await catalogs.GetAsync(1, 3, "gpt-test-11")).Models[0].Model);

        long id = NextId();
        WorkView created = await fixture.Apply(new(NextId(), id, WorkAction.Create, Text: "Use the selected model"));
        WorkView assigned = await fixture.Apply(new(NextId(), id, WorkAction.Assign, created.Version, AgentId: 1));
        WorkView queued = await fixture.Apply(new(NextId(), id, WorkAction.Execute, assigned.Version,
            Model: "gpt-test-1", ReasoningEffort: "high", ModelSelectionProvided: true));
        Assert.Equal("gpt-test-1", queued.Work.Attempts[^1].Target.RequestedModel);
        Assert.Equal("high", queued.Work.Attempts[^1].Target.RequestedEffort);
        Assert.Equal("high", (await fixture.Get(id)).Work.Attempts[^1].Target.RequestedEffort);

        long rejectedId = NextId();
        WorkView other = await fixture.Apply(new(NextId(), rejectedId, WorkAction.Create, Text: "Reject unsupported effort"));
        other = await fixture.Apply(new(NextId(), rejectedId, WorkAction.Assign, other.Version, AgentId: 1));
        ApplicationFailure rejected = await Assert.ThrowsAsync<ApplicationFailure>(() => fixture.Apply(new(
            NextId(), rejectedId, WorkAction.Execute, other.Version, Model: "gpt-test-1",
            ReasoningEffort: "unsupported", ModelSelectionProvided: true)));
        Assert.Equal("reasoning_effort_unavailable", rejected.Code);
        Assert.Empty((await fixture.Get(rejectedId)).Work.Attempts);

        source.Stamp = "v2";
        source.Fail = true;
        await catalogs.GetAsync(1, 3, null);
        ModelCatalogView stale = await UntilCatalog(catalogs, x => !x.Refreshing && x.Stale);
        Assert.Equal(3, stale.Models.Length);
        Assert.Equal(2, source.Calls);
        await catalogs.GetAsync(1, 3, null);
        Assert.Equal(2, source.Calls); // Failure cooldown prevents another upstream request.

        using IServiceScope scope = fixture.Host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WorkStore>().SetConnectionAsync(1, "Available", false,
            observeAccount: true, accountSignature: "new-account");
        ModelCatalogView changed = await catalogs.GetAsync(1, 3, null);
        Assert.Empty(changed.Models); // A different account never sees the old catalog.
    }

    private static async Task<ModelCatalogView> UntilCatalog(ModelCatalogStore catalogs, Func<ModelCatalogView, bool> ready)
    {
        for (int i = 0; i < 100; i++)
        {
            ModelCatalogView view = await catalogs.GetAsync(1, 3, null);
            if (ready(view)) return view;
            await Task.Delay(25);
        }
        throw new TimeoutException("The model catalog did not settle.");
    }

    private sealed class CatalogSource : IModelCatalogSource
    {
        private int _calls;
        public string Runtime => "codex";
        public string Stamp { get; set; } = "v1";
        public bool Fail { get; set; }
        public int Calls => Volatile.Read(ref _calls);
        public string ExecutableStamp() => Stamp;
        public Task<RuntimeModel[]> ListAsync(CancellationToken token)
        {
            Interlocked.Increment(ref _calls);
            if (Fail) throw new InvalidOperationException("private upstream failure");
            return Task.FromResult(Enumerable.Range(0, 12).Select(i => new RuntimeModel(
                $"id-{i}", $"gpt-test-{i}", $"Test model {i}", "medium", ["low", "medium", "high"], i == 0)).ToArray());
        }
    }

    [DatabaseFact]
    public async Task SetupMemorySurvivesRestartIsScopedAndRequiresTheCurrentSavedTurn()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Runtime.RepositoryExecution = true;
        fixture.Remote.Reconciled = true;
        using IServiceScope scope = fixture.Host.Services.CreateScope();
        GitHubStore settings = scope.ServiceProvider.GetRequiredService<GitHubStore>();
        await settings.ObserveAsync(new("first", "42", "owner"), "Connected");
        await settings.SetRepositoryAsync(new(22, "owner/repo", "main", true), true, "first");
        await settings.SetRepositoryAsync(new(23, "owner/other", "main", true), true, "first");
        WorkView work = await fixture.StartRepositoryWork("owner/repo");
        long attempt = work.Work.Attempts[^1].Id;
        var observation = new VerifiedRepositorySetup(new("python-tests", "Tests need Python", ["python 3.12"],
            ["install-python"], ["pyproject.toml"], [new("python --version", "Python 3.12")]),
            [new("pyproject.toml", new string('b', 64))], new string('c', 64));
        RepositorySetupStore memory = scope.ServiceProvider.GetRequiredService<RepositorySetupStore>();
        var request = new SetupMemoryWrite(1, long.MaxValue, "image-one", [observation]);
        await Assert.ThrowsAsync<ApplicationFailure>(() => memory.SaveAsync(attempt, request, default));
        Assert.Empty(await memory.ReadAsync(attempt, default));
        WorkspaceCheckpoint saved = await fixture.SaveCheckpoint(work.Work);
        request = request with { CheckpointId = saved.Id };
        await Assert.ThrowsAsync<ApplicationFailure>(() => memory.SaveAsync(attempt, request with
        { Observations = [observation with { Setup = observation.Setup with { Checks = [] } }] }, default));
        await Task.WhenAll(memory.SaveAsync(attempt, request, default), memory.SaveAsync(attempt, request, default));
        RepositorySetupMemory learned = Assert.Single(await memory.ReadAsync(attempt, default));
        Assert.Equal(work.Work.Id, learned.WorkId);
        Assert.Equal(saved.CommitSha, learned.Commit);
        await Assert.ThrowsAsync<ApplicationFailure>(() => memory.SaveAsync(attempt, request with { TurnNumber = 2 }, default));
        await Assert.ThrowsAsync<ApplicationFailure>(() => memory.SaveAsync(attempt, request with { Environment = "changed-image" }, default));
        await fixture.RestartAsync();
        using IServiceScope restarted = fixture.Host.Services.CreateScope();
        RepositorySetupStore reopened = restarted.ServiceProvider.GetRequiredService<RepositorySetupStore>();
        Assert.Equal(learned.Id, Assert.Single(await reopened.ReadAsync(attempt, default)).Id);
        fixture.Runtime.Observations[attempt] = new(ObservationKind.Result, Text: "Ready") { CheckpointId = saved.Id };
        await fixture.Reconcile(work.Work.Id, attempt);
        await Assert.ThrowsAsync<ApplicationFailure>(() => reopened.SaveAsync(attempt, request, default));
        WorkView next = await fixture.StartRepositoryWork("owner/repo");
        long nextAttempt = next.Work.Attempts[^1].Id;
        Assert.Equal(learned.Id, Assert.Single(await reopened.ReadAsync(nextAttempt, default)).Id);
        WorkspaceCheckpoint nextSaved = await fixture.SaveCheckpoint(next.Work);
        VerifiedRepositorySetup changed = observation with { ConfigurationHash = new string('d', 64) };
        await reopened.SaveAsync(nextAttempt, request with { CheckpointId = nextSaved.Id, Observations = [changed] }, default);
        Assert.Equal(2, (await reopened.ReadAsync(nextAttempt, default)).Length);
        fixture.Runtime.Observations[nextAttempt] = new(ObservationKind.Result, Text: "Ready") { CheckpointId = nextSaved.Id };
        await fixture.Reconcile(next.Work.Id, nextAttempt);
        WorkView other = await fixture.StartRepositoryWork("owner/other");
        long otherAttempt = other.Work.Attempts[^1].Id;
        Assert.Empty(await reopened.ReadAsync(otherAttempt, default));
        WorkspaceCheckpoint otherSaved = await fixture.SaveCheckpoint(other.Work);
        fixture.Runtime.Observations[otherAttempt] = new(ObservationKind.Result, Text: "Ready") { CheckpointId = otherSaved.Id };
        await fixture.Reconcile(other.Work.Id, otherAttempt);
        GitHubStore nextSettings = restarted.ServiceProvider.GetRequiredService<GitHubStore>();
        await nextSettings.ObserveAsync(new("second", "99", "someone-else"), "Connected");
        await nextSettings.SetRepositoryAsync(new(22, "owner/repo", "main", true), true, "second");
        WorkView otherAccount = await fixture.StartRepositoryWork("owner/repo");
        Assert.Empty(await reopened.ReadAsync(otherAccount.Work.Attempts[^1].Id, default));
    }

    [DatabaseFact]
    public async Task MultiTurnPauseRetainsAttemptAndRejectsStaleDispatchAfterRestart()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        WorkView work = await fixture.StartWork();
        long id = work.Work.Id, attempt = work.Work.Attempts.Single().Id;
        fixture.Runtime.Observations[attempt] = new(ObservationKind.Paused, Text: "Which database?") { ReleaseWorkspace = true };
        await fixture.Reconcile(id, attempt);
        work = await fixture.Until(id, x => x.Work.Attempts[^1].Status == AttemptStatus.Waiting && !x.Work.Attempts[^1].CleanupPending);
        long decision = work.Work.Decisions.Single().Id;
        await fixture.Apply(new(NextId(), id, WorkAction.Answer, work.Version, Text: "PostgreSQL", DecisionId: decision));
        work = await fixture.Until(id, x => x.Work.Attempts[^1].TurnNumber == 2 && x.Work.Attempts[^1].Status == AttemptStatus.Starting);
        Assert.Single(work.Work.Attempts);
        Assert.Single(work.Work.Attempts[0].PriorTurns);
        await fixture.Host.Services.GetRequiredService<ExecutionCoordinator>().DispatchAsync(new(id, attempt, 1), default);
        Assert.Equal(2, fixture.Runtime.Starts[attempt]);
        await fixture.RestartAsync();
        await fixture.Reconcile(id, attempt);
        Assert.Equal(AttemptStatus.Starting, (await fixture.Get(id)).Work.Attempts[0].Status);
        fixture.Runtime.Observations[attempt] = new(ObservationKind.Result, Text: "Ready") { TurnNumber = 2 };
        await fixture.Reconcile(id, attempt);
        Assert.Equal(AttentionReason.ResultReview, (await fixture.Get(id)).Work.Attention!.Reason);
        Assert.Equal(2, fixture.Runtime.Starts[attempt]);
    }

    [DatabaseFact]
    public async Task InspectionMessagesStartAndStopThroughTheDurableQueue()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        WorkView work = await fixture.StartWork();
        using IServiceScope scope = fixture.Host.Services.CreateScope();
        IDbContextFactory<GoblinDbContext> factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<GoblinDbContext>>();
        await using GoblinDbContext db = await factory.CreateDbContextAsync();
        long id = NextId();
        db.WorkspaceSessions.Add(new()
        {
            Id = id,
            WorkId = work.Work.Id,
            AttemptId = work.Work.Attempts[0].Id,
            State = "Queued",
            SourceVolume = $"k8s/agents/work-{work.Work.Id}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        await fixture.Host.Services.GetRequiredService<IMessageBus>().PublishAsync(new StartInspection(id));
        for (int i = 0; i < 100 && !fixture.Inspection.Started.ContainsKey(id); i++) await Task.Delay(50);
        Assert.True(fixture.Inspection.Started.ContainsKey(id));
        InspectionStore store = scope.ServiceProvider.GetRequiredService<InspectionStore>();
        await store.ObserveAsync(id, "Running", default);
        await store.StopAsync(work.Work.Id, id, default);
        for (int i = 0; i < 100 && (await store.ListAsync(work.Work.Id, default)).Single().State != "Stopped"; i++) await Task.Delay(50);
        Assert.Equal("Stopped", (await store.ListAsync(work.Work.Id, default)).Single().State);
    }

    [DatabaseFact]
    public async Task PausedWorkspaceFailureRequiresAttentionWithoutDispatchingAgain()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        WorkView work = await fixture.StartWork();
        long id = work.Work.Id, attempt = work.Work.Attempts.Single().Id;
        fixture.Runtime.Observations[attempt] = new(ObservationKind.Paused, Text: "Continue?") { ReleaseWorkspace = false };
        await fixture.Reconcile(id, attempt);
        await fixture.Until(id, x => x.Work.Attempts[0].Status == AttemptStatus.Waiting);
        await fixture.Host.Services.GetRequiredService<IDispatchFailureJournal>()
            .RecordAsync(new(id, attempt, FailureKind.StorageUnavailable), default);
        await fixture.Host.Services.GetRequiredService<ExecutionCoordinator>().DrainFailuresAsync(default);
        work = await fixture.Get(id);
        Assert.Equal(AttentionReason.UncertainExecution, work.Work.Attention!.Reason);
        Assert.Equal(FailureKind.StorageUnavailable, work.Work.Attention.Failure);
        Assert.Equal(1, fixture.Runtime.Starts[attempt]);
    }

    [DatabaseFact]
    public async Task QueuedContinuationKeepsItsExistingWorkspaceReservation()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        WorkView work = await fixture.StartWork();
        long id = work.Work.Id, attempt = work.Work.Attempts.Single().Id;
        fixture.Runtime.Observations[attempt] = new(ObservationKind.Paused, Text: "Continue investigating?") { ReleaseWorkspace = false };
        await fixture.Reconcile(id, attempt);
        work = await fixture.Until(id, x => x.Work.Attempts[^1].Status == AttemptStatus.Waiting);
        using IServiceScope scope = fixture.Host.Services.CreateScope();
        IDbContextFactory<GoblinDbContext> factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<GoblinDbContext>>();
        await using GoblinDbContext db = await factory.CreateDbContextAsync();
        // Hold the dispatch queue while checking the durable admission reservation.
        await db.Connections.Where(x => x.Id == work.Work.Attempts[0].Target.ConnectionId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Availability, "Verifying"));
        work = await fixture.Apply(new(NextId(), id, WorkAction.Answer, work.Version, Text: "Yes", DecisionId: work.Work.Decisions.Single().Id));
        Assert.Equal(AttemptStatus.Queued, work.Work.Attempts[0].Status);
        Assert.True((await db.ExecutionAttempts.AsNoTracking().SingleAsync(x => x.Id == attempt)).WorkspaceRetained);
    }

    [DatabaseFact]
    public async Task GitCheckpointMetadataSurvivesRestartWithoutAnyFileBytes()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Runtime.RepositoryExecution = true;
        fixture.Remote.Reconciled = true;
        using IServiceScope scope = fixture.Host.Services.CreateScope();
        GitHubStore settings = scope.ServiceProvider.GetRequiredService<GitHubStore>();
        await settings.ObserveAsync(new("first", "42", "owner"), "Connected");
        await settings.SetRepositoryAsync(new(22, "owner/repo", "main", true), true, "first");
        long id = NextId();
        WorkView work = await fixture.Apply(new(NextId(), id, WorkAction.Create, Text: "Checkpoint test"));
        work = await fixture.Apply(new(NextId(), id, WorkAction.Assign, work.Version, AgentId: 1));
        await fixture.Apply(new(NextId(), id, WorkAction.Execute, work.Version, Repository: new("owner/repo", "Goblin", "agent@example.com")));
        work = await fixture.Until(id, x => x.Work.Attempts[^1].Status == AttemptStatus.Starting);
        long attempt = work.Work.Attempts[^1].Id;
        RepositoryBroker broker = fixture.Host.Services.GetRequiredService<RepositoryBroker>();
        await broker.PrepareAsync(work.Work, default);
        long publication = await broker.ReserveOperationIdAsync(attempt, default);
        await broker.EnqueueAsync(attempt, publication, "publish", new MemoryStream([1, 2, 3]), default);
        for (int i = 0; i < 100 && (await broker.StatusAsync(attempt, publication, default)).State != "Succeeded"; i++) await Task.Delay(50);
        Assert.Equal("Succeeded", (await broker.StatusAsync(attempt, publication, default)).State);
        IDbContextFactory<GoblinDbContext> factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<GoblinDbContext>>();
        var store = new WorkspaceCheckpoints(factory);
        WorkspaceCheckpoint saved = await store.SaveAsync(attempt, 1, new string('a', 40), broker, default);
        Assert.Equal(saved.Id, (await store.LatestAsync(id, "owner/repo", default))!.Id);
        Assert.Equal(saved.Id, (await store.SaveAsync(attempt, 1, new string('a', 40), broker, default)).Id);
        await using (GoblinDbContext db = await factory.CreateDbContextAsync())
            Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_schema='public' AND table_name='workspace_checkpoints' AND data_type='bytea'").SingleAsync());
        Assert.True(await store.VerifiedAsync(saved.Id, attempt, 1, default));
        Assert.False(await store.VerifiedAsync(saved.Id, attempt, 2, default));
        await fixture.RestartAsync();
        using IServiceScope restartedScope = fixture.Host.Services.CreateScope();
        var reopened = new WorkspaceCheckpoints(restartedScope.ServiceProvider.GetRequiredService<IDbContextFactory<GoblinDbContext>>());
        Assert.Equal(saved.Id, (await reopened.LatestAsync(id, "owner/repo", default))!.Id);
        Assert.Equal(work.Work.Workspace, (await fixture.Get(id)).Work.Workspace);
        fixture.Runtime.Observations[attempt] = new(ObservationKind.Result, Text: "Saved result") { CheckpointId = saved.Id };
        await fixture.Reconcile(id, attempt);
        work = await fixture.Until(id, x => x.Work.Attention?.Reason == AttentionReason.ResultReview && !x.Work.Attempts[^1].CleanupPending);
        await fixture.Apply(new(NextId(), id, WorkAction.Approve, work.Version, AttemptId: attempt));
    }

    [DatabaseFact]
    public async Task PersistentWorkspaceAndAttemptHistorySurviveFailureAndExplicitRetry()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Runtime.RepositoryExecution = true;
        using IServiceScope scope = fixture.Host.Services.CreateScope();
        GitHubStore settings = scope.ServiceProvider.GetRequiredService<GitHubStore>();
        await settings.ObserveAsync(new("first", "42", "owner"), "Connected");
        await settings.SetRepositoryAsync(new(22, "owner/repo", "main", true), true, "first");
        long id = NextId();
        WorkView work = await fixture.Apply(new(NextId(), id, WorkAction.Create, Text: "Continue surviving edits"));
        work = await fixture.Apply(new(NextId(), id, WorkAction.Assign, work.Version, AgentId: 1));
        await fixture.Apply(new(NextId(), id, WorkAction.Execute, work.Version, Repository: new("owner/repo", "Goblin", "agent@example.com")));
        work = await fixture.Until(id, x => x.Work.Attempts[^1].Status == AttemptStatus.Starting);
        long first = work.Work.Attempts[^1].Id;
        string reference = work.Work.Workspace!.EnvironmentReference;
        fixture.Runtime.FailCleanup = true;
        fixture.Runtime.Observations[first] = new(ObservationKind.Failed, Failure: FailureKind.RuntimeDisconnected);
        await fixture.Reconcile(id, first);
        work = await fixture.Until(id, x => x.Work.Attention?.Reason == AttentionReason.CleanupRequired);
        await Assert.ThrowsAsync<WorkRuleException>(() => fixture.Apply(new(NextId(), id, WorkAction.Retry, work.Version)));
        fixture.Runtime.FailCleanup = false;
        await fixture.Reconcile(id, first);
        work = await fixture.Until(id, x => x.Work.Attention?.Reason == AttentionReason.Failure && !x.Work.Attempts[^1].CleanupPending);
        await fixture.RestartAsync();
        work = await fixture.Get(id);
        Assert.Equal(reference, work.Work.Workspace!.EnvironmentReference);
        await fixture.Apply(new(NextId(), id, WorkAction.Retry, work.Version));
        work = await fixture.Until(id, x => x.Work.Attempts.Length == 2 && x.Work.Attempts[^1].Status == AttemptStatus.Starting);
        Assert.Equal(reference, work.Work.Workspace!.EnvironmentReference);
        Assert.Equal(reference, work.Work.Attempts[^1].EnvironmentReference);
        Assert.NotEqual(first, work.Work.Workspace.AttemptId);
        Assert.Equal(AttemptStatus.Failed, work.Work.Attempts[0].Status);
        Assert.NotEqual(work.Work.Attempts[0].Target.Repository!.Grant!.Branch, work.Work.Attempts[1].Target.Repository!.Grant!.Branch);
    }

    [DatabaseFact]
    public async Task RepositoryAccountAndBranchArePinnedAndChangesWaitForReconciliation()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Runtime.RepositoryExecution = true;
        using IServiceScope scope = fixture.Host.Services.CreateScope();
        GitHubStore settings = scope.ServiceProvider.GetRequiredService<GitHubStore>();
        await settings.ObserveAsync(new("first", "42", "owner"), "Connected");
        await settings.SetRepositoryAsync(new(22, "owner/repo", "main", true), true, "first");
        long id = NextId();
        WorkView w = await fixture.Apply(new(NextId(), id, WorkAction.Create, Text: "Update repository"));
        w = await fixture.Apply(new(NextId(), id, WorkAction.Assign, w.Version, AgentId: 1));
        w = await fixture.Apply(new(NextId(), id, WorkAction.Execute, w.Version, Repository: new("owner/repo", "Goblin", "goblin@example.test")));
        long attempt = w.Work.Attempts[^1].Id;
        RepositoryGrant original = w.Work.Attempts[^1].Target.Repository!.Grant!;
        Assert.Equal($"goblin/{id}/{attempt}", original.Branch);
        Assert.Equal("first", original.Generation);
        await Assert.ThrowsAsync<ApplicationFailure>(() => settings.BeginChangeAsync());
        await fixture.Until(id, x => x.Work.Attempts[^1].Status == AttemptStatus.Starting);
        fixture.Runtime.Observations[attempt] = new(ObservationKind.Uncertain, Failure: FailureKind.RuntimeDisconnected);
        await fixture.Reconcile(id, attempt);
        await Assert.ThrowsAsync<ApplicationFailure>(() => settings.SetRepositoryAsync(new(22, "owner/repo", "main", true), false, "first"));
        fixture.Runtime.Observations[attempt] = new(ObservationKind.Stopped);
        await fixture.Reconcile(id, attempt);
        await settings.BeginChangeAsync();
        await settings.ObserveAsync(new("second", "43", "another-owner"), "Connected");
        Assert.Equal(original, (await fixture.Get(id)).Work.Attempts[^1].Target.Repository!.Grant);
        Assert.False((await settings.RepositoriesAsync()).Single().Enabled);
    }

    [DatabaseFact]
    public async Task RepositoryPublicationIsDurableDeduplicatedAndReconciledWithoutReplay()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Runtime.RepositoryExecution = true;
        using IServiceScope scope = fixture.Host.Services.CreateScope();
        GitHubStore settings = scope.ServiceProvider.GetRequiredService<GitHubStore>();
        await settings.ObserveAsync(new("first", "42", "owner"), "Connected");
        await settings.SetRepositoryAsync(new(22, "owner/repo", "main", true), true, "first");
        long id = NextId();
        WorkView w = await fixture.Apply(new(NextId(), id, WorkAction.Create, Text: "Publish repository"));
        w = await fixture.Apply(new(NextId(), id, WorkAction.Assign, w.Version, AgentId: 1));
        await fixture.Apply(new(NextId(), id, WorkAction.Execute, w.Version, Repository: new("owner/repo", "Goblin", "goblin@example.test")));
        w = await fixture.Until(id, x => x.Work.Attempts[^1].Status == AttemptStatus.Starting);
        RepositoryBroker broker = fixture.Host.Services.GetRequiredService<RepositoryBroker>();
        string capability = await broker.PrepareAsync(w.Work, default);
        long attempt = w.Work.Attempts[^1].Id;
        await broker.AuthorizeAsync(attempt, capability, true, default);
        await Assert.ThrowsAsync<ApplicationFailure>(() => broker.AuthorizeAsync(attempt, "00", true, default));
        long operation = await broker.ReserveOperationIdAsync(attempt, default);
        fixture.Remote.Fail = true;
        await broker.EnqueueAsync(attempt, operation, "publish", new MemoryStream([1, 2, 3]), default);
        await broker.EnqueueAsync(attempt, operation, "publish", new MemoryStream([1, 2, 3]), default);
        for (int i = 0; i < 100 && (await broker.StatusAsync(attempt, operation, default)).State is "Queued" or "Running"; i++) await Task.Delay(50);
        if ((await broker.StatusAsync(attempt, operation, default)).State == "Queued")
        {
            await using GoblinDbContext db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<GoblinDbContext>>().CreateDbContextAsync();
            string[] failures = await db.Database.SqlQueryRaw<string>("SELECT exception_message AS \"Value\" FROM public.wolverine_dead_letters").ToArrayAsync();
            Assert.Fail(string.Join("\n", failures));
        }
        Assert.Equal("Uncertain", (await broker.StatusAsync(attempt, operation, default)).State);
        Assert.Equal(1, fixture.Remote.Calls);
        Assert.Equal(ObservationKind.Uncertain, (await broker.ObserveAsync(w.Work, default))!.Kind);
        await broker.ExecuteAsync(operation, default);
        Assert.Equal(1, fixture.Remote.Calls);
        fixture.Remote.Reconciled = true;
        Assert.Null(await broker.ObserveAsync(w.Work, default));
        Assert.Equal("Succeeded", (await broker.StatusAsync(attempt, operation, default)).State);
        await Assert.ThrowsAsync<ApplicationFailure>(() => broker.EnqueueAsync(attempt, operation, "publish", new MemoryStream([4]), default));
        Assert.Equal(1, fixture.Remote.Calls);
    }

    [DatabaseFact]
    public async Task ReservedIdsStayUniqueAcrossConcurrentRequestsAndBeyondJavaScriptPrecision()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        using IServiceScope scope = fixture.Host.Services.CreateScope();
        IdentityStore ids = scope.ServiceProvider.GetRequiredService<IdentityStore>();
        await using (GoblinDbContext db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<GoblinDbContext>>().CreateDbContextAsync())
            await db.Database.ExecuteSqlRawAsync("SELECT setval('public.work_items_id_seq', 9007199254740993, false)");
        ReservedIdentities[] reservations = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => ids.ReserveAsync(new([IdentityKind.Work]))));
        long[] values = [.. reservations.Select(x => x.Ids.Single())];
        Assert.Equal(16, values.Distinct().Count());
        Assert.All(values, id => Assert.True(id > 9007199254740992L));
        long commandId = (await ids.ReserveAsync(new([IdentityKind.Command]))).Ids.Single();
        var create = new WorkCommand(commandId, values[0], WorkAction.Create, Text: "Exact database identity");
        WorkView saved = await fixture.Apply(create);
        Assert.Equal(values[0], saved.Work.Id);
        Assert.Equal(saved.Version, (await fixture.Apply(create)).Version);
        Assert.Equal(saved.Work.Id, (await fixture.Get(saved.Work.Id)).Work.Id);
        await Assert.ThrowsAsync<ApplicationFailure>(() => ids.ReserveAsync(new([IdentityKind.Event])));
    }

    [DatabaseFact]
    public async Task ConversationAndWorkCommandsUseDistinctCoreContextIdentities()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        using IServiceScope scope = fixture.Host.Services.CreateScope();
        IdentityStore ids = scope.ServiceProvider.GetRequiredService<IdentityStore>();
        ConversationStore conversations = scope.ServiceProvider.GetRequiredService<ConversationStore>();
        long[] first = (await ids.ReserveAsync(new([IdentityKind.Conversation, IdentityKind.Message, IdentityKind.Work]))).Ids;
        await conversations.ApplyAsync(new(first[0], first[1], "Track the release", first[2]));
        long messageId = (await ids.ReserveAsync(new([IdentityKind.Message]))).Ids.Single();
        long commandId = (await ids.ReserveAsync(new([IdentityKind.Command, IdentityKind.Command]))).Ids[^1];
        Assert.Equal(messageId, commandId); // Separate table sequences can produce the same number.
        await conversations.ApplyAsync(new(first[0], messageId, "Conversation context"));
        WorkView work = await fixture.Get(first[2]);
        var command = new WorkCommand(commandId, first[2], WorkAction.AddContext, work.Version, "Work context");
        WorkView updated = await fixture.Apply(command);
        Assert.Equal(new[] { "Conversation context", "Work context" }, updated.Work.Messages.Select(x => x.Text));
        Assert.Equal(2, updated.Work.Messages.Select(x => x.Id).Distinct().Count());
        Assert.Equal(updated.Version, (await fixture.Apply(command)).Version);
        await conversations.ApplyAsync(new(first[0], messageId, "Conversation context"));
        Assert.Equal(2, (await fixture.Get(first[2])).Work.Messages.Length);
    }

    [DatabaseFact]
    public async Task RepeatedCommandsConcurrentClaimsAndApprovalSurviveNewScopes()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        long id = NextId();
        var create = new WorkCommand(NextId(), id, WorkAction.Create, Text: "Produce a reviewable answer");
        WorkView[] created = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => fixture.Apply(create)));
        Assert.All(created, x => Assert.Equal(1, x.Version));
        Assert.Single((await fixture.Get(id)).Work.History);
        WorkView assigned = await fixture.Apply(new(NextId(), id, WorkAction.Assign, 1, AgentId: WorkStore.DefaultAgentId));
        WorkView queued = await fixture.Apply(new(NextId(), id, WorkAction.Execute, assigned.Version));
        long attempt = queued.Work.Attempts.Single().Id;
        await fixture.Until(id, x => x.Work.Attempts[^1].Status == AttemptStatus.Starting);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => fixture.Host.Services.GetRequiredService<ExecutionCoordinator>().DispatchAsync(new(id, attempt), CancellationToken.None)));
        Assert.Equal(1, fixture.Runtime.Starts[attempt]);
        fixture.Runtime.Observations[attempt] = new(ObservationKind.Result, new("test-model", "opaque-session", "opaque-operation"), "Reviewed outcome");
        await fixture.Host.Services.GetRequiredService<ExecutionCoordinator>().ReconcileAsync(new(id, attempt), CancellationToken.None);
        WorkView review = await fixture.Get(id);
        Assert.Equal(AttentionReason.ResultReview, review.Work.Attention!.Reason);
        var approve = new WorkCommand(NextId(), id, WorkAction.Approve, review.Version, AttemptId: attempt);
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
        await using Fixture fixture = await Fixture.CreateAsync();
        WorkView running = await fixture.StartWork();
        long id = running.Work.Id, attempt = running.Work.Attempts[^1].Id;
        fixture.Runtime.Observations[attempt] = new(ObservationKind.Uncertain, Failure: FailureKind.RuntimeDisconnected);
        await fixture.Reconcile(id, attempt);
        WorkView uncertain = await fixture.Get(id);
        await Assert.ThrowsAsync<WorkRuleException>(() => fixture.Apply(new(NextId(), id, WorkAction.Retry, uncertain.Version)));
        using (IServiceScope scope = fixture.Host.Services.CreateScope())
            await Assert.ThrowsAsync<ApplicationFailure>(() => scope.ServiceProvider.GetRequiredService<WorkStore>().SetConnectionAsync(WorkStore.DefaultAgentId, "Changing", true));
        await fixture.RestartAsync();
        Assert.Equal(1, fixture.Runtime.Starts[attempt]);
        fixture.Runtime.Observations[attempt] = new(ObservationKind.Stopped);
        await fixture.Reconcile(id, attempt);
        WorkView failed = await fixture.Get(id);
        Assert.Equal(AttentionReason.Failure, failed.Work.Attention!.Reason);
        Assert.Single(fixture.Runtime.Starts);
        WorkView retried = await fixture.Apply(new(NextId(), id, WorkAction.Retry, failed.Version));
        Assert.Equal(2, retried.Work.Attempts.Length);
        Assert.NotEqual(attempt, retried.Work.Attempts[^1].Id);
    }

    [DatabaseFact]
    public async Task InvalidCommandRollsBackStateReceiptAndDispatch()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        long id = NextId(), rejected = NextId();
        await fixture.Apply(new(NextId(), id, WorkAction.Create, Text: "No agent assigned"));
        await Assert.ThrowsAsync<ApplicationFailure>(() => fixture.Apply(new(rejected, id, WorkAction.Execute, 1)));
        using IServiceScope scope = fixture.Host.Services.CreateScope();
        await using GoblinDbContext db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<GoblinDbContext>>().CreateDbContextAsync();
        Assert.False(await db.WorkCommands.AnyAsync(x => x.Id == rejected));
        Assert.False(await db.ExecutionAttempts.AnyAsync(x => x.WorkId == id));
        Assert.Equal(1, (await fixture.Get(id)).Version);
        Assert.Empty(fixture.Runtime.Starts);
        await Assert.ThrowsAsync<ApplicationFailure>(() => fixture.Apply(new(NextId(), id, WorkAction.Assign, 99, AgentId: WorkStore.DefaultAgentId)));
    }

    [DatabaseFact]
    public async Task ReusedWorkStoreIsolatesConcurrentCommandsAndFailedDispatchTransactions()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        using IServiceScope scope = fixture.Host.Services.CreateScope();
        WorkStore store = scope.ServiceProvider.GetRequiredService<WorkStore>();
        long id = NextId(), rejected = NextId();
        var create = new WorkCommand(NextId(), id, WorkAction.Create, Text: "Keep each operation independent");
        WorkView[] created = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.ApplyAsync(create)));
        Assert.All(created, x => Assert.Equal(1, x.Version));
        WorkView assigned = await store.ApplyAsync(new(NextId(), id, WorkAction.Assign, 1, AgentId: WorkStore.DefaultAgentId));

        // Fail the database write after dispatch has been published into the
        // transaction, rather than rejecting the command before publishing.
        await fixture.RejectId("work_commands", rejected);
        await Assert.ThrowsAsync<DbUpdateException>(() => store.ApplyAsync(new(rejected, id, WorkAction.Execute, assigned.Version)));
        await using (GoblinDbContext db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<GoblinDbContext>>().CreateDbContextAsync())
        {
            Assert.False(await db.WorkCommands.AnyAsync(x => x.Id == rejected));
            Assert.False(await db.ExecutionAttempts.AnyAsync(x => x.WorkId == id));
            Assert.Equal(0, await db.Database.SqlQueryRaw<int>("""
                SELECT count(*)::int AS "Value" FROM public.wolverine_incoming_envelopes
                WHERE message_type LIKE '%DispatchWork%'
                """).SingleAsync());
        }
        Assert.Equal(assigned.Version, (await store.GetAsync(id)).Version);
        Assert.Empty(fixture.Runtime.Starts);

        WorkView queued = await store.ApplyAsync(new(NextId(), id, WorkAction.Execute, assigned.Version));
        long attempt = Assert.Single(queued.Work.Attempts).Id;
        await fixture.Until(id, x => x.Work.Attempts[^1].Status == AttemptStatus.Starting);
        Assert.Equal(1, fixture.Runtime.Starts[attempt]);
        Assert.Single((await fixture.Get(id)).Work.Attempts);
    }

    [DatabaseFact]
    public async Task ReusedConversationStoreRollsBackBothSavesBeforeTrackingWorkAgain()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        using IServiceScope scope = fixture.Host.Services.CreateScope();
        ConversationStore store = scope.ServiceProvider.GetRequiredService<ConversationStore>();
        long conversationId = NextId(), messageId = NextId(), rejectedWork = NextId();
        await fixture.RejectId("work_items", rejectedWork);
        await Assert.ThrowsAsync<DbUpdateException>(() => store.ApplyAsync(new(conversationId, messageId, "Track this conversation", rejectedWork)));
        await using (GoblinDbContext db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<GoblinDbContext>>().CreateDbContextAsync())
        {
            Assert.False(await db.Conversations.AnyAsync(x => x.Id == conversationId));
            Assert.False(await db.ConversationMessages.AnyAsync(x => x.Id == messageId));
            Assert.False(await db.WorkItems.AnyAsync(x => x.Id == rejectedWork));
        }

        long workId = NextId();
        var command = new ConversationCommand(conversationId, messageId, "Track this conversation", workId);
        ConversationView tracked = await store.ApplyAsync(command);
        Assert.Equal(workId, tracked.WorkId);
        Assert.Single(tracked.Messages);
        Assert.Single((await store.ApplyAsync(command)).Messages);
        await store.ApplyAsync(new(conversationId, NextId(), "Additional context"));
        Assert.Equal(2, Assert.Single(await store.ListAsync()).Messages.Length);
        WorkView work = await fixture.Get(workId);
        Assert.Equal(2, work.Version);
        Assert.Equal("Track this conversation", work.Work.Objective);
    }

    [DatabaseFact]
    public async Task CancellationWaitsForStoppingEvidenceAndFailureIsNeverRetried()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        WorkView running = await fixture.StartWork();
        long id = running.Work.Id, attempt = running.Work.Attempts[^1].Id;
        await fixture.Apply(new(NextId(), id, WorkAction.Cancel, running.Version));
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
        await using Fixture fixture = await Fixture.CreateAsync();
        using (IServiceScope scope = fixture.Host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<WorkStore>().SetConnectionAsync(WorkStore.DefaultAgentId, "Disconnected", false);
        long id = NextId();
        WorkView w = await fixture.Apply(new(NextId(), id, WorkAction.Create, Text: "Unavailable connection"));
        w = await fixture.Apply(new(NextId(), id, WorkAction.Assign, w.Version, AgentId: WorkStore.DefaultAgentId));
        w = await fixture.Apply(new(NextId(), id, WorkAction.Execute, w.Version));
        await fixture.Until(id, x => x.Work.Attention?.Reason == AttentionReason.Failure);
        Assert.Empty(fixture.Runtime.Starts);
        await fixture.RestartAsync();
        Assert.Equal(AttentionReason.Failure, (await fixture.Get(id)).Work.Attention!.Reason);
        Assert.Empty(fixture.Runtime.Starts);
    }

    [DatabaseFact]
    public async Task CleanupFailureSurvivesRestartAndRequiresReconciliationBeforeApproval()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        WorkView running = await fixture.StartWork();
        long id = running.Work.Id, attempt = running.Work.Attempts[^1].Id;
        fixture.Runtime.FailCleanup = true;
        fixture.Runtime.Observations[attempt] = new(ObservationKind.Result, Text: "Saved before cleanup");
        await fixture.Reconcile(id, attempt);
        WorkView blocked = await fixture.Get(id);
        Assert.Equal(AttentionReason.CleanupRequired, blocked.Work.Attention!.Reason);
        Assert.Single(blocked.Work.Results);
        await Assert.ThrowsAsync<WorkRuleException>(() => fixture.Apply(new(NextId(), id, WorkAction.Approve, blocked.Version, AttemptId: attempt)));
        await fixture.RestartAsync();
        fixture.Runtime.FailCleanup = false;
        Assert.Equal(AttentionReason.CleanupRequired, (await fixture.Get(id)).Work.Attention!.Reason);
        await fixture.Reconcile(id, attempt);
        WorkView review = await fixture.Get(id);
        Assert.Equal(AttentionReason.ResultReview, review.Work.Attention!.Reason);
        Assert.False(review.Work.Attempts[^1].CleanupPending);
        await fixture.Apply(new(NextId(), id, WorkAction.Approve, review.Version, AttemptId: attempt));
        Assert.Equal(1, fixture.Runtime.Starts[attempt]);
    }

    [DatabaseFact]
    public async Task VerificationReservationsPreventDispatchAndPreserveWaitingAccountChanges()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        using IServiceScope scope = fixture.Host.Services.CreateScope();
        WorkStore store = scope.ServiceProvider.GetRequiredService<WorkStore>();
        await store.BeginVerificationAsync(WorkStore.DefaultAgentId);
        await Assert.ThrowsAsync<ApplicationFailure>(() => store.BeginVerificationAsync(WorkStore.DefaultAgentId));
        long id = NextId();
        WorkView work = await fixture.Apply(new(NextId(), id, WorkAction.Create, Text: "Wait for verification"));
        work = await fixture.Apply(new(NextId(), id, WorkAction.Assign, work.Version, AgentId: WorkStore.DefaultAgentId));
        work = await fixture.Apply(new(NextId(), id, WorkAction.Execute, work.Version));
        ExecutionCoordinator coordinator = fixture.Host.Services.GetRequiredService<ExecutionCoordinator>();
        long attempt = work.Work.Attempts[^1].Id;
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
        await using Fixture fixture = await Fixture.CreateAsync();
        WorkView running = await fixture.StartWork();
        long id = running.Work.Id, attempt = running.Work.Attempts[^1].Id;
        await fixture.SetDatabaseAvailable(false);
        try { await Assert.ThrowsAnyAsync<Exception>(() => fixture.Reconcile(id, attempt)); }
        finally { await fixture.SetDatabaseAvailable(true); }
        Assert.Contains(await fixture.Host.Services.GetRequiredService<IDispatchFailureJournal>().ReadAsync(default),
            x => x.AttemptId == attempt && x.Failure == FailureKind.StorageUnavailable);
        await fixture.RestartAsync();
        WorkView uncertain = await fixture.Until(id, x => x.Work.Attention?.Reason == AttentionReason.UncertainExecution);
        Assert.Equal(FailureKind.StorageUnavailable, uncertain.Work.Attention!.Failure);
        await Assert.ThrowsAsync<WorkRuleException>(() => fixture.Apply(new(NextId(), id, WorkAction.Retry, uncertain.Version)));
        Assert.Equal(1, fixture.Runtime.Starts[attempt]);
    }

    private sealed class RepositoryRemote : IRepositoryRemote
    {
        public bool Fail { get; set; }
        public bool Reconciled { get; set; }
        public int Calls { get; private set; }
        public Task PrepareAsync(RepositoryChange repository, string directory, string? checkpoint, CancellationToken token) { Directory.CreateDirectory(directory); return Task.CompletedTask; }
        public Task<string> InspectBundleAsync(RepositoryChange repository, string directory, string bundle, CancellationToken token) => Task.FromResult(new string('a', 40));
        public Task<RepositoryOperationResult> ExecuteAsync(RepositoryChange repository, string directory, string operation, string commit, CancellationToken token)
        {
            Calls++;
            if (Fail) throw new IOException("Lost upstream response");
            return Task.FromResult(new RepositoryOperationResult(commit, "https://github.com/owner/repo/tree/" + repository.Grant!.Branch));
        }
        public Task<RepositoryOperationResult?> ReconcileAsync(RepositoryChange repository, string directory, string operation, string commit, CancellationToken token) =>
            Task.FromResult(Reconciled ? new RepositoryOperationResult(commit, "https://github.com/owner/repo/tree/" + repository.Grant!.Branch) : null);
    }

    private sealed class Runtime : IExecutionHost
    {
        public ConcurrentDictionary<long, int> Starts { get; } = new();
        public ConcurrentDictionary<long, ExecutionObservation> Observations { get; } = new();
        public bool FailCleanup { get; set; }
        public bool RepositoryExecution { get; set; }
        public RuntimeCapabilities[] Capabilities => [new("codex", true, RepositoryExecution, true, false, false)];
        public string EnvironmentFor(long work, long attempt) => "test/" + attempt;
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

    private sealed class InspectionRuntime : IInspectionHost
    {
        public ConcurrentDictionary<long, bool> Started { get; } = new();
        public Task StartAsync(InspectionAllocation session, CancellationToken token)
        { Started[session.Id] = true; return Task.CompletedTask; }
        public Task StopAsync(InspectionAllocation session, CancellationToken token)
        { Started[session.Id] = false; return Task.CompletedTask; }
        public Task<string> ObserveAsync(InspectionAllocation session, CancellationToken token) =>
            Task.FromResult(Started.GetValueOrDefault(session.Id) ? "Running" : "Missing");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _app;
        private readonly string _name;
        private readonly string _directory;

        public Fixture(string app, string name, string directory)
        {
            _app = app;
            _name = name;
            _directory = directory;
        }

        public Runtime Runtime { get; } = new();
        public RepositoryRemote Remote { get; } = new();
        public InspectionRuntime Inspection { get; } = new();
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
            using IServiceScope scope = fixture.Host.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<WorkStore>().SetConnectionAsync(WorkStore.DefaultAgentId, "Available", false);
            return fixture;
        }
        private async Task StartAsync()
        {
            HostApplicationBuilder builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddGoblinPersistence(_app);
            builder.Services.AddWorkApplication();
            builder.Services.AddSingleton<IExecutionHost>(Runtime);
            builder.Services.AddSingleton(new WorkspaceLimits());
            builder.Services.AddSingleton<IInspectionHost>(Inspection);
            builder.Services.AddSingleton<InspectionCoordinator>();
            builder.Services.AddSingleton<IRepositoryRemote>(Remote);
            builder.Services.AddSingleton(new RepositoryBrokerOptions(Path.Combine(_directory, "repositories")));
            builder.Services.AddSingleton<RepositoryBroker>();
            builder.Services.AddSingleton<IDispatchFailureJournal>(new FileDispatchFailureJournal(_directory));
            builder.UseWolverine(o => ApplicationServices.ConfigureMessaging(o, _app, inspectionEnabled: true));
            Host = builder.Build();
            await Host.StartAsync();
        }
        public async Task RestartAsync() { await Host.StopAsync(); Host.Dispose(); await StartAsync(); }
        public async Task RejectId(string table, long id)
        {
            var admin = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("GOBLIN_TEST_POSTGRES_ADMIN")) { Database = _name };
            await using var connection = new NpgsqlConnection(admin.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"ALTER TABLE public.{table} ADD CONSTRAINT rejected_test_id CHECK (id <> '{id}')", connection);
            await command.ExecuteNonQueryAsync();
        }
        public async Task SetDatabaseAvailable(bool available)
        {
            await using var admin = new NpgsqlConnection(Environment.GetEnvironmentVariable("GOBLIN_TEST_POSTGRES_ADMIN"));
            await admin.OpenAsync();
            await new NpgsqlCommand("ALTER DATABASE " + _name + " ALLOW_CONNECTIONS " + (available ? "true" : "false"), admin).ExecuteNonQueryAsync();
            if (!available) await CloseApplicationSessions();
        }
        private async Task CloseApplicationSessions()
        {
            var cleanup = new NpgsqlConnectionStringBuilder(_app) { Database = "postgres", Pooling = false };
            await using var connection = new NpgsqlConnection(cleanup.ConnectionString);
            await connection.OpenAsync();
            await using var close = new NpgsqlCommand("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @name AND usename = current_user", connection);
            close.Parameters.AddWithValue("name", _name);
            await close.ExecuteNonQueryAsync();
        }
        public async Task<WorkView> Apply(WorkCommand command) { using IServiceScope scope = Host.Services.CreateScope(); return await scope.ServiceProvider.GetRequiredService<WorkStore>().ApplyAsync(command); }
        public async Task<WorkView> Get(long id) { using IServiceScope scope = Host.Services.CreateScope(); return await scope.ServiceProvider.GetRequiredService<WorkStore>().GetAsync(id); }
        public Task Reconcile(long id, long attempt) => Host.Services.GetRequiredService<ExecutionCoordinator>().ReconcileAsync(new(id, attempt), CancellationToken.None);
        public async Task<WorkView> StartWork()
        {
            long id = NextId();
            WorkView w = await Apply(new(NextId(), id, WorkAction.Create, Text: "Test durable execution"));
            w = await Apply(new(NextId(), id, WorkAction.Assign, w.Version, AgentId: WorkStore.DefaultAgentId));
            await Apply(new(NextId(), id, WorkAction.Execute, w.Version));
            return await Until(id, x => x.Work.Attempts[^1].Status == AttemptStatus.Starting);
        }
        public async Task<WorkView> StartRepositoryWork(string repository)
        {
            long id = NextId();
            WorkView work = await Apply(new(NextId(), id, WorkAction.Create, Text: "Repository setup memory"));
            work = await Apply(new(NextId(), id, WorkAction.Assign, work.Version, AgentId: 1));
            await Apply(new(NextId(), id, WorkAction.Execute, work.Version, Repository: new(repository, "Goblin", "agent@example.com")));
            return await Until(id, x => x.Work.Attempts[^1].Status == AttemptStatus.Starting);
        }
        public async Task<WorkspaceCheckpoint> SaveCheckpoint(WorkSnapshot work)
        {
            long attempt = work.Attempts[^1].Id;
            RepositoryBroker broker = Host.Services.GetRequiredService<RepositoryBroker>();
            await broker.PrepareAsync(work, default);
            long publication = await broker.ReserveOperationIdAsync(attempt, default);
            await broker.EnqueueAsync(attempt, publication, "publish", new MemoryStream([1, 2, 3]), default);
            for (int i = 0; i < 100 && (await broker.StatusAsync(attempt, publication, default)).State != "Succeeded"; i++) await Task.Delay(50);
            Assert.Equal("Succeeded", (await broker.StatusAsync(attempt, publication, default)).State);
            using IServiceScope scope = Host.Services.CreateScope();
            var store = new WorkspaceCheckpoints(scope.ServiceProvider.GetRequiredService<IDbContextFactory<GoblinDbContext>>());
            return await store.SaveAsync(attempt, work.Attempts[^1].TurnNumber, new string('a', 40), broker, default);
        }
        public async Task<WorkView> Until(long id, Func<WorkView, bool> ready)
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
            await new NpgsqlCommand("DROP DATABASE " + _name + " WITH (FORCE)", db).ExecuteNonQueryAsync();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }
}
