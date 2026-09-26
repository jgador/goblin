using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.Work;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;
using Goblin.Persistence;
using Goblin.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Wolverine.EntityFrameworkCore;

namespace Goblin.Application.Workspaces;

public sealed record StartInspection(long Id);
public sealed record StopInspection(long Id);
public sealed record InspectionView(long Id, long WorkId, long AttemptId, string State);
public sealed class InspectionStore
{
    private readonly IDbContextFactory<GoblinDbContext> _factory;
    private readonly WorkOutboxFactory _outboxes;
    private readonly WorkspaceLimits _limits;
    public InspectionStore(IDbContextFactory<GoblinDbContext> factory, WorkOutboxFactory outboxes, WorkspaceLimits limits)
    { _factory = factory; _outboxes = outboxes; _limits = limits; }
    public async Task<InspectionView[]> ListAsync(long workId, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        return await db.WorkspaceSessions.AsNoTracking().Where(x => x.WorkId == workId).OrderByDescending(x => x.CreatedAt)
            .Select(x => new InspectionView(x.Id, x.WorkId, x.AttemptId, x.State)).ToArrayAsync(token);
    }
    public async Task<InspectionView> OpenAsync(long workId, long id, long attemptId, CancellationToken token)
    {
        if (id <= 0) throw new ApplicationFailure("invalid_command");
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        await using IDbContextTransaction tx = await WorkStore.BeginAsync(db, token);
        WorkspaceSession? row = await db.WorkspaceSessions.SingleOrDefaultAsync(x => x.Id == id, token);
        if (row is not null)
        {
            if (row.WorkId != workId || row.AttemptId != attemptId) throw new ApplicationFailure("command_id_reused");
            return View(row);
        }
        if (await db.WorkspaceSessions.AnyAsync(x => x.WorkId == workId && (x.State == "Queued" || x.State == "Starting" || x.State == "Available" || x.State == "Stopping" || x.State == "NeedsAttention"), token))
            throw new ApplicationFailure("workspace_session_exists");
        Persistence.Entities.WorkItem workRow = await db.WorkItems.SingleOrDefaultAsync(x => x.Id == workId, token) ?? throw new ApplicationFailure("work_not_found");
        WorkSnapshot work = WorkStore.Restore(workRow).Snapshot();
        AttemptSnapshot? attempt = work.Attempts.SingleOrDefault(x => x.Id == attemptId && x.Target.Repository is not null) ?? throw new ApplicationFailure("workspace_not_found");
        WorkWorkspace workspace = work.Workspace ?? throw new ApplicationFailure("workspace_not_found");
        WorkspaceSessionRules.RequireOpenable(attempt.Status);
        if (workspace.AttemptId != attemptId) throw new ApplicationFailure("workspace_not_found");
        WorkspaceSessionRules.RequireOpenable(work.Attempts[^1].Status);
        if (work.Attempts[^1].CleanupPending ||
            (work.Attempts[^1].Status is AttemptStatus.Waiting or AttemptStatus.Queued && !work.Attempts[^1].ReleaseWorkspace))
            throw new ApplicationFailure("workspace_unavailable");
        row = new()
        {
            Id = id,
            WorkId = workId,
            AttemptId = attemptId,
            SourceVolume = workspace.EnvironmentReference,
            State = "Queued",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.WorkspaceSessions.Add(row);
        IDbContextOutbox outbox = _outboxes.Create(db);
        await outbox.PublishAsync(new StartInspection(id));
        await outbox.SaveChangesAndFlushMessagesAsync(token);
        return View(row);
    }
    public async Task StopAsync(long workId, long id, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        await using IDbContextTransaction tx = await WorkStore.BeginAsync(db, token);
        WorkspaceSession row = await db.WorkspaceSessions.SingleOrDefaultAsync(x => x.Id == id && x.WorkId == workId, token) ?? throw new ApplicationFailure("workspace_not_found");
        if (row.State == "Stopped") return;
        row.State = "Stopping"; row.UpdatedAt = DateTime.UtcNow;
        IDbContextOutbox outbox = _outboxes.Create(db);
        await outbox.PublishAsync(new StopInspection(id));
        await outbox.SaveChangesAndFlushMessagesAsync(token);
    }
    public async Task<InspectionAllocation?> ClaimAsync(long id, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        await using IDbContextTransaction tx = await WorkStore.BeginAsync(db, token);
        WorkspaceSession row = await db.WorkspaceSessions.SingleAsync(x => x.Id == id, token);
        if (row.State != "Queued") return null;
        if (await db.ExecutionAttempts.AnyAsync(x => x.WorkId == row.WorkId && x.GithubConnectionId != null &&
            (x.Status == "Starting" || x.Status == "Running" || x.Status == "CancellationRequested" || x.Status == "Uncertain" || x.CleanupPending || x.WorkspaceRetained), token)) return null;
        int occupied = await db.ExecutionAttempts.CountAsync(x => x.GithubConnectionId != null &&
            (x.Status == "Starting" || x.Status == "Running" || x.Status == "CancellationRequested" || x.Status == "Uncertain" || x.CleanupPending || x.WorkspaceRetained), token);
        occupied += await db.WorkspaceSessions.CountAsync(x => x.State == "Starting" || x.State == "Available" || x.State == "Stopping" || x.State == "NeedsAttention", token);
        if (occupied >= _limits.MaxSandboxes) return null;
        row.State = "Starting"; row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(token); await tx.CommitAsync(token);
        return Allocation(row);
    }
    public async Task<InspectionAllocation> GetAsync(long id, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        return Allocation(await db.WorkspaceSessions.AsNoTracking().SingleAsync(x => x.Id == id, token));
    }
    public async Task<InspectionView> RequireAvailableAsync(long workId, long id, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        WorkspaceSession row = await db.WorkspaceSessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.WorkId == workId && x.State == "Available", token)
            ?? throw new ApplicationFailure("workspace_unavailable");
        return View(row);
    }
    public async Task ObserveAsync(long id, string observed, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        await using IDbContextTransaction tx = await WorkStore.BeginAsync(db, token);
        WorkspaceSession row = await db.WorkspaceSessions.SingleAsync(x => x.Id == id, token);
        row.State = WorkspaceSessionRules.Observe(row.State, observed); row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(token); await tx.CommitAsync(token);
    }
    public async Task<InspectionView[]> PendingAsync(CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        return await db.WorkspaceSessions.AsNoTracking().Where(x => x.State == "Queued" || x.State == "Starting" || x.State == "Available" || x.State == "Stopping")
            .Select(x => new InspectionView(x.Id, x.WorkId, x.AttemptId, x.State)).ToArrayAsync(token);
    }
    private static InspectionView View(Persistence.Entities.WorkspaceSession x) => new(x.Id, x.WorkId, x.AttemptId, x.State);
    private static InspectionAllocation Allocation(Persistence.Entities.WorkspaceSession x) => new(x.Id, x.WorkId, x.AttemptId, x.SourceVolume);
}
