using System;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Core.Work;

namespace Goblin.Contracts.Runtime;

public sealed record WorkspaceLimits(int MaxSandboxes = 2, long MaxArchiveBytes = 134217728,
    long MaxStorageBytes = 2147483648, int MaxCachedVolumes = 4);
public sealed record WorkspaceCheckpoint(Guid Id, long WorkId, long AttemptId, int TurnNumber,
    int WorkspaceNumber, string Repository, string Branch, string CommitSha, DateTimeOffset CreatedAt);
public interface IWorkspaceArchive
{
    Task<WorkspaceCheckpoint?> LatestAsync(long workId, string repository, CancellationToken token);
    Task<bool> VerifiedAsync(Guid id, long attemptId, int turnNumber, CancellationToken token);
    Task<bool> CanDiscardAsync(long attemptId, int workspaceNumber, CancellationToken token);
}
