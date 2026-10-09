using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application;
using Goblin.Application.Connections;
using Goblin.Application.GitRepositories;
using Goblin.Application.Runtime;
using Goblin.Application.Work;
using Goblin.Application.Workspaces;
using Goblin.Contracts;
using Goblin.Contracts.Conversations;
using Goblin.Contracts.Runtime;
using Goblin.Core.GitRepositories;
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
using Env = Goblin.Contracts.Configuration.EnvironmentVariables;

namespace Goblin.Application.Tests;

public sealed class DatabaseFactAttribute : FactAttribute
{
    public DatabaseFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Env.GoblinTestPostgresAdmin) is null || Environment.GetEnvironmentVariable(Env.GoblinTestPostgresApp) is null)
            Skip = "Set PostgreSQL test connections to exercise real durable Work.";
    }
}

public sealed partial class DurabilityTests
{
    private static long _nextId = int.MaxValue;

    private static long NextId() => System.Threading.Interlocked.Increment(ref _nextId);

    [DatabaseFact]
    public async Task SlackProofNeedsLocalConfirmationAndEventsDispatchOnce()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        using IServiceScope scope = fixture.Host.Services.CreateScope();
        ExternalConversationStore store = scope.ServiceProvider.GetRequiredService<ExternalConversationStore>();
        var installation = new ExternalInstallation("slack-test", "T123", "A123", "U123");
        ExternalLinkCode code = await store.StartLinkAsync(installation, "owner-session", default);
        var proof = new ExternalMessage()
        {
            Installation = installation,
            EventId = "EV1",
            UserId = "U456",
            ChannelId = "D123",
            ThreadId = "1.000001",
            MessageId = "1.000001",
            Text = "link " + code.Code,
            Direct = true
        };
        await store.AcceptAsync(proof, default);
        ExternalReply proofReply = (await store.ProcessNextAsync(installation, default))!;
        Assert.Null(proofReply.WorkId);
        Assert.Equal(ExternalReplyKind.LinkConfirmationRequired, proofReply.Kind);
        Assert.Empty(await store.IdentitiesAsync(installation, default));
        await Assert.ThrowsAsync<ApplicationFailure>(() => store.ConfirmLinkAsync(installation, "different-session", code.Id, default));
        await store.ConfirmLinkAsync(installation, "owner-session", code.Id, default);
        ExternalMessage Message(string eventId, string messageId, string text) =>
            new()
            {
                Installation = installation,
                EventId = eventId,
                UserId = "U456",
                ChannelId = "D123",
                ThreadId = "2.000001",
                MessageId = messageId,
                Text = text,
                Direct = true
            };
        ExternalMessage request = Message("EV2", "2.000001", "Explain the design");
        await Task.WhenAll(store.AcceptAsync(request, default), store.AcceptAsync(request, default));
        await store.AcceptAsync(Message("EV3", "2.000001", "Explain the design"), default);
        ExternalReply reply = (await store.ProcessNextAsync(installation, default))!;
        Assert.NotNull(reply.WorkId);
        Assert.Equal(ExternalReplyKind.WorkSaved, reply.Kind);
        Assert.Null(await store.ProcessNextAsync(installation, default));
        await fixture.Until(reply.WorkId!.Value, view => view.Work.Attempts[0].Status == AttemptStatus.Starting);
        WorkView work = await fixture.Get(reply.WorkId.Value);
        Assert.Single(work.Work.Attempts);
        Assert.Equal(1, fixture.Runtime.Starts[work.Work.Attempts[0].Id]);
        ConversationView conversation = Assert.Single(await scope.ServiceProvider.GetRequiredService<ConversationStore>().ListAsync());
        Assert.Equal("U456", Assert.Single(conversation.Messages).Source!.UserId);
        await store.AcceptAsync(Message("EV4", "2.000002", "Additional context"), default);
        Assert.Equal(reply.WorkId, (await store.ProcessNextAsync(installation, default))!.WorkId);
        ExternalIdentityView identity = Assert.Single(await store.IdentitiesAsync(installation, default));
        await store.AcceptAsync(Message("EV5", "2.000003", "Do not accept after revocation"), default);
        await store.RevokeAsync(installation, identity.Id, default);
        Assert.Null((await store.ProcessNextAsync(installation, default))!.WorkId);
        Assert.Single((await fixture.Get(reply.WorkId.Value)).Work.Messages);
        await using GoblinDbContext db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<GoblinDbContext>>().CreateDbContextAsync();
        Assert.False(await db.ExternalMessages.AnyAsync(x => x.Body != null && x.Body.Contains(code.Code)));
    }

    [DatabaseFact]
    public async Task ExternalQuestionsSurviveRestartAndSlackAnswersContinueTheSameAttempt()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        var installation = new ExternalInstallation("question-installation", "T123", "A123", "U123");
        long workId;
        long attemptId;
        ExternalQuestion question;
        using (IServiceScope scope = fixture.Host.Services.CreateScope())
        {
            ExternalConversationStore store = scope.ServiceProvider.GetRequiredService<ExternalConversationStore>();
            ExternalLinkCode code = await store.StartLinkAsync(installation, "owner-session", default);
            ExternalMessage Message(string eventId, string timestamp, string text) => new()
            {
                Installation = installation,
                EventId = eventId,
                UserId = "U456",
                ChannelId = "D123",
                ThreadId = "10.000001",
                MessageId = timestamp,
                Text = text,
                Direct = true
            };
            await store.AcceptAsync(Message("link-event", "9.000001", "link " + code.Code), default);
            await store.ProcessNextAsync(installation, default);
            await store.ConfirmLinkAsync(installation, "owner-session", code.Id, default);
            await store.AcceptAsync(Message("request-event", "10.000001", "Explain the design"), default);
            workId = (await store.ProcessNextAsync(installation, default))!.WorkId!.Value;
            WorkView running = await fixture.Until(workId, view => view.Work.Attempts[^1].Status == AttemptStatus.Starting);
            attemptId = running.Work.Attempts[^1].Id;
            fixture.Runtime.Observations[attemptId] = new(ObservationKind.Paused, Text: "Which environment?");
            await fixture.Reconcile(workId, attemptId);
            question = (await store.NextQuestionAsync(installation, default))!;
            Assert.Equal("Which environment?", question.Text);
            Assert.Equal(workId, question.WorkId);
            Assert.Equal("D123", question.ChannelId);
            Assert.Equal("10.000001", question.ThreadId);
            Assert.Null(await store.NextQuestionAsync(new("another-installation", "T123", "A123", "U123"), default));
        }
        await fixture.RestartAsync();
        using (IServiceScope scope = fixture.Host.Services.CreateScope())
        {
            ExternalConversationStore store = scope.ServiceProvider.GetRequiredService<ExternalConversationStore>();
            Assert.Equal(question.DecisionId, (await store.NextQuestionAsync(installation, default))!.DecisionId);
            await store.QuestionSentAsync(installation, question, default);
            Assert.Null(await store.NextQuestionAsync(installation, default));
            await store.AcceptAsync(new()
            {
                Installation = installation,
                EventId = "answer-event",
                UserId = "U456",
                ChannelId = "D123",
                ThreadId = "10.000001",
                MessageId = "10.000002",
                Text = "Development",
                Direct = true
            }, default);
            Assert.Equal(workId, (await store.ProcessNextAsync(installation, default))!.WorkId);
            WorkView answered = await fixture.Get(workId);
            Assert.Equal("Development", answered.Work.Decisions[^1].Answer);
            Assert.Equal(attemptId, Assert.Single(answered.Work.Attempts).Id);
            await fixture.Until(workId, view => view.Work.Attempts[^1].TurnNumber == 2 && view.Work.Attempts[^1].Status == AttemptStatus.Starting);
            fixture.Runtime.Observations[attemptId] = new(ObservationKind.Paused, Text: "Which region?") { TurnNumber = 2 };
            await fixture.Reconcile(workId, attemptId);
            Assert.Equal("Which region?", (await store.NextQuestionAsync(installation, default))!.Text);
            ExternalIdentityView identity = Assert.Single(await store.IdentitiesAsync(installation, default));
            await store.RevokeAsync(installation, identity.Id, default);
            Assert.Null(await store.NextQuestionAsync(installation, default));
        }
    }

    [DatabaseFact]
    public async Task DefaultCoworkerIsSavedAtCreationAndUsedForExplicitStart()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        long id = NextId();
        WorkCommand create = WorkCommands.Create(NextId(), id, "Save this idea for later");
        WorkView saved = await fixture.Apply(create);
        Assert.Equal(WorkStore.DefaultAgentId, saved.Work.AgentId);
        Assert.Equal(WorkStatus.Ready, saved.Work.Status);
        Assert.Empty(saved.Work.Attempts);
        Assert.Single(saved.Work.History, x => x.Kind == WorkEventKind.Assigned);
        Assert.Empty(fixture.Runtime.Starts);
        Assert.Equal(saved.Version, (await fixture.Apply(create)).Version);
        await fixture.RestartAsync();
        Assert.Equal(WorkStore.DefaultAgentId, (await fixture.Get(id)).Work.AgentId);

        using IServiceScope scope = fixture.Host.Services.CreateScope();
        await using GoblinDbContext db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<GoblinDbContext>>().CreateDbContextAsync();
        Assert.Equal(WorkStore.DefaultAgentId, (await db.WorkItems.SingleAsync(x => x.Id == id)).AgentId);
        AgentView[] agents = await scope.ServiceProvider.GetRequiredService<WorkStore>().AgentsAsync();
        Assert.Equal(WorkStore.DefaultAgentId, Assert.Single(agents, x => x.IsDefault).Id);

        WorkCommand start = WorkCommands.Execute(NextId(), id, saved.Version);
        WorkView queued = await fixture.Apply(start);
        Assert.Equal(WorkStore.DefaultAgentId, queued.Work.AgentId);
        Assert.Equal(WorkStore.DefaultAgentId, Assert.Single(queued.Work.Attempts).AgentId);
        Assert.Equal(queued.Version, (await fixture.Apply(start)).Version);
        await fixture.Until(id, x => x.Work.Attempts[^1].Status == AttemptStatus.Starting);
        Assert.Equal(1, fixture.Runtime.Starts[queued.Work.Attempts[0].Id]);
        Assert.Equal(1, (await fixture.Get(id)).Work.History.Count(x => x.Kind == WorkEventKind.Assigned));
    }

    private static async Task EnableHandoffGitRepository(Fixture fixture)
    {
        fixture.Runtime.GitRepositoryExecution = true;
        using IServiceScope scope = fixture.Host.Services.CreateScope();
        GitHubStore settings = scope.ServiceProvider.GetRequiredService<GitHubStore>();
        await settings.ObserveAsync(new("handoff", "42", "owner"), Goblin.Contracts.GitHubConnectionStatus.Connected);
        await settings.SetGitRepositoryAsync(new(22, "owner/repo", "main", true), true, "handoff");
    }

    private sealed class Catalog : IGitRepositoryCatalog
    {
        public GitRepositoryAccount Account { get; set; } = new("catalog", "42", "owner");
        public GitRepositoryInfo GitRepository { get; set; } = new(22, "owner/repo", "main", true);

        public Task<GitRepositoryAccount?> GetAccountAsync(CancellationToken token) => Task.FromResult<GitRepositoryAccount?>(Account);

        public Task<GitRepositoryInfo> GitRepositoryAsync(string name, CancellationToken token) => Task.FromResult(GitRepository);

        public Task<GitRepositoryInfo[]> GitRepositoriesAsync(int page, CancellationToken token) => Task.FromResult(new[] { GitRepository });
    }

    [DatabaseFact]
    public async Task ChatApprovalAtomicallyEnablesGitRepositoryAndDispatchesOnlyOnce()
    {
        var catalog = new Catalog();
        await using Fixture fixture = await Fixture.CreateAsync(catalog);
        fixture.Runtime.GitRepositoryExecution = true;
        using IServiceScope scope = fixture.Host.Services.CreateScope();
        GitHubStore settings = scope.ServiceProvider.GetRequiredService<GitHubStore>();
        await settings.ObserveAsync(catalog.Account, Goblin.Contracts.GitHubConnectionStatus.Connected);
        long id = NextId();
        WorkView work = await fixture.Apply(WorkCommands.Create(NextId(), id, "Fix owner/repo and open a PR using base branch develop"));
        work = await fixture.Apply(new(NextId(), id, WorkAction.Assign)
        {
            ExpectedVersion = work.Version,
            AgentId = 1
        });
        work = await fixture.Apply(WorkCommands.Execute(NextId(), id, work.Version));
        Assert.Empty(work.Work.Attempts);
        Assert.Empty(await settings.GitRepositoriesAsync());
        Assert.True(work.Work.GitRepositoryAuthorization!.EnableGitRepository);
        Assert.True(work.Work.GitRepositoryAuthorization.Target.GitRepository!.Grant!.AllowPullRequest);
        await fixture.RestartAsync();
        using IServiceScope restarted = fixture.Host.Services.CreateScope();
        settings = restarted.ServiceProvider.GetRequiredService<GitHubStore>();
        work = await fixture.Get(id);
        long rejected = NextId();
        await fixture.RejectId("work_commands", rejected);
        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Apply(new(rejected, id, WorkAction.AuthorizeGitRepository)
        {
            ExpectedVersion = work.Version,
            AuthorizationId = work.Work.GitRepositoryAuthorization!.Id
        }));
        Assert.Empty(await settings.GitRepositoriesAsync());
        Assert.Empty((await fixture.Get(id)).Work.Attempts);
        var authorize = new WorkCommand(NextId(), id, WorkAction.AuthorizeGitRepository)
        {
            ExpectedVersion = work.Version,
            AuthorizationId = work.Work.GitRepositoryAuthorization!.Id
        };
        WorkView accepted = await fixture.Apply(authorize);
        Assert.Equal(accepted.Version, (await fixture.Apply(authorize)).Version);
        Assert.True(Assert.Single(await settings.GitRepositoriesAsync()).Enabled);
        Assert.Equal("main", (await settings.GitRepositoriesAsync())[0].DefaultBranch);
        Assert.Equal("develop", accepted.Work.Attempts[0].Target.GitRepository!.Grant!.BaseBranch);
        work = await fixture.Until(id, x => x.Work.Attempts.Length == 1 && x.Work.Attempts[0].Status == AttemptStatus.Starting);
        Assert.Equal(1, fixture.Runtime.Starts[work.Work.Attempts[0].Id]);
    }

    [DatabaseFact]
    public async Task ContextAndAccountChangesCannotReuseChatApproval()
    {
        var catalog = new Catalog();
        await using Fixture fixture = await Fixture.CreateAsync(catalog);
        using IServiceScope scope = fixture.Host.Services.CreateScope();
        GitHubStore settings = scope.ServiceProvider.GetRequiredService<GitHubStore>();
        await settings.ObserveAsync(catalog.Account, Goblin.Contracts.GitHubConnectionStatus.Connected);
        long id = NextId();
        WorkView work = await fixture.Apply(WorkCommands.Create(NextId(), id, "Fix owner/repo"));
        work = await fixture.Apply(new(NextId(), id, WorkAction.Assign)
        {
            ExpectedVersion = work.Version,
            AgentId = 1
        });
        work = await fixture.Apply(WorkCommands.Execute(NextId(), id, work.Version));
        long oldApproval = work.Work.GitRepositoryAuthorization!.Id;
        work = await fixture.Apply(WorkCommands.AddContext(NextId(), id, work.Version, "Also push the changes"));
        await Assert.ThrowsAsync<ApplicationFailure>(() => fixture.Apply(new(NextId(), id, WorkAction.AuthorizeGitRepository)
        {
            ExpectedVersion = work.Version,
            AuthorizationId = oldApproval
        }));
        work = await fixture.Apply(new(NextId(), id, WorkAction.PrepareGitRepository)
        {
            ExpectedVersion = work.Version,
            Text = "owner/repo"
        });
        Assert.True(work.Work.GitRepositoryAuthorization!.Target.GitRepository!.Grant!.AllowPush);
        await settings.ObserveAsync(new("new-account", "43", "another-owner"), Goblin.Contracts.GitHubConnectionStatus.Connected);
        await Assert.ThrowsAsync<ApplicationFailure>(() => fixture.Authorize(work));
        Assert.Empty(await settings.GitRepositoriesAsync());
        Assert.Empty((await fixture.Get(id)).Work.Attempts);
    }

    [DatabaseFact]
    public async Task EnabledGitRepositoryIntentStartsDirectlyAndSurvivesRestartAndDuplicateCommands()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await EnableHandoffGitRepository(fixture);
        long id = NextId();
        WorkView work = await fixture.Apply(WorkCommands.Create(NextId(), id, "https://github.com/owner/repo convert Python to Rust"));
        work = await fixture.Apply(new(NextId(), id, WorkAction.Assign)
        {
            ExpectedVersion = work.Version,
            AgentId = 1
        });
        WorkCommand start = WorkCommands.Execute(NextId(), id, work.Version);
        work = await fixture.Apply(start);
        Assert.Null(work.Work.Attention);
        Assert.Single(work.Work.Attempts);
        Assert.Equal(GitRepositoryAuthorizationStatus.Authorized, work.Work.GitRepositoryAuthorization!.Status);
        Assert.Equal("owner", work.Work.Attempts[0].Target.GitRepository!.GitAuthorName);
        Assert.Equal("42+owner@users.noreply.github.com", work.Work.Attempts[0].Target.GitRepository!.GitAuthorEmail);
        Assert.Equal(work.Version, (await fixture.Apply(start)).Version);
        await fixture.RestartAsync();
        work = await fixture.Get(id);
        Assert.Equal("owner/repo", Assert.Single(work.Work.Attempts).Target.GitRepository!.GitRepository);
        ApplicationFailure rejected = await Assert.ThrowsAsync<ApplicationFailure>(() => fixture.Apply(new(NextId(), id, WorkAction.AuthorizeGitRepository)
        {
            ExpectedVersion = work.Version,
            GitRepository = new("unapproved/repo", "Goblin", "agent@example.com")
        }));
        Assert.Equal("invalid_command", rejected.Code);
        Assert.Single((await fixture.Get(id)).Work.Attempts);
        work = await fixture.Until(id, x => x.Work.Attempts[^1].Status == AttemptStatus.Starting);
        Assert.Single(work.Work.Attempts);
        Assert.Equal("handoff", work.Work.Attempts[0].Target.GitRepository!.Grant!.Generation);
        Assert.Equal(1, fixture.Runtime.Starts[work.Work.Attempts[0].Id]);
        Assert.NotNull(work.Work.Workspace);
        Assert.Null(work.Work.GitRepositoryRequest);
    }

    [DatabaseFact]
    public async Task GitRepositoryAnswerHandsOffTheSameWorkWithoutRewritingTheConversationAttempt()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await EnableHandoffGitRepository(fixture);
        WorkView work = await fixture.StartWork();
        long id = work.Work.Id, original = work.Work.Attempts[^1].Id;
        fixture.Runtime.Observations[original] = new(ObservationKind.Paused,
            new("chosen", "conversation-session", "turn-1"), "Which repository?");
        await fixture.Reconcile(id, original);
        work = await fixture.Until(id, x => x.Work.Attention?.Reason == AttentionReason.InputRequired && !x.Work.Attempts[^1].CleanupPending);
        WorkCommand answer = WorkCommands.Answer(NextId(), id, work.Version, work.Work.Decisions[^1].Id, "Clone https://github.com/owner/repo");
        work = await fixture.Apply(answer);
        Assert.Null(work.Work.Attention);
        Assert.Equal(2, work.Work.Attempts.Length);
        Assert.Equal(1, work.Work.Attempts[0].TurnNumber);
        Assert.Equal(2, work.Work.Attempts.Length);
        Assert.Equal(original, work.Work.Attempts[0].Id);
        Assert.Equal("conversation-session", work.Work.Attempts[0].Session!.SessionReference);
        Assert.Null(work.Work.Attempts[0].Target.GitRepository);
        Assert.Equal(AttemptStatus.Succeeded, work.Work.Attempts[0].Status);
        Assert.Contains("Clone", work.Work.Decisions[^1].Answer);
        await fixture.Apply(answer); // Lost response cannot restart the old text turn.
        Assert.Equal(2, (await fixture.Get(id)).Work.Attempts.Length);
        Assert.Equal(1, fixture.Runtime.Starts[original]);
    }

    [DatabaseFact]
    public async Task ChangedDeliveryAfterRetriedConversationRequiresNewApproval()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await EnableHandoffGitRepository(fixture);
        WorkView work = await fixture.StartGitRepositoryWork("owner/repo");
        long id = work.Work.Id, first = work.Work.Attempts[^1].Id;
        fixture.Runtime.Observations[first] = new(ObservationKind.Failed, Failure: FailureKind.ExecutionFailed);
        await fixture.Reconcile(id, first);
        work = await fixture.Until(id, x => x.Work.Attention?.Reason == AttentionReason.Failure && !x.Work.Attempts[^1].CleanupPending);
        work = await fixture.ExecuteAndAuthorize(WorkCommands.Retry(NextId(), id, work.Version));
        work = await fixture.Until(id, x => x.Work.Attempts[^1].Status == AttemptStatus.Starting);
        long second = work.Work.Attempts[^1].Id;
        fixture.Runtime.Observations[second] = new(ObservationKind.Paused, Text: "How should I deliver it?");
        await fixture.Reconcile(id, second);
        work = await fixture.Until(id, x => x.Work.Attention?.Reason == AttentionReason.InputRequired && !x.Work.Attempts[^1].CleanupPending);
        work = await fixture.Apply(WorkCommands.Answer(NextId(), id, work.Version, work.Work.Decisions[^1].Id, "Push the changes"));
        Assert.Equal(AttentionReason.GitRepositoryRequired, work.Work.Attention!.Reason);
        Assert.Equal(2, work.Work.Attempts.Length);
        work = await fixture.Apply(new(NextId(), id, WorkAction.PrepareGitRepository)
        {
            ExpectedVersion = work.Version,
            Text = "owner/repo"
        });
        Assert.False(work.Work.GitRepositoryAuthorization!.Retry);
        Assert.True(work.Work.GitRepositoryAuthorization.Target.GitRepository!.Grant!.AllowPush);
        work = await fixture.Authorize(work);
        Assert.Equal(3, work.Work.Attempts.Length);
        Assert.Equal(AttemptStatus.Failed, work.Work.Attempts[0].Status);
        Assert.False(work.Work.Attempts[1].Target.GitRepository!.Grant!.AllowPush);
        Assert.True(work.Work.Attempts[2].Target.GitRepository!.Grant!.AllowPush);
    }

    [DatabaseFact]
    public async Task RuntimeGitRepositoryRequestRequiresSetupAndCleanupBeforeDispatch()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await EnableHandoffGitRepository(fixture);
        WorkView work = await fixture.StartWork();
        long id = work.Work.Id, attempt = work.Work.Attempts[^1].Id;
        fixture.Runtime.FailCleanup = true;
        fixture.Runtime.Observations[attempt] = new(ObservationKind.WorkspaceRequired, Text: "Inspect repository files");
        await fixture.Reconcile(id, attempt);
        work = await fixture.Until(id, x => x.Work.Attention?.Reason == AttentionReason.CleanupRequired);
        Assert.NotNull(work.Work.GitRepositoryRequest);
        await Assert.ThrowsAsync<WorkRuleException>(() => fixture.Apply(new(NextId(), id, WorkAction.PrepareGitRepository)
        {
            ExpectedVersion = work.Version,
            GitRepository = new("owner/repo", "Goblin", "agent@example.com")
        }));
        fixture.Runtime.FailCleanup = false;
        await fixture.Reconcile(id, attempt);
        work = await fixture.Until(id, x => x.Work.Attention?.Reason == AttentionReason.GitRepositoryRequired && !x.Work.Attempts[^1].CleanupPending);
        Assert.Single(work.Work.Attempts);
        Assert.Null(work.Work.Workspace);
        await fixture.Reconcile(id, attempt); // Observing the same outcome does not dispatch or rewrite the request.
        Assert.Equal(work.Version, (await fixture.Get(id)).Version);
        await fixture.Apply(new(NextId(), id, WorkAction.Cancel)
        {
            ExpectedVersion = work.Version
        });
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
        var catalogs = new ModelCatalogStore(factory, source,
            fixture.Host.Services.GetRequiredService<ModelCatalogRefreshCoordinator>());
        await catalogs.GetAsync(1, 3, null);
        ModelCatalogView first = await UntilCatalog(catalogs, x => !x.Refreshing && x.Models.Length == 3);
        Assert.True(first.HasMore);
        Assert.Equal("gpt-test-0", first.DefaultModel);
        Assert.Equal(10, (await catalogs.GetAsync(1, 10, "gpt-test-11")).Models.Length);
        Assert.Equal("gpt-test-11", (await catalogs.GetAsync(1, 3, "gpt-test-11")).Models[0].Model);

        long id = NextId();
        WorkView created = await fixture.Apply(WorkCommands.Create(NextId(), id, "Use the selected model"));
        WorkView assigned = await fixture.Apply(new(NextId(), id, WorkAction.Assign)
        {
            ExpectedVersion = created.Version,
            AgentId = 1
        });
        WorkView queued = await fixture.Apply(WorkCommands.Execute(NextId(), id, assigned.Version, new WorkModelSelection("gpt-test-1", "high")));
        Assert.Equal("gpt-test-1", queued.Work.Attempts[^1].Target.RequestedModel);
        Assert.Equal("high", queued.Work.Attempts[^1].Target.RequestedEffort);
        Assert.Equal("high", (await fixture.Get(id)).Work.Attempts[^1].Target.RequestedEffort);

        long rejectedId = NextId();
        WorkView other = await fixture.Apply(WorkCommands.Create(NextId(), rejectedId, "Reject unsupported effort"));
        other = await fixture.Apply(new(NextId(), rejectedId, WorkAction.Assign)
        {
            ExpectedVersion = other.Version,
            AgentId = 1
        });
        ApplicationFailure rejected = await Assert.ThrowsAsync<ApplicationFailure>(() => fixture.Apply(WorkCommands.Execute(NextId(), rejectedId, other.Version, new WorkModelSelection("gpt-test-1", "unsupported"))));
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
        await scope.ServiceProvider.GetRequiredService<ConnectionStore>().ObserveAccountAsync(1, Goblin.Contracts.ConnectionAvailability.Available, "new-account");
        ModelCatalogView changed = await catalogs.GetAsync(1, 3, null);
        Assert.Empty(changed.Models); // A different account never sees the old catalog.
    }

    [DatabaseFact]
    public async Task ModelDiscoveryBelongsToTheHostAndShutdownPreservesTheSavedCatalog()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        var source = new CatalogSource();
        IDbContextFactory<GoblinDbContext> factory = fixture.Host.Services.GetRequiredService<IDbContextFactory<GoblinDbContext>>();
        var catalogs = new ModelCatalogStore(factory, source,
            fixture.Host.Services.GetRequiredService<ModelCatalogRefreshCoordinator>());
        await catalogs.GetAsync(1, 3, null);
        ModelCatalogView first = await UntilCatalog(catalogs, x => !x.Refreshing && x.Models.Length == 3);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Stamp = "v2";
        source.Discover = async token =>
        {
            started.SetResult(token);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cancelled.SetResult(); }
            return [];
        };
        using var request = new CancellationTokenSource();
        await catalogs.GetAsync(1, 3, null, request.Token);
        CancellationToken discovery = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        request.Cancel();
        Assert.True(discovery.CanBeCanceled);
        Assert.False(discovery.IsCancellationRequested);
        for (int i = 0; i < 10; i++) catalogs.ScheduleRefresh(1, force: true);
        Assert.Equal(2, source.Calls);

        await fixture.Host.StopAsync();
        Assert.True(discovery.IsCancellationRequested);
        Assert.True(cancelled.Task.IsCompletedSuccessfully);
        await using GoblinDbContext db = await factory.CreateDbContextAsync();
        Persistence.Entities.ConnectionModelCatalog saved = await db.ConnectionModelCatalogs.SingleAsync(x => x.ConnectionId == 1);
        Assert.Equal("v1", saved.ExecutableStamp);
        Assert.False(saved.RefreshFailed);
        Assert.Null(saved.RetryAfter);
        Assert.Equal(first.FetchedAt, new DateTimeOffset(saved.FetchedAt!.Value, TimeSpan.Zero));
        Assert.Equal(12, ModelCatalogPolicy.Read(saved.Catalog).Length);
        catalogs.ScheduleRefresh(1, force: true);
        Assert.False((await catalogs.GetAsync(1, 3, null)).Refreshing);
        Assert.Equal(2, source.Calls);
    }

    [DatabaseFact]
    public async Task ModelRefreshDiscardsAResponseFromAnAccountThatChangedDuringDiscovery()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<RuntimeModel[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new CatalogSource
        {
            Discover = token => { started.SetResult(); return response.Task.WaitAsync(token); }
        };
        IDbContextFactory<GoblinDbContext> factory = fixture.Host.Services.GetRequiredService<IDbContextFactory<GoblinDbContext>>();
        ModelCatalogRefreshCoordinator refreshes = fixture.Host.Services.GetRequiredService<ModelCatalogRefreshCoordinator>();
        var catalogs = new ModelCatalogStore(factory, source, refreshes);
        await catalogs.GetAsync(1, 3, null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using (IServiceScope scope = fixture.Host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ConnectionStore>().ObserveAccountAsync(1,
                ConnectionAvailability.Disconnected, "changed-account");
        response.SetResult([new("old-id", "old-model", "Old account model", "medium", ["medium"], true)]);
        for (int i = 0; i < 100 && refreshes.IsRefreshing(1); i++) await Task.Delay(25);
        Assert.False(refreshes.IsRefreshing(1));
        await using GoblinDbContext db = await factory.CreateDbContextAsync();
        Assert.Empty(await db.ConnectionModelCatalogs.ToArrayAsync());
        Assert.Empty((await catalogs.GetAsync(1, 3, null)).Models);
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
        public Func<CancellationToken, Task<RuntimeModel[]>>? Discover { get; set; }
        public int Calls => Volatile.Read(ref _calls);

        public string ExecutableStamp() => Stamp;

        public Task<RuntimeModel[]> ListAsync(CancellationToken token)
        {
            Interlocked.Increment(ref _calls);
            if (Discover is not null) return Discover(token);
            if (Fail) throw new InvalidOperationException("private upstream failure");
            return Task.FromResult(Enumerable.Range(0, 12).Select(i => new RuntimeModel(
                $"id-{i}", $"gpt-test-{i}", $"Test model {i}", "medium", ["low", "medium", "high"], i == 0)).ToArray());
        }
    }

    [DatabaseFact]
    public async Task SetupMemorySurvivesRestartIsScopedAndRequiresTheCurrentSavedTurn()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Runtime.GitRepositoryExecution = true;
        fixture.Remote.Reconciled = true;
        using IServiceScope scope = fixture.Host.Services.CreateScope();
        GitHubStore settings = scope.ServiceProvider.GetRequiredService<GitHubStore>();
        await settings.ObserveAsync(new("first", "42", "owner"), Goblin.Contracts.GitHubConnectionStatus.Connected);
        await settings.SetGitRepositoryAsync(new(22, "owner/repo", "main", true), true, "first");
        await settings.SetGitRepositoryAsync(new(23, "owner/other", "main", true), true, "first");
        WorkView work = await fixture.StartGitRepositoryWork("owner/repo");
        long attempt = work.Work.Attempts[^1].Id;
        var observation = new VerifiedGitRepositorySetup(new("python-tests", "Tests need Python", ["python 3.12"],
            ["install-python"], ["pyproject.toml"], [new("python --version", "Python 3.12")]),
            [new("pyproject.toml", new string('b', 64))], new string('c', 64));
        GitRepositorySetupStore memory = scope.ServiceProvider.GetRequiredService<GitRepositorySetupStore>();
        var request = new SetupMemoryWrite(1, long.MaxValue, "image-one", [observation]);
        await Assert.ThrowsAsync<ApplicationFailure>(() => memory.SaveAsync(attempt, request, default));
        Assert.Empty(await memory.ReadAsync(attempt, default));
        WorkspaceCheckpoint saved = await fixture.SaveCheckpoint(work.Work);
        request = new(1, saved.Id, "image-one", [observation]);
        await Assert.ThrowsAsync<ApplicationFailure>(() => memory.SaveAsync(attempt,
            new SetupMemoryWrite(1, saved.Id, "image-one", [observation with { Setup = observation.Setup with { Checks = [] } }]), default));
        await Task.WhenAll(memory.SaveAsync(attempt, request, default), memory.SaveAsync(attempt, request, default));
        GitRepositorySetupMemory learned = Assert.Single(await memory.ReadAsync(attempt, default));
        Assert.Equal(work.Work.Id, learned.WorkId);
        Assert.Equal(saved.CommitSha, learned.Commit);
        await Assert.ThrowsAsync<ApplicationFailure>(() => memory.SaveAsync(attempt, new SetupMemoryWrite(2, saved.Id, "image-one", [observation]), default));
        await Assert.ThrowsAsync<ApplicationFailure>(() => memory.SaveAsync(attempt, new SetupMemoryWrite(1, saved.Id, "changed-image", [observation]), default));
        await fixture.RestartAsync();
        using IServiceScope restarted = fixture.Host.Services.CreateScope();
        GitRepositorySetupStore reopened = restarted.ServiceProvider.GetRequiredService<GitRepositorySetupStore>();
        Assert.Equal(learned.Id, Assert.Single(await reopened.ReadAsync(attempt, default)).Id);
        fixture.Runtime.Observations[attempt] = new(ObservationKind.Result, Text: "Ready") { CheckpointId = saved.Id };
        await fixture.Reconcile(work.Work.Id, attempt);
        await Assert.ThrowsAsync<ApplicationFailure>(() => reopened.SaveAsync(attempt, request, default));
        WorkView next = await fixture.StartGitRepositoryWork("owner/repo");
        long nextAttempt = next.Work.Attempts[^1].Id;
        Assert.Equal(learned.Id, Assert.Single(await reopened.ReadAsync(nextAttempt, default)).Id);
        WorkspaceCheckpoint nextSaved = await fixture.SaveCheckpoint(next.Work);
        VerifiedGitRepositorySetup changed = observation with { ConfigurationHash = new string('d', 64) };
        await reopened.SaveAsync(nextAttempt, new SetupMemoryWrite(1, nextSaved.Id, "image-one", [changed]), default);
        Assert.Equal(2, (await reopened.ReadAsync(nextAttempt, default)).Length);
        fixture.Runtime.Observations[nextAttempt] = new(ObservationKind.Result, Text: "Ready") { CheckpointId = nextSaved.Id };
        await fixture.Reconcile(next.Work.Id, nextAttempt);
        WorkView other = await fixture.StartGitRepositoryWork("owner/other");
        long otherAttempt = other.Work.Attempts[^1].Id;
        Assert.Empty(await reopened.ReadAsync(otherAttempt, default));
        WorkspaceCheckpoint otherSaved = await fixture.SaveCheckpoint(other.Work);
        fixture.Runtime.Observations[otherAttempt] = new(ObservationKind.Result, Text: "Ready") { CheckpointId = otherSaved.Id };
        await fixture.Reconcile(other.Work.Id, otherAttempt);
        GitHubStore nextSettings = restarted.ServiceProvider.GetRequiredService<GitHubStore>();
        await nextSettings.ObserveAsync(new("second", "99", "someone-else"), Goblin.Contracts.GitHubConnectionStatus.Connected);
        await nextSettings.SetGitRepositoryAsync(new(22, "owner/repo", "main", true), true, "second");
        WorkView otherAccount = await fixture.StartGitRepositoryWork("owner/repo");
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
        await fixture.Apply(WorkCommands.Answer(NextId(), id, work.Version, decision, "PostgreSQL"));
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
        await store.ObserveAsync(id, InspectionObservation.Running, default);
        await store.StopAsync(work.Work.Id, id, default);
        for (int i = 0; i < 100 && (await store.ListAsync(work.Work.Id, default)).Single().State != InspectionState.Stopped; i++) await Task.Delay(50);
        Assert.Equal(InspectionState.Stopped, (await store.ListAsync(work.Work.Id, default)).Single().State);
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
        work = await fixture.Apply(WorkCommands.Answer(NextId(), id, work.Version, work.Work.Decisions.Single().Id, "Yes"));
        Assert.Equal(AttemptStatus.Queued, work.Work.Attempts[0].Status);
        Assert.True((await db.ExecutionAttempts.AsNoTracking().SingleAsync(x => x.Id == attempt)).WorkspaceRetained);
    }

    [DatabaseFact]
    public async Task GitCheckpointMetadataSurvivesRestartWithoutAnyFileBytes()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Runtime.GitRepositoryExecution = true;
        fixture.Remote.Reconciled = true;
        using IServiceScope scope = fixture.Host.Services.CreateScope();
        GitHubStore settings = scope.ServiceProvider.GetRequiredService<GitHubStore>();
        await settings.ObserveAsync(new("first", "42", "owner"), Goblin.Contracts.GitHubConnectionStatus.Connected);
        await settings.SetGitRepositoryAsync(new(22, "owner/repo", "main", true), true, "first");
        long id = NextId();
        WorkView work = await fixture.Apply(WorkCommands.Create(NextId(), id, "Checkpoint test"));
        work = await fixture.Apply(new(NextId(), id, WorkAction.Assign)
        {
            ExpectedVersion = work.Version,
            AgentId = 1
        });
        await fixture.ExecuteAndAuthorize(new(NextId(), id, WorkAction.Execute)
        {
            ExpectedVersion = work.Version,
            GitRepository = new("owner/repo", "Goblin", "agent@example.com")
        });
        work = await fixture.Until(id, x => x.Work.Attempts[^1].Status == AttemptStatus.Starting);
        long attempt = work.Work.Attempts[^1].Id;
        GitRepositoryBroker broker = fixture.Host.Services.GetRequiredService<GitRepositoryBroker>();
        await broker.PrepareAsync(work.Work, default);
        long publication = await broker.ReserveOperationIdAsync(attempt, default);
        await broker.EnqueueAsync(attempt, publication, work.Work.Attempts[^1].Target.GitRepository!.Grant!.AllowPush ? GitRepositoryOperationKind.Publish : GitRepositoryOperationKind.Checkpoint, new MemoryStream([1, 2, 3]), default);
        for (int i = 0; i < 100 && (await broker.StatusAsync(attempt, publication, default)).State != GitRepositoryOperationState.Succeeded; i++) await Task.Delay(50);
        Assert.Equal(Goblin.Contracts.GitRepositoryOperationState.Succeeded, (await broker.StatusAsync(attempt, publication, default)).State);
        Assert.Equal(0, fixture.Remote.Calls); // Local checkpoint did not invoke GitHub.
        Assert.Equal(0, fixture.Remote.CheckpointPreparations);
        IDbContextFactory<GoblinDbContext> factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<GoblinDbContext>>();
        var store = new WorkspaceCheckpoints(factory);
        WorkspaceCheckpointCoordinator checkpoints = fixture.Host.Services.GetRequiredService<WorkspaceCheckpointCoordinator>();
        WorkspaceCheckpoint saved = await checkpoints.SaveAsync(attempt, 1, new string('a', 40), default);
        AssertCheckpoint(saved, (await store.LatestAsync(id, "owner/repo", default))!);
        AssertCheckpoint(saved, Assert.Single(await store.ListAsync(id, default)));
        Assert.Equal(saved.Id, (await checkpoints.SaveAsync(attempt, 1, new string('a', 40), default)).Id);
        await using (GoblinDbContext db = await factory.CreateDbContextAsync())
            Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_schema='public' AND table_name='workspace_checkpoints' AND data_type='bytea'").SingleAsync());
        Assert.True(await store.VerifiedAsync(saved.Id, attempt, 1, default));
        Assert.False(await store.VerifiedAsync(saved.Id, attempt, 2, default));
        await fixture.RestartAsync();
        using IServiceScope restartedScope = fixture.Host.Services.CreateScope();
        var reopened = new WorkspaceCheckpoints(restartedScope.ServiceProvider.GetRequiredService<IDbContextFactory<GoblinDbContext>>());
        AssertCheckpoint(saved, (await reopened.LatestAsync(id, "owner/repo", default))!);
        Assert.Equal(work.Work.Workspace, (await fixture.Get(id)).Work.Workspace);
        fixture.Runtime.Observations[attempt] = new(ObservationKind.Result, Text: "Saved result") { CheckpointId = saved.Id };
        await fixture.Reconcile(id, attempt);
        work = await fixture.Until(id, x => x.Work.Attention?.Reason == AttentionReason.ResultReview && !x.Work.Attempts[^1].CleanupPending);
        await fixture.Apply(WorkCommands.Approve(NextId(), id, work.Version, attempt));
    }

    private static void AssertCheckpoint(WorkspaceCheckpoint expected, WorkspaceCheckpoint actual)
    {
        Assert.Equal((expected.Id, expected.WorkId, expected.AttemptId, expected.TurnNumber, expected.WorkspaceNumber,
            expected.GitRepository, expected.Branch, expected.CommitSha),
            (actual.Id, actual.WorkId, actual.AttemptId, actual.TurnNumber, actual.WorkspaceNumber,
                actual.GitRepository, actual.Branch, actual.CommitSha));
        // PostgreSQL timestamps retain microseconds; the immediate save response has .NET ticks.
        Assert.Equal(expected.CreatedAt.UtcTicks / 10, actual.CreatedAt.UtcTicks / 10);
    }

    [DatabaseFact]
    public async Task CheckpointCoordinatorRejectsUnconfirmedCommitsAndCoalescesIdenticalSaves()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await EnableHandoffGitRepository(fixture);
        WorkView work = await fixture.StartGitRepositoryWork("owner/repo");
        long attempt = work.Work.Attempts[^1].Id;
        WorkspaceCheckpointCoordinator checkpoints = fixture.Host.Services.GetRequiredService<WorkspaceCheckpointCoordinator>();
        WorkspaceCheckpoints store = fixture.Host.Services.GetRequiredService<WorkspaceCheckpoints>();
        ApplicationFailure invalid = await Assert.ThrowsAsync<ApplicationFailure>(() => checkpoints.SaveAsync(attempt, 1, "invalid", default));
        Assert.Equal("workspace_changed", invalid.Code);
        ApplicationFailure unconfirmed = await Assert.ThrowsAsync<ApplicationFailure>(() => checkpoints.SaveAsync(attempt, 1, new string('a', 40), default));
        Assert.Equal("workspace_checkpoint_unconfirmed", unconfirmed.Code);
        ApplicationFailure wrongTurn = await Assert.ThrowsAsync<ApplicationFailure>(() => checkpoints.SaveAsync(attempt, 2, new string('a', 40), default));
        Assert.Equal("workspace_changed", wrongTurn.Code);
        Assert.Empty(await store.ListAsync(work.Work.Id, default));

        WorkspaceCheckpoint saved = await fixture.SaveCheckpoint(work.Work);
        WorkspaceCheckpoint[] duplicates = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            checkpoints.SaveAsync(attempt, 1, saved.CommitSha, default)));
        Assert.All(duplicates, x => AssertCheckpoint(saved, x));
        AssertCheckpoint(saved, Assert.Single(await store.ListAsync(work.Work.Id, default)));
        Assert.Equal(0, fixture.Remote.Calls);
        Assert.Equal(0, fixture.Remote.ReconciliationCalls);

        // A different verified commit for the same turn must not replace its checkpoint.
        GitRepositoryBroker broker = fixture.Host.Services.GetRequiredService<GitRepositoryBroker>();
        fixture.Remote.InspectedCommit = new string('b', 40);
        long operation = await broker.ReserveOperationIdAsync(attempt, default);
        await broker.EnqueueAsync(attempt, operation, GitRepositoryOperationKind.Checkpoint, new MemoryStream([4, 5, 6]), default);
        for (int i = 0; i < 100 && (await broker.StatusAsync(attempt, operation, default)).State != GitRepositoryOperationState.Succeeded; i++) await Task.Delay(25);
        Assert.Equal(GitRepositoryOperationState.Succeeded, (await broker.StatusAsync(attempt, operation, default)).State);
        ApplicationFailure changed = await Assert.ThrowsAsync<ApplicationFailure>(() => checkpoints.SaveAsync(attempt, 1, fixture.Remote.InspectedCommit, default));
        Assert.Equal("workspace_changed", changed.Code);
        AssertCheckpoint(saved, Assert.Single(await store.ListAsync(work.Work.Id, default)));
    }

    [DatabaseFact]
    public async Task PublishedCheckpointRequiresConfirmationAndRechecksOwnershipAfterRemoteVerification()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await EnableHandoffGitRepository(fixture);
        WorkView work = await fixture.StartGitRepositoryWork("owner/repo", "Publish repository");
        long attempt = work.Work.Attempts[^1].Id;
        GitRepositoryBroker broker = fixture.Host.Services.GetRequiredService<GitRepositoryBroker>();
        WorkspaceCheckpointCoordinator checkpoints = fixture.Host.Services.GetRequiredService<WorkspaceCheckpointCoordinator>();
        WorkspaceCheckpoints store = fixture.Host.Services.GetRequiredService<WorkspaceCheckpoints>();
        await broker.PrepareAsync(work.Work, default);
        long operation = await broker.ReserveOperationIdAsync(attempt, default);
        await broker.EnqueueAsync(attempt, operation, GitRepositoryOperationKind.Publish, new MemoryStream([1, 2, 3]), default);
        for (int i = 0; i < 100 && (await broker.StatusAsync(attempt, operation, default)).State != GitRepositoryOperationState.Succeeded; i++) await Task.Delay(25);
        Assert.Equal(GitRepositoryOperationState.Succeeded, (await broker.StatusAsync(attempt, operation, default)).State);
        ApplicationFailure unconfirmed = await Assert.ThrowsAsync<ApplicationFailure>(() => checkpoints.SaveAsync(attempt, 1, fixture.Remote.InspectedCommit, default));
        Assert.Equal("workspace_checkpoint_unconfirmed", unconfirmed.Code);
        Assert.Empty(await store.ListAsync(work.Work.Id, default));

        var verifying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmed = new TaskCompletionSource<GitRepositoryOperationResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Remote.Reconcile = token => { verifying.SetResult(); return confirmed.Task.WaitAsync(token); };
        Task<WorkspaceCheckpoint> pending = checkpoints.SaveAsync(attempt, 1, fixture.Remote.InspectedCommit, default);
        await verifying.Task.WaitAsync(TimeSpan.FromSeconds(5));
        WorkView cancelled = await fixture.Apply(new(NextId(), work.Work.Id, WorkAction.Cancel) { ExpectedVersion = work.Version })
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AttemptStatus.CancellationRequested, cancelled.Work.Attempts[^1].Status);
        confirmed.SetResult(new(fixture.Remote.InspectedCommit, "https://github.com/owner/repo/tree/verified"));
        ApplicationFailure changed = await Assert.ThrowsAsync<ApplicationFailure>(() => pending);
        Assert.Equal("workspace_changed", changed.Code);
        Assert.Empty(await store.ListAsync(work.Work.Id, default));
        Assert.Equal(1, fixture.Remote.Calls);
    }

    [DatabaseFact]
    public async Task PublishedCheckpointPreparesItsRemoteHeadWhileLocalCheckpointUsesRetainedFiles()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await EnableHandoffGitRepository(fixture);
        fixture.Remote.Reconciled = true;
        long id = NextId();
        WorkView work = await fixture.Apply(WorkCommands.Create(NextId(), id, "Push owner/repo"));
        work = await fixture.Apply(new(NextId(), id, WorkAction.Assign)
        {
            ExpectedVersion = work.Version,
            AgentId = 1
        });
        await fixture.ExecuteAndAuthorize(WorkCommands.Execute(NextId(), id, work.Version));
        work = await fixture.Until(id, x => x.Work.Attempts[^1].Status == AttemptStatus.Starting);
        await fixture.SaveCheckpoint(work.Work);
        await fixture.Host.Services.GetRequiredService<GitRepositoryBroker>().PrepareAsync(work.Work, default);
        Assert.Equal(1, fixture.Remote.CheckpointPreparations);
    }

    [DatabaseFact]
    public async Task PersistentWorkspaceAndAttemptHistorySurviveFailureAndExplicitRetry()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Runtime.GitRepositoryExecution = true;
        using IServiceScope scope = fixture.Host.Services.CreateScope();
        GitHubStore settings = scope.ServiceProvider.GetRequiredService<GitHubStore>();
        await settings.ObserveAsync(new("first", "42", "owner"), Goblin.Contracts.GitHubConnectionStatus.Connected);
        await settings.SetGitRepositoryAsync(new(22, "owner/repo", "main", true), true, "first");
        long id = NextId();
        WorkView work = await fixture.Apply(WorkCommands.Create(NextId(), id, "Continue surviving edits"));
        work = await fixture.Apply(new(NextId(), id, WorkAction.Assign)
        {
            ExpectedVersion = work.Version,
            AgentId = 1
        });
        await fixture.ExecuteAndAuthorize(new(NextId(), id, WorkAction.Execute)
        {
            ExpectedVersion = work.Version,
            GitRepository = new("owner/repo", "Goblin", "agent@example.com")
        });
        work = await fixture.Until(id, x => x.Work.Attempts[^1].Status == AttemptStatus.Starting);
        long first = work.Work.Attempts[^1].Id;
        string reference = work.Work.Workspace!.EnvironmentReference;
        fixture.Runtime.FailCleanup = true;
        fixture.Runtime.Observations[first] = new(ObservationKind.Failed, Failure: FailureKind.RuntimeDisconnected);
        await fixture.Reconcile(id, first);
        work = await fixture.Until(id, x => x.Work.Attention?.Reason == AttentionReason.CleanupRequired);
        await Assert.ThrowsAsync<WorkRuleException>(() => fixture.Apply(WorkCommands.Retry(NextId(), id, work.Version)));
        fixture.Runtime.FailCleanup = false;
        await fixture.Reconcile(id, first);
        work = await fixture.Until(id, x => x.Work.Attention?.Reason == AttentionReason.Failure && !x.Work.Attempts[^1].CleanupPending);
        await fixture.RestartAsync();
        work = await fixture.Get(id);
        Assert.Equal(reference, work.Work.Workspace!.EnvironmentReference);
        await fixture.ExecuteAndAuthorize(WorkCommands.Retry(NextId(), id, work.Version));
        work = await fixture.Until(id, x => x.Work.Attempts.Length == 2 && x.Work.Attempts[^1].Status == AttemptStatus.Starting);
        Assert.Equal(reference, work.Work.Workspace!.EnvironmentReference);
        Assert.Equal(reference, work.Work.Attempts[^1].EnvironmentReference);
        Assert.NotEqual(first, work.Work.Workspace.AttemptId);
        Assert.Equal(AttemptStatus.Failed, work.Work.Attempts[0].Status);
        Assert.NotEqual(work.Work.Attempts[0].Target.GitRepository!.Grant!.Branch, work.Work.Attempts[1].Target.GitRepository!.Grant!.Branch);
    }

    [DatabaseFact]
    public async Task GitRepositoryAccountAndBranchArePinnedAndChangesWaitForReconciliation()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Runtime.GitRepositoryExecution = true;
        using IServiceScope scope = fixture.Host.Services.CreateScope();
        GitHubStore settings = scope.ServiceProvider.GetRequiredService<GitHubStore>();
        await settings.ObserveAsync(new("first", "42", "owner"), Goblin.Contracts.GitHubConnectionStatus.Connected);
        await settings.SetGitRepositoryAsync(new(22, "owner/repo", "main", true), true, "first");
        long id = NextId();
        WorkView w = await fixture.Apply(WorkCommands.Create(NextId(), id, "Update repository"));
        w = await fixture.Apply(new(NextId(), id, WorkAction.Assign)
        {
            ExpectedVersion = w.Version,
            AgentId = 1
        });
        w = await fixture.ExecuteAndAuthorize(new(NextId(), id, WorkAction.Execute)
        {
            ExpectedVersion = w.Version,
            GitRepository = new("owner/repo", "Goblin", "goblin@example.test")
        });
        long attempt = w.Work.Attempts[^1].Id;
        GitRepositoryGrant original = w.Work.Attempts[^1].Target.GitRepository!.Grant!;
        Assert.Equal($"goblin/{id}/{attempt}", original.Branch);
        Assert.Equal("first", original.Generation);
        await Assert.ThrowsAsync<ApplicationFailure>(() => settings.BeginChangeAsync());
        await fixture.Until(id, x => x.Work.Attempts[^1].Status == AttemptStatus.Starting);
        fixture.Runtime.Observations[attempt] = new(ObservationKind.Uncertain, Failure: FailureKind.RuntimeDisconnected);
        await fixture.Reconcile(id, attempt);
        await Assert.ThrowsAsync<ApplicationFailure>(() => settings.SetGitRepositoryAsync(new(22, "owner/repo", "main", true), false, "first"));
        fixture.Runtime.Observations[attempt] = new(ObservationKind.Stopped);
        await fixture.Reconcile(id, attempt);
        await settings.BeginChangeAsync();
        await settings.ObserveAsync(new("second", "43", "another-owner"), Goblin.Contracts.GitHubConnectionStatus.Connected);
        Assert.Equal(original, (await fixture.Get(id)).Work.Attempts[^1].Target.GitRepository!.Grant);
        Assert.False((await settings.GitRepositoriesAsync()).Single().Enabled);
    }

    [DatabaseFact]
    public async Task RepositoryAccessPreservesOperationApprovalAndSetupProvenanceChecks()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Runtime.GitRepositoryExecution = true;
        using IServiceScope scope = fixture.Host.Services.CreateScope();
        GitHubStore settings = scope.ServiceProvider.GetRequiredService<GitHubStore>();
        await settings.ObserveAsync(new("first", "42", "owner"), GitHubConnectionStatus.Connected);
        await settings.SetGitRepositoryAsync(new(22, "owner/repo", "main", true), true, "first");
        WorkSnapshot work = (await fixture.StartGitRepositoryWork("owner/repo")).Work;
        AttemptSnapshot attempt = work.Attempts[^1];
        await using GoblinDbContext db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<GoblinDbContext>>().CreateDbContextAsync();
        await GitRepositoryAccess.RequireOperationAsync(db, work, attempt.Id, default);
        await GitRepositoryAccess.RequireSetupAsync(db, work, attempt.Id, attempt.TurnNumber, default);

        async Task RejectedByBoth()
        {
            Assert.Equal("repository_operation_unavailable", (await Assert.ThrowsAsync<ApplicationFailure>(() =>
                GitRepositoryAccess.RequireOperationAsync(db, work, attempt.Id, default))).Code);
            Assert.Equal("repository_setup_unavailable", (await Assert.ThrowsAsync<ApplicationFailure>(() =>
                GitRepositoryAccess.RequireSetupAsync(db, work, attempt.Id, attempt.TurnNumber, default))).Code);
        }
        foreach (string availability in new[] { "Changing", "Disconnected", "Unavailable" })
        {
            await db.GithubConnections.Where(x => x.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(x => x.Availability, availability));
            await RejectedByBoth();
        }
        await db.GithubConnections.Where(x => x.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(x => x.Availability, "Connected").SetProperty(x => x.Generation, "changed"));
        await RejectedByBoth();
        await db.GithubConnections.Where(x => x.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(x => x.Generation, "first").SetProperty(x => x.AccountId, "99"));
        await RejectedByBoth();
        await db.GithubConnections.Where(x => x.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(x => x.AccountId, "42"));
        await db.GithubRepositories.Where(x => x.Id == 22).ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, false));
        await RejectedByBoth();
        await db.GithubRepositories.Where(x => x.Id == 22).ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, true).SetProperty(x => x.Name, "owner/renamed"));
        await GitRepositoryAccess.RequireOperationAsync(db, work, attempt.Id, default);
        await Assert.ThrowsAsync<ApplicationFailure>(() => GitRepositoryAccess.RequireSetupAsync(db, work, attempt.Id, attempt.TurnNumber, default));
        await db.GithubRepositories.Where(x => x.Id == 22).ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, "owner/repo"));
        await Assert.ThrowsAsync<ApplicationFailure>(() => GitRepositoryAccess.RequireOperationAsync(db, work, attempt.Id + 1, default));
        await Assert.ThrowsAsync<ApplicationFailure>(() => GitRepositoryAccess.RequireSetupAsync(db, work, attempt.Id, attempt.TurnNumber + 1, default));

        var unapproved = new WorkSnapshot { Attempts = work.Attempts };
        await Assert.ThrowsAsync<ApplicationFailure>(() => GitRepositoryAccess.RequireOperationAsync(db, unapproved, attempt.Id, default));
        await GitRepositoryAccess.RequireSetupAsync(db, unapproved, attempt.Id, attempt.TurnNumber, default);
        GitRepositoryChange repository = attempt.Target.GitRepository!;
        var legacy = new WorkSnapshot
        {
            Attempts = [new AttemptSnapshot
            {
                Id = attempt.Id,
                Target = new(attempt.Target.Runtime, attempt.Target.ConnectionId, attempt.Target.RequestedModel,
                    new(repository.GitRepository, repository.GitAuthorName, repository.GitAuthorEmail,
                        repository.Grant! with { PolicyVersion = 1 }), attempt.Target.RequestedEffort)
            }]
        };
        await GitRepositoryAccess.RequireOperationAsync(db, legacy, attempt.Id, default);
    }

    [DatabaseFact]
    public async Task GitRepositoryPublicationIsDurableDeduplicatedAndReconciledWithoutReplay()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Runtime.GitRepositoryExecution = true;
        using IServiceScope scope = fixture.Host.Services.CreateScope();
        GitHubStore settings = scope.ServiceProvider.GetRequiredService<GitHubStore>();
        await settings.ObserveAsync(new("first", "42", "owner"), Goblin.Contracts.GitHubConnectionStatus.Connected);
        await settings.SetGitRepositoryAsync(new(22, "owner/repo", "main", true), true, "first");
        long id = NextId();
        WorkView w = await fixture.Apply(WorkCommands.Create(NextId(), id, "Publish repository"));
        w = await fixture.Apply(new(NextId(), id, WorkAction.Assign)
        {
            ExpectedVersion = w.Version,
            AgentId = 1
        });
        await fixture.ExecuteAndAuthorize(new(NextId(), id, WorkAction.Execute)
        {
            ExpectedVersion = w.Version,
            GitRepository = new("owner/repo", "Goblin", "goblin@example.test")
        });
        w = await fixture.Until(id, x => x.Work.Attempts[^1].Status == AttemptStatus.Starting);
        GitRepositoryBroker broker = fixture.Host.Services.GetRequiredService<GitRepositoryBroker>();
        string capability = await broker.PrepareAsync(w.Work, default);
        long attempt = w.Work.Attempts[^1].Id;
        await broker.AuthorizeAsync(attempt, capability, true, default);
        await Assert.ThrowsAsync<ApplicationFailure>(() => broker.AuthorizeAsync(attempt, "00", true, default));
        long operation = await broker.ReserveOperationIdAsync(attempt, default);
        fixture.Remote.Fail = true;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            broker.EnqueueAsync(attempt, operation, GitRepositoryOperationKind.Publish, new MemoryStream([1, 2, 3]), default)));
        for (int i = 0; i < 100 && (await broker.StatusAsync(attempt, operation, default)).State is GitRepositoryOperationState.Queued or GitRepositoryOperationState.Running; i++) await Task.Delay(50);
        if ((await broker.StatusAsync(attempt, operation, default)).State == GitRepositoryOperationState.Queued)
        {
            await using GoblinDbContext db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<GoblinDbContext>>().CreateDbContextAsync();
            string[] failures = await db.Database.SqlQueryRaw<string>("SELECT exception_message AS \"Value\" FROM public.wolverine_dead_letters").ToArrayAsync();
            Assert.Fail(string.Join("\n", failures));
        }
        Assert.Equal(GitRepositoryOperationState.Uncertain, (await broker.StatusAsync(attempt, operation, default)).State);
        Assert.Equal(1, fixture.Remote.Calls);
        Assert.Equal(ObservationKind.Uncertain, (await broker.ObserveAsync(w.Work, default))!.Kind);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => broker.ExecuteAsync(operation, default)));
        Assert.Equal(1, fixture.Remote.Calls);
        fixture.Remote.Reconciled = true;
        Assert.Null(await broker.ObserveAsync(w.Work, default));
        Assert.Equal(Goblin.Contracts.GitRepositoryOperationState.Succeeded, (await broker.StatusAsync(attempt, operation, default)).State);
        await Assert.ThrowsAsync<ApplicationFailure>(() => broker.EnqueueAsync(attempt, operation, GitRepositoryOperationKind.Publish, new MemoryStream([4]), default));
        Assert.Equal(1, fixture.Remote.Calls);
    }

    [DatabaseFact]
    public async Task RejectedRepositoryOperationRollsBackDispatchAndDoesNotLeakIntoTheNextUpload()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await EnableHandoffGitRepository(fixture);
        WorkView work = await fixture.StartGitRepositoryWork("owner/repo");
        GitRepositoryBroker broker = fixture.Host.Services.GetRequiredService<GitRepositoryBroker>();
        await broker.PrepareAsync(work.Work, default);
        long attempt = work.Work.Attempts[^1].Id;
        long rejected = await broker.ReserveOperationIdAsync(attempt, default);
        await fixture.RejectId("repository_operations", rejected);
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            broker.EnqueueAsync(attempt, rejected, GitRepositoryOperationKind.Checkpoint, new MemoryStream([1, 2, 3]), default));
        using IServiceScope scope = fixture.Host.Services.CreateScope();
        await using GoblinDbContext db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<GoblinDbContext>>().CreateDbContextAsync();
        Assert.False(await db.GitRepositoryOperations.AnyAsync(x => x.Id == rejected));
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("""
            SELECT count(*)::int AS "Value" FROM public.wolverine_incoming_envelopes
            WHERE message_type LIKE '%ExecuteGitRepositoryOperation%'
            """).SingleAsync());
        Assert.Equal(0, fixture.Remote.Calls);
        long accepted = await broker.ReserveOperationIdAsync(attempt, default);
        await broker.EnqueueAsync(attempt, accepted, GitRepositoryOperationKind.Checkpoint, new MemoryStream([4, 5, 6]), default);
        for (int i = 0; i < 100 && (await broker.StatusAsync(attempt, accepted, default)).State != GitRepositoryOperationState.Succeeded; i++) await Task.Delay(25);
        Assert.Equal(GitRepositoryOperationState.Succeeded, (await broker.StatusAsync(attempt, accepted, default)).State);
        Assert.Equal(0, fixture.Remote.Calls);
    }

    [DatabaseFact]
    public async Task CancellationDuringRepositoryPublicationRetainsUncertaintyUntilReconciled()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await EnableHandoffGitRepository(fixture);
        WorkView work = await fixture.StartGitRepositoryWork("owner/repo", "Publish repository");
        GitRepositoryBroker broker = fixture.Host.Services.GetRequiredService<GitRepositoryBroker>();
        await broker.PrepareAsync(work.Work, default);
        long attempt = work.Work.Attempts[^1].Id;
        long operation = await broker.ReserveOperationIdAsync(attempt, default);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Remote.Execute = async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Publication was expected to be cancelled");
        };
        await broker.EnqueueAsync(attempt, operation, GitRepositoryOperationKind.Publish, new MemoryStream([1, 2, 3]), default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ApplicationFailure>(() => broker.StopAsync(work.Work, default));
        Assert.Equal(GitRepositoryOperationState.Uncertain, (await broker.StatusAsync(attempt, operation, default)).State);
        await broker.ExecuteAsync(operation, default);
        Assert.Equal(1, fixture.Remote.Calls);
        fixture.Remote.Reconciled = true;
        await broker.StopAsync(work.Work, default);
        Assert.Equal(GitRepositoryOperationState.Succeeded, (await broker.StatusAsync(attempt, operation, default)).State);
        Assert.Equal(1, fixture.Remote.Calls);
    }

    [DatabaseFact]
    public async Task RepositoryPublicationOutageEvidenceSurvivesRestartWithoutReplayingTheRemoteCall()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await EnableHandoffGitRepository(fixture);
        WorkView work = await fixture.StartGitRepositoryWork("owner/repo", "Publish repository");
        GitRepositoryBroker broker = fixture.Host.Services.GetRequiredService<GitRepositoryBroker>();
        await broker.PrepareAsync(work.Work, default);
        long attempt = work.Work.Attempts[^1].Id;
        long operation = await broker.ReserveOperationIdAsync(attempt, default);
        var unavailable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Remote.Execute = async _ =>
        {
            await fixture.SetDatabaseAvailable(false);
            unavailable.SetResult();
            throw new IOException("Lost publication response");
        };
        string evidence = Path.Combine(fixture.Host.Services.GetRequiredService<GitRepositoryBrokerOptions>().Directory,
            operation.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".failed");
        try
        {
            await broker.EnqueueAsync(attempt, operation, GitRepositoryOperationKind.Publish, new MemoryStream([1, 2, 3]), default);
            await unavailable.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (int i = 0; i < 200 && !File.Exists(evidence); i++) await Task.Delay(25);
            Assert.True(File.Exists(evidence));
        }
        finally { await fixture.SetDatabaseAvailable(true); }
        await fixture.RestartAsync();
        broker = fixture.Host.Services.GetRequiredService<GitRepositoryBroker>();
        Assert.Equal(ObservationKind.Uncertain, (await broker.ObserveAsync(work.Work, default))!.Kind);
        Assert.False(File.Exists(evidence));
        Assert.Equal(GitRepositoryOperationState.Uncertain, (await broker.StatusAsync(attempt, operation, default)).State);
        await broker.ExecuteAsync(operation, default);
        Assert.Equal(1, fixture.Remote.Calls);
        fixture.Remote.Reconciled = true;
        Assert.Null(await broker.ObserveAsync(work.Work, default));
        Assert.Equal(GitRepositoryOperationState.Succeeded, (await broker.StatusAsync(attempt, operation, default)).State);
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
        WorkCommand create = WorkCommands.Create(commandId, values[0], "Exact database identity");
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
        WorkCommand command = WorkCommands.AddContext(commandId, first[2], work.Version, "Work context");
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
        WorkCommand create = WorkCommands.Create(NextId(), id, "Produce a reviewable answer");
        WorkView[] created = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => fixture.Apply(create)));
        Assert.All(created, x => Assert.Equal(1, x.Version));
        Assert.Equal(new[] { WorkEventKind.Created, WorkEventKind.Assigned }, (await fixture.Get(id)).Work.History.Select(x => x.Kind));
        WorkView assigned = await fixture.Apply(new(NextId(), id, WorkAction.Assign)
        {
            ExpectedVersion = 1,
            AgentId = WorkStore.DefaultAgentId
        });
        WorkView queued = await fixture.Apply(WorkCommands.Execute(NextId(), id, assigned.Version));
        long attempt = queued.Work.Attempts.Single().Id;
        await fixture.Until(id, x => x.Work.Attempts[^1].Status == AttemptStatus.Starting);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => fixture.Host.Services.GetRequiredService<ExecutionCoordinator>().DispatchAsync(new(id, attempt), CancellationToken.None)));
        Assert.Equal(1, fixture.Runtime.Starts[attempt]);
        fixture.Runtime.Observations[attempt] = new(ObservationKind.Result, new("test-model", "opaque-session", "opaque-operation"), "Reviewed outcome");
        await fixture.Host.Services.GetRequiredService<ExecutionCoordinator>().ReconcileAsync(new(id, attempt), CancellationToken.None);
        WorkView review = await fixture.Get(id);
        Assert.Equal(AttentionReason.ResultReview, review.Work.Attention!.Reason);
        WorkCommand approve = WorkCommands.Approve(NextId(), id, review.Version, attempt);
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
        await Assert.ThrowsAsync<WorkRuleException>(() => fixture.Apply(WorkCommands.Retry(NextId(), id, uncertain.Version)));
        using (IServiceScope scope = fixture.Host.Services.CreateScope())
            await Assert.ThrowsAsync<ApplicationFailure>(() => scope.ServiceProvider.GetRequiredService<ConnectionStore>().BeginChangeAsync(ConnectionStore.DefaultConnectionId));
        await fixture.RestartAsync();
        Assert.Equal(1, fixture.Runtime.Starts[attempt]);
        fixture.Runtime.Observations[attempt] = new(ObservationKind.Stopped);
        await fixture.Reconcile(id, attempt);
        WorkView failed = await fixture.Get(id);
        Assert.Equal(AttentionReason.Failure, failed.Work.Attention!.Reason);
        Assert.Single(fixture.Runtime.Starts);
        WorkView retried = await fixture.Apply(WorkCommands.Retry(NextId(), id, failed.Version));
        Assert.Equal(2, retried.Work.Attempts.Length);
        Assert.NotEqual(attempt, retried.Work.Attempts[^1].Id);
    }

    [DatabaseFact]
    public async Task InvalidCommandRollsBackStateReceiptAndDispatch()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        long id = NextId(), rejected = NextId();
        await fixture.Apply(WorkCommands.Create(NextId(), id, "Reject an unavailable model"));
        await Assert.ThrowsAsync<ApplicationFailure>(() => fixture.Apply(WorkCommands.Execute(rejected, id, 1, new WorkModelSelection("unavailable-fixture-model", null))));
        using IServiceScope scope = fixture.Host.Services.CreateScope();
        await using GoblinDbContext db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<GoblinDbContext>>().CreateDbContextAsync();
        Assert.False(await db.WorkCommands.AnyAsync(x => x.Id == rejected));
        Assert.False(await db.ExecutionAttempts.AnyAsync(x => x.WorkId == id));
        Assert.Equal(1, (await fixture.Get(id)).Version);
        Assert.Empty(fixture.Runtime.Starts);
        await Assert.ThrowsAsync<ApplicationFailure>(() => fixture.Apply(new(NextId(), id, WorkAction.Assign)
        {
            ExpectedVersion = 99,
            AgentId = WorkStore.DefaultAgentId
        }));
    }

    [DatabaseFact]
    public async Task ReusedWorkStoreIsolatesConcurrentCommandsAndFailedDispatchTransactions()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        using IServiceScope scope = fixture.Host.Services.CreateScope();
        WorkStore store = scope.ServiceProvider.GetRequiredService<WorkStore>();
        long id = NextId(), rejected = NextId();
        WorkCommand create = WorkCommands.Create(NextId(), id, "Keep each operation independent");
        WorkView[] created = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.ApplyAsync(create)));
        Assert.All(created, x => Assert.Equal(1, x.Version));
        WorkView assigned = await store.ApplyAsync(new(NextId(), id, WorkAction.Assign)
        {
            ExpectedVersion = 1,
            AgentId = WorkStore.DefaultAgentId
        });

        // Fail the database write after dispatch has been published into the
        // transaction, rather than rejecting the command before publishing.
        await fixture.RejectId("work_commands", rejected);
        await Assert.ThrowsAsync<DbUpdateException>(() => store.ApplyAsync(WorkCommands.Execute(rejected, id, assigned.Version)));
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

        WorkView queued = await store.ApplyAsync(WorkCommands.Execute(NextId(), id, assigned.Version));
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
        Assert.Equal(WorkStore.DefaultAgentId, work.Work.AgentId);
        Assert.Equal(WorkStatus.Ready, work.Work.Status);
        Assert.Empty(work.Work.Attempts);
        await using GoblinDbContext trackedDb = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<GoblinDbContext>>().CreateDbContextAsync();
        Assert.Equal(WorkStore.DefaultAgentId, (await trackedDb.WorkItems.SingleAsync(x => x.Id == workId)).AgentId);
    }

    [DatabaseFact]
    public async Task ConversationAndWorkCommandsPreserveSharedHistoryAcrossRestart()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        using IServiceScope scope = fixture.Host.Services.CreateScope();
        ConversationStore conversations = scope.ServiceProvider.GetRequiredService<ConversationStore>();
        long conversationId = NextId(), workId = NextId();
        await conversations.ApplyAsync(new(conversationId, NextId(), "Track shared history"));
        await conversations.ApplyAsync(new(conversationId, NextId(), "Context before tracking"));
        var track = new ConversationCommand(conversationId, NextId(), null, workId);
        await conversations.ApplyAsync(track);
        await conversations.ApplyAsync(track);
        WorkView tracked = await fixture.Get(workId);
        Assert.Equal(1, tracked.Version);
        Assert.Equal("Context before tracking", Assert.Single(tracked.Work.Messages).Text);

        WorkView commandContext = await fixture.Apply(new(NextId(), workId, WorkAction.AddContext)
        {
            ExpectedVersion = tracked.Version,
            Text = "Context from a Work command"
        });
        await fixture.Apply(WorkCommands.Execute(NextId(), workId, commandContext.Version));
        WorkView running = await fixture.Until(workId, x => x.Work.Attempts[^1].Status == AttemptStatus.Starting);
        AttemptSnapshot original = Assert.Single(running.Work.Attempts);
        var message = new ConversationCommand(conversationId, NextId(), "Context during execution");
        await conversations.ApplyAsync(message);
        await conversations.ApplyAsync(message);
        WorkView updated = await fixture.Get(workId);
        Assert.Equal(running.Version + 1, updated.Version);
        Assert.Equal(running.Work.Status, updated.Work.Status);
        Assert.Equal(JsonSerializer.Serialize(original, ContractJson.Options),
            JsonSerializer.Serialize(Assert.Single(updated.Work.Attempts), ContractJson.Options));
        Assert.Equal(new[] { "Context before tracking", "Context from a Work command", "Context during execution" },
            updated.Work.Messages.Select(x => x.Text));

        await using (GoblinDbContext db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<GoblinDbContext>>().CreateDbContextAsync())
        {
            Persistence.Entities.WorkItem row = await db.WorkItems.AsNoTracking().SingleAsync(x => x.Id == workId);
            Persistence.Entities.ExecutionAttempt attempt = await db.ExecutionAttempts.AsNoTracking().SingleAsync(x => x.Id == original.Id);
            Assert.Equal(updated.Version, row.Version);
            Assert.Equal(updated.Work.Status.ToString(), row.Status);
            Assert.Equal(updated.Work.AgentId, row.AgentId);
            Assert.Equal(original.Status.ToString(), attempt.Status);
            Assert.Equal(original.OwnerId, attempt.OwnerId);
            Assert.Equal(original.EnvironmentReference, attempt.EnvironmentReference);
        }

        await fixture.RestartAsync();
        WorkView recovered = await fixture.Get(workId);
        Assert.Equal(updated.Version, recovered.Version);
        Assert.Equal(JsonSerializer.Serialize(updated.Work, ContractJson.Options),
            JsonSerializer.Serialize(recovered.Work, ContractJson.Options));
        Assert.Equal(1, fixture.Runtime.Starts[original.Id]);
    }

    [DatabaseFact]
    public async Task CancellationWaitsForStoppingEvidenceAndFailureIsNeverRetried()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        WorkView running = await fixture.StartWork();
        long id = running.Work.Id, attempt = running.Work.Attempts[^1].Id;
        await fixture.Apply(new(NextId(), id, WorkAction.Cancel)
        {
            ExpectedVersion = running.Version
        });
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
            await scope.ServiceProvider.GetRequiredService<ConnectionStore>().ObserveAvailabilityAsync(ConnectionStore.DefaultConnectionId, Goblin.Contracts.ConnectionAvailability.Disconnected);
        long id = NextId();
        WorkView w = await fixture.Apply(WorkCommands.Create(NextId(), id, "Unavailable connection"));
        w = await fixture.Apply(new(NextId(), id, WorkAction.Assign)
        {
            ExpectedVersion = w.Version,
            AgentId = WorkStore.DefaultAgentId
        });
        w = await fixture.Apply(WorkCommands.Execute(NextId(), id, w.Version));
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
        await Assert.ThrowsAsync<WorkRuleException>(() => fixture.Apply(WorkCommands.Approve(NextId(), id, blocked.Version, attempt)));
        await fixture.RestartAsync();
        fixture.Runtime.FailCleanup = false;
        Assert.Equal(AttentionReason.CleanupRequired, (await fixture.Get(id)).Work.Attention!.Reason);
        await fixture.Reconcile(id, attempt);
        WorkView review = await fixture.Get(id);
        Assert.Equal(AttentionReason.ResultReview, review.Work.Attention!.Reason);
        Assert.False(review.Work.Attempts[^1].CleanupPending);
        await fixture.Apply(WorkCommands.Approve(NextId(), id, review.Version, attempt));
        Assert.Equal(1, fixture.Runtime.Starts[attempt]);
    }

    [DatabaseFact]
    public async Task VerificationReservationsPreventDispatchAndPreserveWaitingAccountChanges()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        using IServiceScope scope = fixture.Host.Services.CreateScope();
        ConnectionStore store = scope.ServiceProvider.GetRequiredService<ConnectionStore>();
        await store.BeginVerificationAsync(ConnectionStore.DefaultConnectionId);
        await Assert.ThrowsAsync<ApplicationFailure>(() => store.BeginVerificationAsync(ConnectionStore.DefaultConnectionId));
        long id = NextId();
        WorkView work = await fixture.Apply(WorkCommands.Create(NextId(), id, "Wait for verification"));
        work = await fixture.Apply(new(NextId(), id, WorkAction.Assign)
        {
            ExpectedVersion = work.Version,
            AgentId = WorkStore.DefaultAgentId
        });
        work = await fixture.Apply(WorkCommands.Execute(NextId(), id, work.Version));
        ExecutionCoordinator coordinator = fixture.Host.Services.GetRequiredService<ExecutionCoordinator>();
        long attempt = work.Work.Attempts[^1].Id;
        await coordinator.DispatchAsync(new(id, attempt), CancellationToken.None);
        Assert.Empty(fixture.Runtime.Starts);
        Assert.Equal(WorkStatus.Queued, (await fixture.Get(id)).Work.Status);
        await store.BeginChangeAsync(ConnectionStore.DefaultConnectionId);
        await store.EndVerificationAsync(ConnectionStore.DefaultConnectionId, true);
        Assert.Equal(Goblin.Contracts.ConnectionAvailability.Changing, (await store.ListAsync()).Single().Availability);
        await store.CompleteChangeAsync(ConnectionStore.DefaultConnectionId, Goblin.Contracts.ConnectionAvailability.Available, null);
        await coordinator.DispatchAsync(new(id, attempt), CancellationToken.None);
        Assert.Equal(1, fixture.Runtime.Starts[attempt]);
        await Assert.ThrowsAsync<ApplicationFailure>(() => store.BeginVerificationAsync(ConnectionStore.DefaultConnectionId));
    }

    [DatabaseFact]
    public async Task ConnectionObservationsPreserveReservationsUntilRestartRecovery()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        using IServiceScope scope = fixture.Host.Services.CreateScope();
        ConnectionStore store = scope.ServiceProvider.GetRequiredService<ConnectionStore>();
        long id = ConnectionStore.DefaultConnectionId;
        await store.BeginVerificationAsync(id);
        Assert.False(await store.ObserveAccountAsync(id, ConnectionAvailability.Disconnected, "other-account"));
        await store.ObserveAvailabilityAsync(id, ConnectionAvailability.Unavailable);
        Assert.Equal(ConnectionAvailability.Verifying, (await store.ListAsync()).Single().Availability);
        await store.BeginChangeAsync(id);
        Assert.False(await store.ObserveAccountAsync(id, ConnectionAvailability.Available, "other-account"));
        await store.ObserveAvailabilityAsync(id, ConnectionAvailability.Unavailable);
        await store.EndVerificationAsync(id, true);
        Assert.Equal(ConnectionAvailability.Changing, (await store.ListAsync()).Single().Availability);
        await Assert.ThrowsAsync<ApplicationFailure>(() => store.BeginChangeAsync(id));
        await fixture.RestartAsync();
        using IServiceScope recovered = fixture.Host.Services.CreateScope();
        Assert.Equal(ConnectionAvailability.Unavailable,
            (await recovered.ServiceProvider.GetRequiredService<ConnectionStore>().ListAsync()).Single().Availability);
    }

    [DatabaseFact]
    public async Task ConnectionAccountChangesInvalidateCatalogsWithoutChangingWorkHistory()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        WorkView work = await fixture.Apply(WorkCommands.Create(NextId(), NextId(), "Preserve durable history"));
        using IServiceScope scope = fixture.Host.Services.CreateScope();
        ConnectionStore store = scope.ServiceProvider.GetRequiredService<ConnectionStore>();
        long id = ConnectionStore.DefaultConnectionId;
        Assert.True(await store.ObserveAccountAsync(id, ConnectionAvailability.Available, "first-account"));
        await using GoblinDbContext db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<GoblinDbContext>>().CreateDbContextAsync();
        long generation = await db.Connections.Where(x => x.Id == id).Select(x => x.AuthGeneration).SingleAsync();
        db.ConnectionModelCatalogs.Add(new()
        {
            ConnectionId = id,
            AuthGeneration = generation,
            Catalog = "[]",
            ExecutableStamp = "test"
        });
        await db.SaveChangesAsync();
        Assert.False(await store.ObserveAccountAsync(id, ConnectionAvailability.Available, "first-account"));
        await store.ObserveAvailabilityAsync(id, ConnectionAvailability.Unavailable);
        Assert.True(await db.ConnectionModelCatalogs.AnyAsync(x => x.ConnectionId == id));
        await store.BeginChangeAsync(id);
        // Completing an explicit change invalidates discovery even if the
        // provider presents the same account signature again.
        Assert.True(await store.CompleteChangeAsync(id, ConnectionAvailability.Available, "first-account"));
        Assert.False(await db.ConnectionModelCatalogs.AnyAsync(x => x.ConnectionId == id));
        Assert.Equal(generation + 1, await db.Connections.Where(x => x.Id == id).Select(x => x.AuthGeneration).SingleAsync());
        Assert.Equal(work.Version, (await fixture.Get(work.Work.Id)).Version);
        Assert.Equal(work.Work.History, (await fixture.Get(work.Work.Id)).Work.History);
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
        await Assert.ThrowsAsync<WorkRuleException>(() => fixture.Apply(WorkCommands.Retry(NextId(), id, uncertain.Version)));
        Assert.Equal(1, fixture.Runtime.Starts[attempt]);
    }

    [DatabaseFact]
    public async Task ConnectionServiceOwnsVerificationReservationsAndCompletesChangesAfterCancellation()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        var authentication = new CodexAuthentication();
        var service = new ConnectionService(authentication, fixture.Host.Services.GetRequiredService<ConnectionStore>(),
            fixture.Host.Services.GetRequiredService<ModelCatalogStore>());
        ConnectionStore store = fixture.Host.Services.GetRequiredService<ConnectionStore>();
        Task<PromptResult> prompt = service.PromptAsync("Verify", default);
        await authentication.PromptEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ConnectionAvailability.Verifying, (await store.ListAsync()).Single().Availability);
        await Assert.ThrowsAsync<ApplicationFailure>(() => service.PromptAsync("Overlap", default));
        Assert.Equal(1, authentication.PromptCalls);

        using var cancelled = new CancellationTokenSource();
        Task<AuthenticationState> change = service.LogoutAsync(cancelled.Token);
        try
        {
            await fixture.UntilConnection(ConnectionAvailability.Changing);
            cancelled.Cancel();
        }
        finally { authentication.PromptRelease.TrySetResult(); }
        await prompt;
        await change;
        Assert.Equal(ConnectionAvailability.Disconnected, (await store.ListAsync()).Single().Availability);

        authentication.FailPrompt = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PromptAsync("Fail", default));
        Assert.Equal(ConnectionAvailability.Unavailable, (await store.ListAsync()).Single().Availability);
        Assert.Equal(2, authentication.PromptCalls);
    }

    private sealed class CodexAuthentication : ICodexAuthentication
    {
        public TaskCompletionSource PromptEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PromptRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int PromptCalls { get; private set; }
        public bool FailPrompt { get; set; }

        public async Task<PromptResult> SendPromptAsync(string? value, CancellationToken cancellationToken = default)
        {
            PromptCalls++;
            if (FailPrompt) throw new InvalidOperationException("Test prompt failure");
            PromptEntered.TrySetResult();
            await PromptRelease.Task.WaitAsync(cancellationToken);
            return new("Verified", "model", 1, AuthenticationMethod.ApiKey);
        }

        public async Task<AuthenticationState> LogoutAsync()
        {
            await PromptRelease.Task;
            return new(null, null, null, null, true);
        }

        public Task<AuthenticationState> StatusAsync() => throw new NotSupportedException();
        public Task<AuthenticationState> LoginChatGPTAsync() => throw new NotSupportedException();
        public Task<AuthenticationState> LoginApiKeyAsync(string? value) => throw new NotSupportedException();
        public Task<AuthenticationState> CancelLoginAsync() => throw new NotSupportedException();
    }

    private sealed class GitRepositoryRemote : IGitRepositoryRemote
    {
        public bool Fail { get; set; }
        public bool Reconciled { get; set; }
        public Func<CancellationToken, Task<GitRepositoryOperationResult>>? Execute { get; set; }
        public Func<CancellationToken, Task<GitRepositoryOperationResult?>>? Reconcile { get; set; }
        public string InspectedCommit { get; set; } = new string('a', 40);
        public int ReconciliationCalls { get; private set; }
        public int Calls { get; private set; }
        public int CheckpointPreparations { get; private set; }

        public Task PrepareCheckpointAsync(GitRepositoryChange gitRepository, string directory, WorkspaceCheckpoint checkpoint, CancellationToken token)
        { CheckpointPreparations++; return PrepareAsync(gitRepository, directory, checkpoint.Branch, token); }

        public Task PrepareAsync(GitRepositoryChange gitRepository, string directory, string? checkpoint, CancellationToken token) { Directory.CreateDirectory(directory); return Task.CompletedTask; }

        public Task<string> InspectBundleAsync(GitRepositoryChange gitRepository, string directory, string bundle, CancellationToken token) => Task.FromResult(InspectedCommit);

        public Task<GitRepositoryOperationResult> ExecuteAsync(GitRepositoryChange gitRepository, string directory, GitRepositoryOperationKind operation, string commit, CancellationToken token)
        {
            Calls++;
            if (Execute is not null) return Execute(token);
            if (Fail) throw new IOException("Lost upstream response");
            return Task.FromResult(new GitRepositoryOperationResult(commit, "https://github.com/owner/repo/tree/" + gitRepository.Grant!.Branch));
        }

        public Task<GitRepositoryOperationResult?> ReconcileAsync(GitRepositoryChange gitRepository, string directory, GitRepositoryOperationKind operation, string commit, CancellationToken token)
        {
            ReconciliationCalls++;
            return Reconcile is not null ? Reconcile(token) :
                Task.FromResult(Reconciled ? new GitRepositoryOperationResult(commit, "https://github.com/owner/repo/tree/" + gitRepository.Grant!.Branch) : null);
        }
    }

    private sealed class Runtime : IExecutionHost
    {
        public ConcurrentDictionary<long, int> Starts { get; } = new();
        public ConcurrentDictionary<long, ExecutionObservation> Observations { get; } = new();
        public bool FailCleanup { get; set; }
        public bool GitRepositoryExecution { get; set; }
        public RuntimeCapabilities[] Capabilities => [new("codex", true, GitRepositoryExecution, true, false, false)];

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

        public Task<InspectionObservation> ObserveAsync(InspectionAllocation session, CancellationToken token) =>
            Task.FromResult(Started.GetValueOrDefault(session.Id) ? InspectionObservation.Running : InspectionObservation.Missing);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _app;
        private readonly string _name;
        private readonly string _directory;
        private readonly IGitRepositoryCatalog? _catalog;

        public Fixture(string app, string name, string directory, IGitRepositoryCatalog? catalog)
        {
            _app = app;
            _name = name;
            _directory = directory;
            _catalog = catalog;
        }

        public Runtime Runtime { get; } = new();
        public GitRepositoryRemote Remote { get; } = new();
        public InspectionRuntime Inspection { get; } = new();
        public IHost Host { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync(IGitRepositoryCatalog? catalog = null)
        {
            var admin = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(Env.GoblinTestPostgresAdmin));
            var app = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(Env.GoblinTestPostgresApp));
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
            string? repo = Environment.CurrentDirectory;
            while (repo is not null && !Directory.Exists(Path.Combine(repo, ".git"))) repo = Path.GetDirectoryName(repo);
            if (repo is null) throw new InvalidOperationException("Repository directory not found");
            var fixture = new Fixture(app.ConnectionString, name, Path.Combine(repo, ".artifacts", "durability-tests", name), catalog);
            await fixture.StartAsync();
            using IServiceScope scope = fixture.Host.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ConnectionStore>().ObserveAvailabilityAsync(ConnectionStore.DefaultConnectionId, Goblin.Contracts.ConnectionAvailability.Available);
            return fixture;
        }

        private async Task StartAsync()
        {
            HostApplicationBuilder builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddGoblinPersistence(_app);
            builder.Services.AddWorkApplication();
            builder.Services.AddSingleton<IModelCatalogSource, CatalogSource>();
            builder.Services.AddModelCatalog();
            if (_catalog is not null) builder.Services.AddSingleton(_catalog);
            builder.Services.AddSingleton<IExecutionHost>(Runtime);
            builder.Services.AddSingleton(new WorkspaceLimits());
            builder.Services.AddSingleton<WorkspaceCheckpoints>();
            builder.Services.AddSingleton<IWorkspaceCheckpoints>(services => services.GetRequiredService<WorkspaceCheckpoints>());
            builder.Services.AddSingleton<IInspectionHost>(Inspection);
            builder.Services.AddSingleton<InspectionCoordinator>();
            builder.Services.AddSingleton<IGitRepositoryRemote>(Remote);
            builder.Services.AddSingleton(new GitRepositoryBrokerOptions(Path.Combine(_directory, "repositories")));
            builder.Services.AddSingleton<GitRepositoryBroker>();
            builder.Services.AddSingleton<IDispatchFailureJournal>(new FileDispatchFailureJournal(_directory));
            builder.UseWolverine(o => ApplicationServices.ConfigureMessaging(o, _app, inspectionEnabled: true));
            Host = builder.Build();
            await Host.StartAsync();
        }

        public async Task RestartAsync() { await Host.StopAsync(); Host.Dispose(); await StartAsync(); }

        public async Task UntilConnection(ConnectionAvailability availability)
        {
            ConnectionStore store = Host.Services.GetRequiredService<ConnectionStore>();
            for (int i = 0; i < 100; i++)
            {
                if ((await store.ListAsync()).Single().Availability == availability) return;
                await Task.Delay(25);
            }
            throw new TimeoutException("Connection did not reach the expected state.");
        }

        public async Task RejectId(string table, long id)
        {
            var admin = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(Env.GoblinTestPostgresAdmin)) { Database = _name };
            await using var connection = new NpgsqlConnection(admin.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"ALTER TABLE public.{table} ADD CONSTRAINT rejected_test_id CHECK (id <> '{id}')", connection);
            await command.ExecuteNonQueryAsync();
        }

        public async Task SetDatabaseAvailable(bool available)
        {
            await using var admin = new NpgsqlConnection(Environment.GetEnvironmentVariable(Env.GoblinTestPostgresAdmin));
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

        public Task<WorkView> Authorize(WorkView work) => work.Work.GitRepositoryAuthorization?.Status == GitRepositoryAuthorizationStatus.Authorized
            ? Task.FromResult(work) : Apply(new(NextId(), work.Work.Id, WorkAction.AuthorizeGitRepository)
            {
                ExpectedVersion = work.Version,
                AuthorizationId = work.Work.GitRepositoryAuthorization!.Id
            });

        public async Task<WorkView> ExecuteAndAuthorize(WorkCommand command)
        {
            WorkView view = await Apply(command);
            return view.Work.GitRepositoryAuthorization?.Status == GitRepositoryAuthorizationStatus.Pending ? await Authorize(view) : view;
        }

        public async Task<WorkView> Get(long id) { using IServiceScope scope = Host.Services.CreateScope(); return await scope.ServiceProvider.GetRequiredService<WorkStore>().GetAsync(id); }

        public Task Reconcile(long id, long attempt) => Host.Services.GetRequiredService<ExecutionCoordinator>().ReconcileAsync(new(id, attempt), CancellationToken.None);

        public async Task<WorkView> StartWork()
        {
            long id = NextId();
            WorkView w = await Apply(WorkCommands.Create(NextId(), id, "Test durable execution"));
            w = await Apply(new(NextId(), id, WorkAction.Assign)
            {
                ExpectedVersion = w.Version,
                AgentId = WorkStore.DefaultAgentId
            });
            await Apply(WorkCommands.Execute(NextId(), id, w.Version));
            return await Until(id, x => x.Work.Attempts[^1].Status == AttemptStatus.Starting);
        }

        public Task<WorkView> StartGitRepositoryWork(string gitRepository) =>
            StartGitRepositoryWork(gitRepository, "Repository setup memory");

        public async Task<WorkView> StartGitRepositoryWork(string gitRepository, string objective)
        {
            long id = NextId();
            WorkView work = await Apply(WorkCommands.Create(NextId(), id, objective));
            work = await Apply(new(NextId(), id, WorkAction.Assign)
            {
                ExpectedVersion = work.Version,
                AgentId = 1
            });
            await ExecuteAndAuthorize(new(NextId(), id, WorkAction.Execute)
            {
                ExpectedVersion = work.Version,
                GitRepository = new(gitRepository, "Goblin", "agent@example.com")
            });
            return await Until(id, x => x.Work.Attempts[^1].Status == AttemptStatus.Starting);
        }

        public async Task<WorkspaceCheckpoint> SaveCheckpoint(WorkSnapshot work)
        {
            long attempt = work.Attempts[^1].Id;
            GitRepositoryBroker broker = Host.Services.GetRequiredService<GitRepositoryBroker>();
            await broker.PrepareAsync(work, default);
            long publication = await broker.ReserveOperationIdAsync(attempt, default);
            await broker.EnqueueAsync(attempt, publication, work.Attempts[^1].Target.GitRepository!.Grant!.AllowPush ? GitRepositoryOperationKind.Publish : GitRepositoryOperationKind.Checkpoint, new MemoryStream([1, 2, 3]), default);
            for (int i = 0; i < 100 && (await broker.StatusAsync(attempt, publication, default)).State != GitRepositoryOperationState.Succeeded; i++) await Task.Delay(50);
            Assert.Equal(Goblin.Contracts.GitRepositoryOperationState.Succeeded, (await broker.StatusAsync(attempt, publication, default)).State);
            return await Host.Services.GetRequiredService<WorkspaceCheckpointCoordinator>()
                .SaveAsync(attempt, work.Attempts[^1].TurnNumber, new string('a', 40), default);
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
            await using var db = new NpgsqlConnection(Environment.GetEnvironmentVariable(Env.GoblinTestPostgresAdmin));
            await db.OpenAsync();
            await new NpgsqlCommand("DROP DATABASE " + _name + " WITH (FORCE)", db).ExecuteNonQueryAsync();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }
}
