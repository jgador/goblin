using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.GitRepositories;
using Goblin.Application.Work;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;
using Goblin.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Goblin.Application.Workspaces;

// A checkpoint records verified Git provenance only. Files stay on the Work PVC;
// this record is not a filesystem backup and never authorizes deleting the volume.
public sealed class WorkspaceCheckpoints : IWorkspaceCheckpoints
{
    private readonly IDbContextFactory<GoblinDbContext> _factory;

    public WorkspaceCheckpoints(IDbContextFactory<GoblinDbContext> factory) => _factory = factory;

    public async Task<WorkspaceCheckpoint?> LatestAsync(long workId, string gitRepository, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        WorkspaceCheckpoint? row = await db.WorkspaceCheckpoints.AsNoTracking().Where(x => x.WorkId == workId && x.GitRepository == gitRepository)
            .OrderByDescending(x => x.CreatedAt).Select(x => new WorkspaceCheckpoint()
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
            }).FirstOrDefaultAsync(token);
        return row;
    }

    public async Task<bool> VerifiedAsync(long id, long attemptId, int turnNumber, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        return await db.WorkspaceCheckpoints.AnyAsync(x => x.Id == id && x.AttemptId == attemptId && x.TurnNumber == turnNumber, token);
    }

    public async Task<WorkspaceCheckpoint> SaveAsync(long attemptId, int turn, string commit,
        GitRepositoryBroker broker, CancellationToken token)
    {
        WorkSnapshot work = await broker.CurrentAsync(attemptId, token);
        AttemptSnapshot attempt = work.Attempts[^1];
        if (attempt.TurnNumber != turn || attempt.Status is not (AttemptStatus.Starting or AttemptStatus.Running)) throw new ApplicationFailure("workspace_changed");
        await broker.VerifyCheckpointAsync(work, commit, token);
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        await using IDbContextTransaction transaction = await WorkStore.BeginAsync(db, token);
        Persistence.Entities.WorkspaceCheckpoint? previous = await db.WorkspaceCheckpoints.SingleOrDefaultAsync(x => x.AttemptId == attemptId && x.TurnNumber == turn, token);
        if (previous is not null)
        {
            if (previous.CommitSha != commit) throw new ApplicationFailure("workspace_changed");
            return View(previous);
        }
        Persistence.Entities.ExecutionAttempt owner = await db.ExecutionAttempts.SingleAsync(x => x.Id == attemptId, token);
        if (owner.TurnNumber != turn || owner.Status is not ("Starting" or "Running")) throw new ApplicationFailure("workspace_changed");
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
        await db.SaveChangesAsync(token); await transaction.CommitAsync(token);
        return View(row);
    }

    public async Task<WorkspaceCheckpoint[]> ListAsync(long workId, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        return await db.WorkspaceCheckpoints.AsNoTracking().Where(x => x.WorkId == workId).OrderByDescending(x => x.CreatedAt)
            .Select(x => new WorkspaceCheckpoint()
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
            }).ToArrayAsync(token);
    }

    private static WorkspaceCheckpoint View(Persistence.Entities.WorkspaceCheckpoint x) =>
        new()
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

}
