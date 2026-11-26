using System;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.GitRepositories;
using Goblin.Application.Work;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;
using Goblin.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Wolverine.EntityFrameworkCore;
using CheckpointRow = Goblin.Persistence.Entities.WorkspaceCheckpoint;

namespace Goblin.Application.Workspaces;

// A checkpoint records verified Git provenance only. Files stay on the Work PVC;
// this record is not a filesystem backup and never authorizes deleting the volume.
public sealed class WorkspaceCheckpoints : IWorkspaceCheckpoints
{
    private static readonly Expression<Func<CheckpointRow, WorkspaceCheckpoint>> Projection = x => new()
    {
        Id = x.Id,
        WorkId = x.WorkId,
        AttemptId = x.AttemptId,
        TurnNumber = x.TurnNumber,
        WorkspaceNumber = x.WorkspaceNumber,
        GitRepository = x.GitRepository,
        Branch = x.Branch,
        CommitSha = x.CommitSha,
        CreatedAt = x.CreatedAt
    };
    private static readonly Func<CheckpointRow, WorkspaceCheckpoint> View = Projection.Compile();
    private readonly IDbContextFactory<GoblinDbContext> _factory;
    private readonly WorkPatchFiles _patches;
    private readonly WorkOutboxFactory _outboxes;

    public WorkspaceCheckpoints(IDbContextFactory<GoblinDbContext> factory, WorkPatchFiles patches, WorkOutboxFactory outboxes)
    {
        _factory = factory;
        _patches = patches;
        _outboxes = outboxes;
    }

    internal static bool RetainsPatches(string? status) => status is not (nameof(WorkStatus.Completed) or nameof(WorkStatus.Cancelled));

    internal async Task PinBaseAsync(WorkSnapshot work, string baseCommit, CancellationToken token)
    {
        AttemptSnapshot attempt = work.Attempts[^1];
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        await using IDbContextTransaction transaction = await ApplicationTransaction.BeginAsync(db, token);
        Persistence.Entities.WorkItem row = await db.WorkItems.SingleAsync(x => x.Id == work.Id, token);
        WorkItem current = WorkStatePersistence.Restore(row);
        if (current.Workspace?.BaseCommitSha is null)
        {
            current.PinWorkspaceBase(attempt.Id, attempt.OwnerId!.Value, baseCommit);
            WorkStatePersistence.SaveSnapshot(row, current);
            await db.SaveChangesAsync(token);
        }
        await transaction.CommitAsync(token);
    }

    public async Task<WorkspaceCheckpoint?> LatestAsync(long workId, string gitRepository, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        return await db.WorkspaceCheckpoints.AsNoTracking().Where(x => x.WorkId == workId && x.GitRepository == gitRepository)
            .OrderByDescending(x => x.CreatedAt).Select(Projection).FirstOrDefaultAsync(token);
    }

    public async Task<bool> VerifiedAsync(long id, long attemptId, int turnNumber, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        return await db.WorkspaceCheckpoints.AnyAsync(x => x.Id == id && x.AttemptId == attemptId && x.TurnNumber == turnNumber, token);
    }

    internal async Task<WorkspaceCheckpoint> SaveVerifiedAsync(WorkSnapshot work, int turn, string commit, byte[] patch, CancellationToken token)
    {
        AttemptSnapshot attempt = work.Attempts[^1];
        long attemptId = attempt.Id;
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        await using IDbContextTransaction transaction = await ApplicationTransaction.BeginAsync(db, token);
        Persistence.Entities.WorkItem current = await db.WorkItems.AsNoTracking().SingleAsync(x => x.Id == work.Id, token);
        if (!RetainsPatches(current.Status) || WorkStatePersistence.Snapshot(current).Workspace?.BaseCommitSha != work.Workspace?.BaseCommitSha)
            throw new ApplicationFailure("workspace_changed");
        Persistence.Entities.WorkspaceCheckpoint? previous = await db.WorkspaceCheckpoints.SingleOrDefaultAsync(x => x.AttemptId == attemptId && x.TurnNumber == turn, token);
        if (previous is not null)
        {
            if (previous.CommitSha != commit) throw new ApplicationFailure("workspace_changed");
            return View(previous);
        }
        Persistence.Entities.ExecutionAttempt owner = await db.ExecutionAttempts.SingleAsync(x => x.Id == attemptId, token);
        if (owner.TurnNumber != turn || !GitRepositoryAttemptPolicy.AllowsOperations(owner.Status)) throw new ApplicationFailure("workspace_changed");
        var row = new Persistence.Entities.WorkspaceCheckpoint
        {
            WorkId = work.Id,
            AttemptId = attemptId,
            TurnNumber = turn,
            WorkspaceNumber = attempt.WorkspaceNumber,
            GitRepository = attempt.Target.GitRepository!.GitRepository,
            Branch = attempt.Target.GitRepository.Grant!.Branch,
            CommitSha = commit,
            CreatedAt = DateTime.UtcNow
        };
        db.WorkspaceCheckpoints.Add(row);
        await db.SaveChangesAsync(token);
        await _patches.SaveAsync(work.Id, row.Id, patch, token);
        IDbContextOutbox outbox = _outboxes.Create(db);
        await outbox.PublishAsync(new ReconcileWorkPatches(work.Id));
        await outbox.SaveChangesAndFlushMessagesAsync(token);
        return View(row);
    }

    public async Task<WorkPatch?> ReadPatchAsync(long workId, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        Persistence.Entities.WorkItem? row = await db.WorkItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == workId, token) ?? throw new ApplicationFailure("work_not_found");
        if (!RetainsPatches(row.Status) || WorkStatePersistence.Snapshot(row).Workspace?.BaseCommitSha is not { } baseCommit) return null;
        WorkspaceCheckpoint? checkpoint = await db.WorkspaceCheckpoints.AsNoTracking().Where(x => x.WorkId == workId)
            .OrderByDescending(x => x.Id).Select(Projection).FirstOrDefaultAsync(token);
        if (checkpoint is null) return null;
        byte[]? patch = await _patches.ReadAsync(workId, checkpoint.Id, token);
        return patch is null ? null : new(checkpoint, baseCommit, patch);
    }

    internal async Task PrunePatchesAsync(long workId, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        await using IDbContextTransaction transaction = await ApplicationTransaction.BeginAsync(db, token);
        string? status = await db.WorkItems.Where(x => x.Id == workId).Select(x => x.Status).SingleOrDefaultAsync(token);
        long? keep = status is not null && RetainsPatches(status)
            ? await db.WorkspaceCheckpoints.Where(x => x.WorkId == workId).MaxAsync(x => (long?)x.Id, token)
            : null;
        _patches.Prune(workId, keep);
        await transaction.CommitAsync(token);
    }

    public async Task<WorkspaceCheckpoint[]> ListAsync(long workId, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        return await db.WorkspaceCheckpoints.AsNoTracking().Where(x => x.WorkId == workId).OrderByDescending(x => x.CreatedAt)
            .Select(Projection).ToArrayAsync(token);
    }
}

public sealed class WorkPatch
{
    public WorkPatch(WorkspaceCheckpoint checkpoint, string baseCommitSha, byte[] content)
    {
        Checkpoint = checkpoint;
        BaseCommitSha = baseCommitSha;
        Content = content;
    }

    public WorkspaceCheckpoint Checkpoint { get; }
    public string BaseCommitSha { get; }
    public byte[] Content { get; }
}
