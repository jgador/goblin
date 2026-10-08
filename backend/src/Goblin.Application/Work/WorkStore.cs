using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.Runtime;
using Goblin.Application.Workspaces;
using Goblin.Contracts;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;
using Goblin.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Wolverine.EntityFrameworkCore;
using AttemptRow = Goblin.Persistence.Entities.ExecutionAttempt;
using Receipt = Goblin.Persistence.Entities.WorkCommand;
using Row = Goblin.Persistence.Entities.WorkItem;

namespace Goblin.Application.Work;

// Each operation owns its context; commands also own their outbox. The database
// lock serializes short product transactions, including connection reservations.
// No runtime/network operation may run under it. PostgreSQL also enforces capacity.
public sealed partial class WorkStore
{
    private readonly IDbContextFactory<GoblinDbContext> _dbFactory;
    private readonly WorkOutboxFactory _outboxes;
    private readonly WorkspaceLimits _limits;
    private readonly IGitRepositoryCatalog? _gitRepositoryCatalog;

    public WorkStore(IDbContextFactory<GoblinDbContext> dbFactory, WorkOutboxFactory outboxes, WorkspaceLimits? limits = null, IGitRepositoryCatalog? gitRepositoryCatalog = null)
    {
        _dbFactory = dbFactory;
        _outboxes = outboxes;
        _limits = limits ?? new();
        _gitRepositoryCatalog = gitRepositoryCatalog;
    }

    public static readonly long DefaultAgentId = 1;
    // Retained for source compatibility. New consumers should take serialization
    // policy from the contracts boundary rather than from this persistence service.
    public static readonly JsonSerializerOptions Json = ContractJson.Options;

    public async Task<WorkView[]> ListAsync(CancellationToken token = default)
    {
        await using GoblinDbContext db = await _dbFactory.CreateDbContextAsync(token);
        Row[] rows = await db.WorkItems.AsNoTracking().OrderByDescending(x => x.UpdatedAt)
            .ThenBy(x => x.Id).ToArrayAsync(token);
        return [.. rows.Select(View)];
    }

    public async Task<WorkView> GetAsync(long id, CancellationToken token = default)
    {
        await using GoblinDbContext db = await _dbFactory.CreateDbContextAsync(token);
        return View(await db.WorkItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token)
            ?? throw new ApplicationFailure("work_not_found"));
    }

    public async Task<AgentView[]> AgentsAsync(CancellationToken token = default)
    {
        await using GoblinDbContext db = await _dbFactory.CreateDbContextAsync(token);
        return await db.Agents.AsNoTracking().OrderBy(x => x.Name)
            .Select(x => new AgentView(x.Id, x.Name, x.ConnectionId, x.Model)).ToArrayAsync(token);
    }

    public async Task<ConnectionView[]> ConnectionsAsync(CancellationToken token = default)
    {
        await using GoblinDbContext db = await _dbFactory.CreateDbContextAsync(token);
        return await db.Connections.AsNoTracking().OrderBy(x => x.Name)
            .Select(x => new ConnectionView(x.Id, x.Runtime, x.Name, ContractValue.Parse<ConnectionAvailability>(x.Availability))).ToArrayAsync(token);
    }

    public async Task<string[]> GitRepositorySuggestionsAsync(WorkSnapshot work, CancellationToken token = default)
    {
        await using GoblinDbContext db = await _dbFactory.CreateDbContextAsync(token);
        return await GitRepositorySuggestionsAsync(db, work, null, token);
    }

    private static async Task<string[]> GitRepositorySuggestionsAsync(GoblinDbContext db, WorkSnapshot work,
        string? answer, CancellationToken token) => GitRepositoryReferences.Find(work,
            await db.GithubRepositories.Select(x => x.Name).ToArrayAsync(token), answer);

    public async Task<WorkView> ApplyAsync(WorkCommand command, CancellationToken token = default)
    {
        WorkCommandPolicy.Validate(command);
        WorkView? replay = await ReplayAsync(command, token);
        if (replay is not null) return replay;
        GitRepositoryProposal? proposal = await ProposalAsync(command, token);
        await using GoblinDbContext db = await _dbFactory.CreateDbContextAsync(token);
        await using IDbContextTransaction transaction = await ApplicationTransaction.BeginAsync(db, token);
        IDbContextOutbox outbox = _outboxes.Create(db);
        WorkView view = await ApplyInTransactionAsync(db, outbox, command, proposal, token);
        await outbox.SaveChangesAndFlushMessagesAsync(token);
        return view;
    }

    // Conversation adapters share these exact commands and product checks.
    // Their actor authorization, source receipt and commands use one transaction.
    internal Task<WorkView> ApplyConversationCommandAsync(GoblinDbContext db, IDbContextOutbox outbox,
        WorkCommand command, CancellationToken token)
    {
        WorkCommandPolicy.ValidateConversation(command);
        return ApplyInTransactionAsync(db, outbox, command, null, token);
    }

    private async Task<WorkView> ApplyInTransactionAsync(GoblinDbContext db, IDbContextOutbox outbox,
        WorkCommand command, GitRepositoryProposal? proposal, CancellationToken token)
    {
        string fingerprint = WorkCommandPolicy.Fingerprint(command);
        Receipt? receipt = await db.WorkCommands.SingleOrDefaultAsync(x => x.Id == command.CommandId, token);
        if (receipt is not null)
        {
            if (receipt.Fingerprint != fingerprint) throw new ApplicationFailure("command_id_reused");
            return JsonSerializer.Deserialize<WorkView>(receipt.Response, ContractJson.Options)!;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Row? row = await db.WorkItems.SingleOrDefaultAsync(x => x.Id == command.WorkId, token);
        WorkItem work;
        if (command.Action == WorkAction.Create)
        {
            if (row is not null) throw new ApplicationFailure("work_already_exists");
            work = new(command.WorkId, command.Text ?? "", now);
            work.Assign(DefaultAgentId, now);
            row = new() { Id = work.Id, Objective = work.Objective, CreatedAt = now.UtcDateTime, Version = 0 };
            db.WorkItems.Add(row);
        }
        else
        {
            if (row is null) throw new ApplicationFailure("work_not_found");
            if (command.ExpectedVersion != row.Version) throw new ApplicationFailure("work_changed");
            work = Restore(row);
        }

        switch (command.Action)
        {
            case WorkAction.Create: break;
            case WorkAction.Assign:
                long agentId = command.AgentId ?? throw new ApplicationFailure("agent_required");
                if (!await db.Agents.AnyAsync(x => x.Id == agentId, token)) throw new ApplicationFailure("agent_not_found");
                work.Assign(agentId, now);
                break;
            case WorkAction.Execute:
            case WorkAction.Retry:
                Persistence.Entities.Agent agent = await db.Agents.SingleOrDefaultAsync(x => x.Id == work.AgentId, token)
                    ?? throw new ApplicationFailure("agent_required");
                Persistence.Entities.Connection connection = await db.Connections.SingleAsync(x => x.Id == agent.ConnectionId, token);
                long attemptId = await IdentitySequence.NextAsync(db, IdentityKind.Attempt, token);
                GitRepositoryChange? gitRepository = null; // Bound only by a verified proposal.
                string? model = command.ModelSelectionProvided ? command.Model :
                    command.Model ?? (command.Action == WorkAction.Retry ? work.CurrentAttempt?.Target.RequestedModel : agent.Model);
                string? effort = command.ModelSelectionProvided ? command.ReasoningEffort :
                    command.ReasoningEffort ?? (command.Action == WorkAction.Retry && command.Model is null
                        ? work.CurrentAttempt?.Target.RequestedEffort : null);
                if (command.ModelSelectionProvided || command.Model is not null || command.ReasoningEffort is not null)
                    await ModelCatalogStore.ValidateSelectionAsync(db, connection, model, effort, token);
                var target = new ExecutionTarget(connection.Runtime, connection.Id, model, gitRepository, effort);
                if (proposal is not null)
                {
                    await SaveProposalAsync(db, work, attemptId, target, proposal, command.Action == WorkAction.Retry, now, token);
                    break;
                }
                if (command.Action == WorkAction.Execute && gitRepository is null)
                {
                    string[] suggestions = await GitRepositorySuggestionsAsync(db, work.Snapshot(), null, token);
                    if (suggestions.Length > 0)
                    {
                        work.RequestGitRepositorySetup(target, suggestions, now);
                        break;
                    }
                }
                if (command.Action == WorkAction.Retry) work.RetryExecution(attemptId, target, now);
                else work.QueueExecution(attemptId, target, now);
                await outbox.PublishAsync(new DispatchWork(work.Id, attemptId));
                break;
            case WorkAction.PrepareGitRepository:
                ExecutionTarget previous = work.GitRepositoryRequest?.Target ?? work.CurrentAttempt?.Target
                    ?? throw new ApplicationFailure("invalid_command");
                await SaveProposalAsync(db, work, await IdentitySequence.NextAsync(db, IdentityKind.Attempt, token), previous,
                    proposal ?? throw new ApplicationFailure("repository_ambiguous"), work.CurrentAttempt?.Status == AttemptStatus.Failed && work.GitRepositoryAuthorization?.Retry == true, now, token);
                break;
            case WorkAction.AuthorizeGitRepository:
                GitRepositoryAuthorization approval = work.GitRepositoryAuthorization
                    ?? throw new ApplicationFailure("repository_authorization_changed");
                if (approval.Id != command.AuthorizationId || approval.Status != GitRepositoryAuthorizationStatus.Pending)
                    throw new ApplicationFailure("repository_authorization_changed");
                await GitHubStore.AcceptAsync(db, approval, token);
                work.AuthorizeGitRepository(approval.Id, approval.Target, now);
                await outbox.PublishAsync(new DispatchWork(work.Id, approval.Id));
                break;
            case WorkAction.DenyGitRepository:
                work.DenyGitRepositoryAuthorization(command.AuthorizationId ?? 0, now);
                break;
            case WorkAction.Cancel:
                work.RequestCancellation(now);
                if (work.CurrentAttempt is { Status: AttemptStatus.CancellationRequested } cancelled)
                    await outbox.PublishAsync(new ReconcileWork(work.Id, cancelled.Id));
                break;
            case WorkAction.Answer:
                if (work.CurrentAttempt?.Target.GitRepository is { } existingGitRepository)
                {
                    string[] referenced = GitRepositoryReferences.FindText(command.Text ?? "",
                        await db.GithubRepositories.Select(x => x.Name).ToArrayAsync(token));
                    if (referenced.Any(x => !x.Equals(existingGitRepository.GitRepository, StringComparison.OrdinalIgnoreCase)))
                        throw new ApplicationFailure("repository_requires_new_work");
                }
                GitRepositoryGrant? currentGrant = work.CurrentAttempt?.Target.GitRepository?.Grant;
                GitDeliveryIntent requestedDelivery = GitRepositoryIntent.Delivery(work.Snapshot(), command.Text);
                bool changedDelivery = currentGrant is not null && (currentGrant.AllowPush != requestedDelivery.Push ||
                    currentGrant.AllowPullRequest != requestedDelivery.OpenPullRequest || currentGrant.BaseBranch != requestedDelivery.BaseBranch);
                if (work.CurrentAttempt?.Target.GitRepository is null || changedDelivery)
                {
                    string[] suggestions = await GitRepositorySuggestionsAsync(db, work.Snapshot(), command.Text, token);
                    if (suggestions.Length == 0 && work.CurrentAttempt?.Target.GitRepository is { } currentGitRepository) suggestions = [currentGitRepository.GitRepository];
                    if (suggestions.Length > 0)
                    {
                        work.RequestGitRepositorySetupForAnswer(command.DecisionId ?? 0, command.Text ?? "", suggestions, now);
                        break;
                    }
                }
                work.AnswerDecision(command.DecisionId ?? 0, command.Text ?? "", now);
                if (work.CurrentAttempt is { Status: AttemptStatus.Queued } continuation)
                    await outbox.PublishAsync(new DispatchWork(work.Id, continuation.Id, continuation.TurnNumber));
                break;
            case WorkAction.RequestChanges:
                work.RequestChanges(command.AttemptId ?? 0, command.Text ?? "", now);
                break;
            case WorkAction.Approve:
                work.ApproveResult(command.AttemptId ?? 0, now);
                break;
            case WorkAction.AddContext:
                work.AddContext(await IdentitySequence.NextAsync(db, IdentityKind.Event, token), command.Text ?? "", now);
                break;
            case WorkAction.Reconcile:
                ExecutionAttempt? uncertain = work.CurrentAttempt;
                if (uncertain is null || (!uncertain.CleanupPending && uncertain.Status is not (AttemptStatus.Uncertain or AttemptStatus.CancellationRequested)))
                    throw new ApplicationFailure("reconciliation_not_required");
                await outbox.PublishAsync(new ReconcileWork(work.Id, uncertain.Id));
                break;
        }
        await SaveAsync(db, row, work, now, token);
        WorkView view = View(row);
        db.WorkCommands.Add(new()
        {
            Id = command.CommandId,
            WorkId = work.Id,
            Fingerprint = fingerprint,
            Response = JsonSerializer.Serialize(view, ContractJson.Options),
            CreatedAt = now.UtcDateTime
        });
        await db.SaveChangesAsync(token);
        return view;
    }

    // A successful claim is committed before the caller can contact the host.
    // Returning null means this delivery has no permission to start execution.
    public Task<WorkSnapshot?> ClaimAsync(DispatchWork command, string environment,
        Func<ExecutionTarget, bool> supportsRuntime, CancellationToken token = default) =>
        ClaimAsync(command, _ => environment, supportsRuntime, token);

    public async Task<WorkSnapshot?> ClaimAsync(DispatchWork command, Func<WorkSnapshot, string> environmentFor,
        Func<ExecutionTarget, bool> supportsRuntime, CancellationToken token = default)
    {
        await using GoblinDbContext db = await _dbFactory.CreateDbContextAsync(token);
        await using IDbContextTransaction transaction = await ApplicationTransaction.BeginAsync(db, token);
        IDbContextOutbox outbox = _outboxes.Create(db);
        Row? row = await db.WorkItems.SingleOrDefaultAsync(x => x.Id == command.WorkId, token);
        if (row is null) return null;
        WorkItem work = Restore(row);
        if (work.CurrentAttempt is not { Status: AttemptStatus.Queued } attempt || attempt.Id != command.AttemptId || attempt.TurnNumber != command.TurnNumber) return null;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Persistence.Entities.Connection connection = await db.Connections.SingleAsync(x => x.Id == attempt.Target.ConnectionId, token);
        if (connection.Availability is nameof(ConnectionAvailability.Changing) or nameof(ConnectionAvailability.Verifying)) return null;
        FailureKind? failure = !supportsRuntime(attempt.Target) ? FailureKind.CapabilityUnavailable :
            connection.Availability != nameof(ConnectionAvailability.Available) ? FailureKind.ConnectionUnavailable : null;
        if (attempt.Target.GitRepository?.Grant is { } grant && !await db.GithubConnections.AnyAsync(x =>
            x.Id == grant.ConnectionId && x.Generation == grant.Generation && x.AccountId == grant.AccountId && x.Availability == nameof(GitHubConnectionStatus.Connected), token))
            failure = FailureKind.ConnectionUnavailable;
        if (attempt.Target.GitRepository?.Grant is { } gitRepositoryGrant && !await db.GithubRepositories.AnyAsync(x =>
            x.Id == gitRepositoryGrant.GitRepositoryId && x.ConnectionId == gitRepositoryGrant.ConnectionId && x.Enabled &&
            x.Name == attempt.Target.GitRepository.GitRepository, token)) failure = FailureKind.ConnectionUnavailable;
        if (failure is null && attempt.Target.GitRepository is not null && !attempt.ReasoningOnly)
        {
            if (await WorkspaceSessionQueries.Active(db.WorkspaceSessions)
                .AnyAsync(x => x.WorkId == work.Id, token)) return null;
            int occupied = await ResourceReservations.CountSandboxesAsync(db,
                ResourceReservations.Attempts(db.ExecutionAttempts).Where(x => x.Id != attempt.Id), token);
            if (occupied >= _limits.MaxSandboxes) return null;
        }
        if (failure is null && await ResourceReservations.Attempts(db.ExecutionAttempts)
            .AnyAsync(x => x.Id != attempt.Id && x.ConnectionId == connection.Id, token))
        {
            // Capacity waiting is not a failed runtime operation. A periodic
            // queue scan will deliver this same unclaimed attempt when free.
            return null;
        }
        if (failure is not null) work.DispatchFailed(attempt.Id, failure.Value, now);
        else if (!work.TryClaimExecution(attempt.Id, await IdentitySequence.NextAsync(db, IdentityKind.Event, token), environmentFor(work.Snapshot()), now)) return null;
        await SaveAsync(db, row, work, now, token);
        await outbox.SaveChangesAndFlushMessagesAsync(token);
        return failure is null ? work.Snapshot() : null;
    }

    public async Task MutateAsync(long workId, Action<WorkItem> transition, CancellationToken token = default)
    {
        await using GoblinDbContext db = await _dbFactory.CreateDbContextAsync(token);
        await using IDbContextTransaction transaction = await ApplicationTransaction.BeginAsync(db, token);
        IDbContextOutbox outbox = _outboxes.Create(db);
        Row row = await db.WorkItems.SingleAsync(x => x.Id == workId, token);
        WorkItem work = Restore(row);
        long before = work.History.Count;
        int turnBefore = work.CurrentAttempt?.TurnNumber ?? 0;
        transition(work);
        if (before == work.History.Count) return;
        await SaveAsync(db, row, work, DateTimeOffset.UtcNow, token);
        if (work.CurrentAttempt is { Status: AttemptStatus.Queued } queued && queued.TurnNumber != turnBefore)
            await outbox.PublishAsync(new DispatchWork(work.Id, queued.Id, queued.TurnNumber));
        await outbox.SaveChangesAndFlushMessagesAsync(token);
    }

    public async Task<AttemptRow[]> RecoverableAsync(CancellationToken token = default)
    {
        await using GoblinDbContext db = await _dbFactory.CreateDbContextAsync(token);
        return await db.ExecutionAttempts.AsNoTracking()
            .Where(x => x.Status == nameof(AttemptStatus.Queued) || x.Status == nameof(AttemptStatus.Starting) || x.Status == nameof(AttemptStatus.Running) ||
                x.Status == nameof(AttemptStatus.CancellationRequested) || x.Status == nameof(AttemptStatus.Uncertain) || (x.CleanupPending && !x.CleanupFailed) || x.WorkspaceRetained)
            .OrderBy(x => x.QueuedAt).ToArrayAsync(token);
    }

    public async Task<bool> SetConnectionAsync(long id, ConnectionAvailability availability, bool requireIdle,
        CancellationToken token = default, bool completeChange = false,
        bool observeAccount = false, string? accountSignature = null)
    {
        if (!Enum.IsDefined(availability)) throw new ApplicationFailure("invalid_command");
        await using GoblinDbContext db = await _dbFactory.CreateDbContextAsync(token);
        await using IDbContextTransaction transaction = await ApplicationTransaction.BeginAsync(db, token);
        if (requireIdle && await ResourceReservations.Attempts(db.ExecutionAttempts).AnyAsync(x => x.ConnectionId == id, token))
            throw new ApplicationFailure("connection_in_use");
        Persistence.Entities.Connection row = await db.Connections.SingleAsync(x => x.Id == id, token);
        if (row.Availability == nameof(ConnectionAvailability.Changing))
        {
            if (requireIdle) throw new ApplicationFailure("connection_in_use");
            if (!completeChange) return false;
        }
        if (row.Availability == nameof(ConnectionAvailability.Verifying) && !requireIdle && !completeChange) return false;
        bool changedAccount = (observeAccount && row.AccountSignature != accountSignature) || completeChange;
        if (changedAccount)
        {
            row.AuthGeneration++;
            row.AccountSignature = accountSignature;
            await db.ConnectionModelCatalogs.Where(x => x.ConnectionId == id).ExecuteDeleteAsync(token);
        }
        row.Availability = availability.ToString();
        row.ChangedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return changedAccount;
    }

    public async Task BeginVerificationAsync(long id, CancellationToken token = default)
    {
        await using GoblinDbContext db = await _dbFactory.CreateDbContextAsync(token);
        await using IDbContextTransaction transaction = await ApplicationTransaction.BeginAsync(db, token);
        Persistence.Entities.Connection row = await db.Connections.SingleAsync(x => x.Id == id, token);
        if (row.Availability == nameof(ConnectionAvailability.Verifying)) throw new ApplicationFailure("prompt_in_progress");
        if (row.Availability == nameof(ConnectionAvailability.Changing) || await ResourceReservations.Attempts(db.ExecutionAttempts).AnyAsync(x => x.ConnectionId == id, token))
            throw new ApplicationFailure("connection_in_use");
        row.Availability = nameof(ConnectionAvailability.Verifying);
        row.ChangedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    public async Task EndVerificationAsync(long id, bool available)
    {
        await using GoblinDbContext db = await _dbFactory.CreateDbContextAsync();
        await using IDbContextTransaction transaction = await ApplicationTransaction.BeginAsync(db, default);
        Persistence.Entities.Connection row = await db.Connections.SingleAsync(x => x.Id == id);
        // A waiting account change owns the reservation until its operation ends.
        if (row.Availability != nameof(ConnectionAvailability.Verifying)) return;
        row.Availability = available ? nameof(ConnectionAvailability.Available) : nameof(ConnectionAvailability.Unavailable);
        row.ChangedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    // The self-hosted application has a single controller. Its verification
    // process cannot survive a controller restart; durable attempts can.
    public async Task RecoverConnectionReservationsAsync(CancellationToken token)
    {
        await using GoblinDbContext db = await _dbFactory.CreateDbContextAsync(token);
        await using IDbContextTransaction transaction = await ApplicationTransaction.BeginAsync(db, token);
        await db.Connections.Where(x => x.Availability == nameof(ConnectionAvailability.Changing) || x.Availability == nameof(ConnectionAvailability.Verifying))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Availability, nameof(ConnectionAvailability.Unavailable)).SetProperty(x => x.ChangedAt, DateTime.UtcNow), token);
        await transaction.CommitAsync(token);
    }

    private static async Task SaveAsync(GoblinDbContext db, Row row, WorkItem work, DateTimeOffset now, CancellationToken token)
    {
        row.State = JsonSerializer.Serialize(work.Snapshot(), ContractJson.Options);
        row.Status = work.Status.ToString();
        row.AgentId = work.AgentId;
        row.Version++;
        row.UpdatedAt = now.UtcDateTime;
        foreach (ExecutionAttempt attempt in work.Attempts)
        {
            AttemptRow? saved = await db.ExecutionAttempts.SingleOrDefaultAsync(x => x.Id == attempt.Id, token);
            if (saved is null)
            {
                saved = new()
                {
                    Id = attempt.Id,
                    WorkId = work.Id,
                    AgentId = attempt.AgentId,
                    ConnectionId = attempt.Target.ConnectionId,
                    GithubConnectionId = attempt.Target.GitRepository?.Grant?.ConnectionId,
                    Runtime = attempt.Target.Runtime,
                    QueuedAt = attempt.QueuedAt.UtcDateTime
                };
                db.ExecutionAttempts.Add(saved);
            }
            saved.Status = attempt.Status.ToString();
            saved.TurnNumber = attempt.TurnNumber;
            saved.WorkspaceRetained = (attempt.Status is AttemptStatus.Waiting or AttemptStatus.Queued) && !attempt.ReleaseWorkspace;
            saved.OwnerId = attempt.OwnerId;
            saved.EnvironmentReference = attempt.EnvironmentReference;
            saved.CleanupPending = attempt.CleanupPending;
            saved.CleanupFailed = attempt.CleanupFailed;
            saved.UpdatedAt = now.UtcDateTime;
        }
    }

    internal static WorkItem Restore(Row row) => row.State is null
        ? new(row.Id, row.Objective, new DateTimeOffset(row.CreatedAt, TimeSpan.Zero))
        : WorkItem.Restore(JsonSerializer.Deserialize<WorkSnapshot>(row.State, ContractJson.Options)!);

    private static WorkView View(Row row) => new(row.Version, new(row.CreatedAt, TimeSpan.Zero),
        new(row.UpdatedAt, TimeSpan.Zero), Restore(row).Snapshot());

}
