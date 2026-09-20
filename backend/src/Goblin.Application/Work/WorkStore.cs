using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Core.Work;
using Goblin.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Wolverine.EntityFrameworkCore;
using AttemptRow = Goblin.Persistence.Entities.ExecutionAttempt;
using Receipt = Goblin.Persistence.Entities.WorkCommand;
using Row = Goblin.Persistence.Entities.WorkItem;

namespace Goblin.Application.Work;

// Each instance is one unit of work. The database lock serializes the short
// product transactions, including connection reservations. No runtime/network
// operation may run under it. PostgreSQL also enforces active connection capacity.
public sealed class WorkStore
{
    private readonly GoblinDbContext _db;
    private readonly IDbContextOutbox<GoblinDbContext> _outbox;

    public WorkStore(GoblinDbContext db, IDbContextOutbox<GoblinDbContext> outbox)
    {
        _db = db;
        _outbox = outbox;
    }

    public static readonly Guid DefaultAgentId = new("00000000-0000-0000-0000-000000000001");
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<WorkView[]> ListAsync(CancellationToken token = default)
    {
        Row[] rows = await _db.WorkItems.AsNoTracking().OrderByDescending(x => x.UpdatedAt)
            .ThenBy(x => x.Id).ToArrayAsync(token);
        return [.. rows.Select(View)];
    }

    public async Task<WorkView> GetAsync(Guid id, CancellationToken token = default) =>
        View(await _db.WorkItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token)
            ?? throw new ApplicationFailure("work_not_found"));

    public Task<AgentView[]> AgentsAsync(CancellationToken token = default) => _db.Agents.AsNoTracking()
        .OrderBy(x => x.Name).Select(x => new AgentView(x.Id, x.Name, x.ConnectionId, x.Model)).ToArrayAsync(token);

    public Task<ConnectionView[]> ConnectionsAsync(CancellationToken token = default) => _db.Connections.AsNoTracking()
        .OrderBy(x => x.Name).Select(x => new ConnectionView(x.Id, x.Runtime, x.Name, x.Availability)).ToArrayAsync(token);

    public async Task<WorkView> ApplyAsync(WorkCommand command, CancellationToken token = default)
    {
        if (command.CommandId == Guid.Empty || command.WorkId == Guid.Empty || !Enum.IsDefined(command.Action))
            throw new ApplicationFailure("invalid_command");
        if (command.Text?.Length > 4000) throw new ApplicationFailure("text_too_long");
        await using IDbContextTransaction transaction = await BeginAsync(token);
        string fingerprint = Hash(command);
        Receipt? receipt = await _db.WorkCommands.SingleOrDefaultAsync(x => x.Id == command.CommandId, token);
        if (receipt is not null)
        {
            if (receipt.Fingerprint != fingerprint) throw new ApplicationFailure("command_id_reused");
            return JsonSerializer.Deserialize<WorkView>(receipt.Response, Json)!;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Row? row = await _db.WorkItems.SingleOrDefaultAsync(x => x.Id == command.WorkId, token);
        WorkItem work;
        if (command.Action == WorkAction.Create)
        {
            if (row is not null) throw new ApplicationFailure("work_already_exists");
            work = new(command.WorkId, command.Text ?? "", now);
            row = new() { Id = work.Id, Objective = work.Objective, CreatedAt = now.UtcDateTime, Version = 0 };
            _db.WorkItems.Add(row);
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
                Guid agentId = command.AgentId ?? throw new ApplicationFailure("agent_required");
                if (!await _db.Agents.AnyAsync(x => x.Id == agentId, token)) throw new ApplicationFailure("agent_not_found");
                work.Assign(agentId, now);
                break;
            case WorkAction.Execute:
            case WorkAction.Retry:
                Persistence.Entities.Agent agent = await _db.Agents.SingleOrDefaultAsync(x => x.Id == work.AgentId, token)
                    ?? throw new ApplicationFailure("agent_required");
                Persistence.Entities.Connection connection = await _db.Connections.SingleAsync(x => x.Id == agent.ConnectionId, token);
                var target = new ExecutionTarget(connection.Runtime, connection.Id, agent.Model,
                    command.Repository ?? work.CurrentAttempt?.Target.Repository);
                Guid attemptId = Guid.NewGuid();
                if (command.Action == WorkAction.Retry) work.RetryExecution(attemptId, target, now);
                else work.QueueExecution(attemptId, target, now);
                await _outbox.PublishAsync(new DispatchWork(work.Id, attemptId));
                break;
            case WorkAction.Cancel:
                work.RequestCancellation(now);
                if (work.CurrentAttempt is { Status: AttemptStatus.CancellationRequested } cancelled)
                    await _outbox.PublishAsync(new ReconcileWork(work.Id, cancelled.Id));
                break;
            case WorkAction.Answer:
                work.AnswerDecision(command.DecisionId ?? Guid.Empty, command.Text ?? "", now);
                break;
            case WorkAction.RequestChanges:
                work.RequestChanges(command.AttemptId ?? Guid.Empty, command.Text ?? "", now);
                break;
            case WorkAction.Approve:
                work.ApproveResult(command.AttemptId ?? Guid.Empty, now);
                break;
            case WorkAction.AddContext:
                work.AddContext(command.CommandId, command.Text ?? "", now);
                break;
            case WorkAction.Reconcile:
                ExecutionAttempt? uncertain = work.CurrentAttempt;
                if (uncertain is null || (!uncertain.CleanupPending && uncertain.Status is not (AttemptStatus.Uncertain or AttemptStatus.CancellationRequested)))
                    throw new ApplicationFailure("reconciliation_not_required");
                await _outbox.PublishAsync(new ReconcileWork(work.Id, uncertain.Id));
                break;
        }
        await SaveAsync(row, work, now, token);
        WorkView view = View(row);
        _db.WorkCommands.Add(new()
        {
            Id = command.CommandId,
            WorkId = work.Id,
            Fingerprint = fingerprint,
            Response = JsonSerializer.Serialize(view, Json),
            CreatedAt = now.UtcDateTime
        });
        await _outbox.SaveChangesAndFlushMessagesAsync(token);
        return view;
    }

    // A successful claim is committed before the caller can contact the host.
    // Returning null means this delivery has no permission to start execution.
    public async Task<WorkSnapshot?> ClaimAsync(DispatchWork command, Guid ownerId, string environment,
        Func<ExecutionTarget, bool> supportsRuntime, CancellationToken token = default)
    {
        await using IDbContextTransaction transaction = await BeginAsync(token);
        Row? row = await _db.WorkItems.SingleOrDefaultAsync(x => x.Id == command.WorkId, token);
        if (row is null) return null;
        WorkItem work = Restore(row);
        if (work.CurrentAttempt is not { Status: AttemptStatus.Queued } attempt || attempt.Id != command.AttemptId) return null;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Persistence.Entities.Connection connection = await _db.Connections.SingleAsync(x => x.Id == attempt.Target.ConnectionId, token);
        if (connection.Availability is "Changing" or "Verifying") return null;
        FailureKind? failure = !supportsRuntime(attempt.Target) ? FailureKind.CapabilityUnavailable :
            connection.Availability != "Available" ? FailureKind.ConnectionUnavailable : null;
        if (failure is null && await _db.ExecutionAttempts.AnyAsync(x => x.ConnectionId == connection.Id &&
            (x.Status == "Starting" || x.Status == "Running" || x.Status == "CancellationRequested" || x.Status == "Uncertain" || x.CleanupPending), token))
        {
            // Capacity waiting is not a failed runtime operation. A periodic
            // queue scan will deliver this same unclaimed attempt when free.
            return null;
        }
        if (failure is not null) work.DispatchFailed(attempt.Id, failure.Value, now);
        else if (!work.TryClaimExecution(attempt.Id, ownerId, environment, now)) return null;
        await SaveAsync(row, work, now, token);
        await _outbox.SaveChangesAndFlushMessagesAsync(token);
        return failure is null ? work.Snapshot() : null;
    }

    public async Task MutateAsync(Guid workId, Action<WorkItem> transition, CancellationToken token = default)
    {
        await using IDbContextTransaction transaction = await BeginAsync(token);
        Row row = await _db.WorkItems.SingleAsync(x => x.Id == workId, token);
        WorkItem work = Restore(row);
        long before = work.History.Count;
        transition(work);
        if (before == work.History.Count) return;
        await SaveAsync(row, work, DateTimeOffset.UtcNow, token);
        await _outbox.SaveChangesAndFlushMessagesAsync(token);
    }

    public Task<AttemptRow[]> RecoverableAsync(CancellationToken token = default) => _db.ExecutionAttempts.AsNoTracking()
        .Where(x => x.Status == "Queued" || x.Status == "Starting" || x.Status == "Running" ||
            x.Status == "CancellationRequested" || x.Status == "Uncertain" || (x.CleanupPending && !x.CleanupFailed))
        .OrderBy(x => x.QueuedAt).ToArrayAsync(token);

    public async Task SetConnectionAsync(Guid id, string availability, bool requireIdle, CancellationToken token = default, bool completeChange = false)
    {
        await using IDbContextTransaction transaction = await BeginAsync(token);
        if (requireIdle && await _db.ExecutionAttempts.AnyAsync(x => x.ConnectionId == id &&
            (x.Status == "Starting" || x.Status == "Running" || x.Status == "CancellationRequested" || x.Status == "Uncertain" || x.CleanupPending), token))
            throw new ApplicationFailure("connection_in_use");
        Persistence.Entities.Connection row = await _db.Connections.SingleAsync(x => x.Id == id, token);
        if (row.Availability == "Changing")
        {
            if (requireIdle) throw new ApplicationFailure("connection_in_use");
            if (!completeChange) return;
        }
        if (row.Availability == "Verifying" && !requireIdle && !completeChange) return;
        row.Availability = availability;
        row.ChangedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    public async Task BeginVerificationAsync(Guid id, CancellationToken token = default)
    {
        await using IDbContextTransaction transaction = await BeginAsync(token);
        Persistence.Entities.Connection row = await _db.Connections.SingleAsync(x => x.Id == id, token);
        if (row.Availability == "Verifying") throw new ApplicationFailure("prompt_in_progress");
        if (row.Availability == "Changing" || await _db.ExecutionAttempts.AnyAsync(x => x.ConnectionId == id &&
            (x.Status == "Starting" || x.Status == "Running" || x.Status == "CancellationRequested" || x.Status == "Uncertain" || x.CleanupPending), token))
            throw new ApplicationFailure("connection_in_use");
        row.Availability = "Verifying";
        row.ChangedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    public async Task EndVerificationAsync(Guid id, bool available)
    {
        await using IDbContextTransaction transaction = await BeginAsync(default);
        Persistence.Entities.Connection row = await _db.Connections.SingleAsync(x => x.Id == id);
        // A waiting account change owns the reservation until its operation ends.
        if (row.Availability != "Verifying") return;
        row.Availability = available ? "Available" : "Unavailable";
        row.ChangedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    // The self-hosted application has a single controller. Its verification
    // process cannot survive a controller restart; durable attempts can.
    public async Task RecoverConnectionReservationsAsync(CancellationToken token)
    {
        await using IDbContextTransaction transaction = await BeginAsync(token);
        await _db.Connections.Where(x => x.Availability == "Changing" || x.Availability == "Verifying")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Availability, "Unavailable").SetProperty(x => x.ChangedAt, DateTime.UtcNow), token);
        await transaction.CommitAsync(token);
    }

    private async Task<IDbContextTransaction> BeginAsync(CancellationToken token)
    {
        _db.ChangeTracker.Clear();
        IDbContextTransaction transaction = await _db.Database.BeginTransactionAsync(token);
        try
        {
            await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(716352019)", token);
            return transaction;
        }
        catch { await transaction.DisposeAsync(); throw; }
    }

    private async Task SaveAsync(Row row, WorkItem work, DateTimeOffset now, CancellationToken token)
    {
        row.State = JsonSerializer.Serialize(work.Snapshot(), Json);
        row.Status = work.Status.ToString();
        row.AgentId = work.AgentId;
        row.Version++;
        row.UpdatedAt = now.UtcDateTime;
        foreach (ExecutionAttempt attempt in work.Attempts)
        {
            AttemptRow? saved = await _db.ExecutionAttempts.SingleOrDefaultAsync(x => x.Id == attempt.Id, token);
            if (saved is null)
            {
                saved = new()
                {
                    Id = attempt.Id,
                    WorkId = work.Id,
                    AgentId = attempt.AgentId,
                    ConnectionId = attempt.Target.ConnectionId,
                    Runtime = attempt.Target.Runtime,
                    QueuedAt = attempt.QueuedAt.UtcDateTime
                };
                _db.ExecutionAttempts.Add(saved);
            }
            saved.Status = attempt.Status.ToString();
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
