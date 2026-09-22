using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
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
public sealed class WorkStore
{
    private readonly IDbContextFactory<GoblinDbContext> _dbFactory;
    private readonly WorkOutboxFactory _outboxes;
    private readonly WorkspaceLimits _limits;

    public WorkStore(IDbContextFactory<GoblinDbContext> dbFactory, WorkOutboxFactory outboxes, WorkspaceLimits? limits = null)
    {
        _dbFactory = dbFactory;
        _outboxes = outboxes;
        _limits = limits ?? new();
    }

    public static readonly long DefaultAgentId = 1;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

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
            .Select(x => new ConnectionView(x.Id, x.Runtime, x.Name, x.Availability)).ToArrayAsync(token);
    }

    public async Task<WorkView> ApplyAsync(WorkCommand command, CancellationToken token = default)
    {
        if (command.CommandId <= 0 || command.WorkId <= 0 || !Enum.IsDefined(command.Action))
            throw new ApplicationFailure("invalid_command");
        if (command.Text?.Length > 4000) throw new ApplicationFailure("text_too_long");
        await using GoblinDbContext db = await _dbFactory.CreateDbContextAsync(token);
        await using IDbContextTransaction transaction = await BeginAsync(db, token);
        IDbContextOutbox outbox = _outboxes.Create(db);
        string fingerprint = Hash(command);
        Receipt? receipt = await db.WorkCommands.SingleOrDefaultAsync(x => x.Id == command.CommandId, token);
        if (receipt is not null)
        {
            if (receipt.Fingerprint != fingerprint) throw new ApplicationFailure("command_id_reused");
            return JsonSerializer.Deserialize<WorkView>(receipt.Response, Json)!;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Row? row = await db.WorkItems.SingleOrDefaultAsync(x => x.Id == command.WorkId, token);
        WorkItem work;
        if (command.Action == WorkAction.Create)
        {
            if (row is not null) throw new ApplicationFailure("work_already_exists");
            work = new(command.WorkId, command.Text ?? "", now);
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
                long attemptId = await IdentityStore.NextAsync(db, IdentityKind.Attempt, token);
                RepositoryChange? repository = command.Repository ?? work.CurrentAttempt?.Target.Repository;
                if (repository is not null) repository = await GitHubStore.BindAsync(db, repository, work.Id, attemptId, token);
                var target = new ExecutionTarget(connection.Runtime, connection.Id, agent.Model, repository);
                if (command.Action == WorkAction.Retry) work.RetryExecution(attemptId, target, now);
                else work.QueueExecution(attemptId, target, now);
                await outbox.PublishAsync(new DispatchWork(work.Id, attemptId));
                break;
            case WorkAction.Cancel:
                work.RequestCancellation(now);
                if (work.CurrentAttempt is { Status: AttemptStatus.CancellationRequested } cancelled)
                    await outbox.PublishAsync(new ReconcileWork(work.Id, cancelled.Id));
                break;
            case WorkAction.Answer:
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
                work.AddContext(await IdentityStore.NextAsync(db, IdentityKind.Event, token), command.Text ?? "", now);
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
            Response = JsonSerializer.Serialize(view, Json),
            CreatedAt = now.UtcDateTime
        });
        await outbox.SaveChangesAndFlushMessagesAsync(token);
        return view;
    }

    // A successful claim is committed before the caller can contact the host.
    // Returning null means this delivery has no permission to start execution.
    public async Task<WorkSnapshot?> ClaimAsync(DispatchWork command, string environment,
        Func<ExecutionTarget, bool> supportsRuntime, CancellationToken token = default)
    {
        await using GoblinDbContext db = await _dbFactory.CreateDbContextAsync(token);
        await using IDbContextTransaction transaction = await BeginAsync(db, token);
        IDbContextOutbox outbox = _outboxes.Create(db);
        Row? row = await db.WorkItems.SingleOrDefaultAsync(x => x.Id == command.WorkId, token);
        if (row is null) return null;
        WorkItem work = Restore(row);
        if (work.CurrentAttempt is not { Status: AttemptStatus.Queued } attempt || attempt.Id != command.AttemptId || attempt.TurnNumber != command.TurnNumber) return null;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Persistence.Entities.Connection connection = await db.Connections.SingleAsync(x => x.Id == attempt.Target.ConnectionId, token);
        if (connection.Availability is "Changing" or "Verifying") return null;
        FailureKind? failure = !supportsRuntime(attempt.Target) ? FailureKind.CapabilityUnavailable :
            connection.Availability != "Available" ? FailureKind.ConnectionUnavailable : null;
        if (attempt.Target.Repository?.Grant is { } grant && !await db.GithubConnections.AnyAsync(x =>
            x.Id == grant.ConnectionId && x.Generation == grant.Generation && x.AccountId == grant.AccountId && x.Availability == "Connected", token))
            failure = FailureKind.ConnectionUnavailable;
        if (failure is null && attempt.Target.Repository is not null && !attempt.ReasoningOnly)
        {
            int occupied = await db.ExecutionAttempts.CountAsync(x => x.Id != attempt.Id && x.GithubConnectionId != null &&
                (x.Status == "Starting" || x.Status == "Running" || x.Status == "Uncertain" || x.CleanupPending || x.WorkspaceRetained), token);
            occupied += await db.WorkspaceSessions.CountAsync(x => x.State == "Starting" || x.State == "Available" || x.State == "Stopping" || x.State == "NeedsAttention", token);
            long used = await db.WorkspaceCheckpoints.SumAsync(x => (long)x.Archive.Length, token);
            if (occupied >= _limits.MaxSandboxes || used >= _limits.MaxStorageBytes) return null;
        }
        if (failure is null && await db.ExecutionAttempts.AnyAsync(x => x.Id != attempt.Id && x.ConnectionId == connection.Id &&
            (x.Status == "Starting" || x.Status == "Running" || x.Status == "CancellationRequested" || x.Status == "Uncertain" || x.CleanupPending || x.WorkspaceRetained), token))
        {
            // Capacity waiting is not a failed runtime operation. A periodic
            // queue scan will deliver this same unclaimed attempt when free.
            return null;
        }
        if (failure is not null) work.DispatchFailed(attempt.Id, failure.Value, now);
        else if (!work.TryClaimExecution(attempt.Id, await IdentityStore.NextAsync(db, IdentityKind.Event, token), attempt.ReasoningOnly ? "text/" + attempt.Id + "/turn/" + attempt.TurnNumber : environment, now)) return null;
        await SaveAsync(db, row, work, now, token);
        await outbox.SaveChangesAndFlushMessagesAsync(token);
        return failure is null ? work.Snapshot() : null;
    }

    public async Task MutateAsync(long workId, Action<WorkItem> transition, CancellationToken token = default)
    {
        await using GoblinDbContext db = await _dbFactory.CreateDbContextAsync(token);
        await using IDbContextTransaction transaction = await BeginAsync(db, token);
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
            .Where(x => x.Status == "Queued" || x.Status == "Starting" || x.Status == "Running" ||
                x.Status == "CancellationRequested" || x.Status == "Uncertain" || (x.CleanupPending && !x.CleanupFailed) || x.WorkspaceRetained)
            .OrderBy(x => x.QueuedAt).ToArrayAsync(token);
    }

    public async Task SetConnectionAsync(long id, string availability, bool requireIdle, CancellationToken token = default, bool completeChange = false)
    {
        await using GoblinDbContext db = await _dbFactory.CreateDbContextAsync(token);
        await using IDbContextTransaction transaction = await BeginAsync(db, token);
        if (requireIdle && await db.ExecutionAttempts.AnyAsync(x => x.ConnectionId == id &&
            (x.Status == "Starting" || x.Status == "Running" || x.Status == "CancellationRequested" || x.Status == "Uncertain" || x.CleanupPending || x.WorkspaceRetained), token))
            throw new ApplicationFailure("connection_in_use");
        Persistence.Entities.Connection row = await db.Connections.SingleAsync(x => x.Id == id, token);
        if (row.Availability == "Changing")
        {
            if (requireIdle) throw new ApplicationFailure("connection_in_use");
            if (!completeChange) return;
        }
        if (row.Availability == "Verifying" && !requireIdle && !completeChange) return;
        row.Availability = availability;
        row.ChangedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    public async Task BeginVerificationAsync(long id, CancellationToken token = default)
    {
        await using GoblinDbContext db = await _dbFactory.CreateDbContextAsync(token);
        await using IDbContextTransaction transaction = await BeginAsync(db, token);
        Persistence.Entities.Connection row = await db.Connections.SingleAsync(x => x.Id == id, token);
        if (row.Availability == "Verifying") throw new ApplicationFailure("prompt_in_progress");
        if (row.Availability == "Changing" || await db.ExecutionAttempts.AnyAsync(x => x.ConnectionId == id &&
            (x.Status == "Starting" || x.Status == "Running" || x.Status == "CancellationRequested" || x.Status == "Uncertain" || x.CleanupPending || x.WorkspaceRetained), token))
            throw new ApplicationFailure("connection_in_use");
        row.Availability = "Verifying";
        row.ChangedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    public async Task EndVerificationAsync(long id, bool available)
    {
        await using GoblinDbContext db = await _dbFactory.CreateDbContextAsync();
        await using IDbContextTransaction transaction = await BeginAsync(db, default);
        Persistence.Entities.Connection row = await db.Connections.SingleAsync(x => x.Id == id);
        // A waiting account change owns the reservation until its operation ends.
        if (row.Availability != "Verifying") return;
        row.Availability = available ? "Available" : "Unavailable";
        row.ChangedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    // The self-hosted application has a single controller. Its verification
    // process cannot survive a controller restart; durable attempts can.
    public async Task RecoverConnectionReservationsAsync(CancellationToken token)
    {
        await using GoblinDbContext db = await _dbFactory.CreateDbContextAsync(token);
        await using IDbContextTransaction transaction = await BeginAsync(db, token);
        await db.Connections.Where(x => x.Availability == "Changing" || x.Availability == "Verifying")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Availability, "Unavailable").SetProperty(x => x.ChangedAt, DateTime.UtcNow), token);
        await transaction.CommitAsync(token);
    }

    internal static async Task<IDbContextTransaction> BeginAsync(GoblinDbContext db, CancellationToken token)
    {
        IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(token);
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(716352019)", token);
            return transaction;
        }
        catch { await transaction.DisposeAsync(); throw; }
    }

    private static async Task SaveAsync(GoblinDbContext db, Row row, WorkItem work, DateTimeOffset now, CancellationToken token)
    {
        row.State = JsonSerializer.Serialize(work.Snapshot(), Json);
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
                    GithubConnectionId = attempt.Target.Repository?.Grant?.ConnectionId,
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

    private static WorkItem Restore(Row row) => row.State is null
        ? new(row.Id, row.Objective, new DateTimeOffset(row.CreatedAt, TimeSpan.Zero))
        : WorkItem.Restore(JsonSerializer.Deserialize<WorkSnapshot>(row.State, Json)!);
    private static WorkView View(Row row) => new(row.Version, new(row.CreatedAt, TimeSpan.Zero),
        new(row.UpdatedAt, TimeSpan.Zero), Restore(row).Snapshot());
    private static string Hash(WorkCommand command) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command, Json))));
}
