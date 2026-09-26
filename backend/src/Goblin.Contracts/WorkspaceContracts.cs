using System;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Core.Work;

namespace Goblin.Contracts.Runtime;

public sealed record WorkspaceLimits(int MaxSandboxes = 2, int MaxCachedVolumes = 4);
public sealed record WorkspaceCheckpoint(long Id, long WorkId, long AttemptId, int TurnNumber,
    int WorkspaceNumber, string Repository, string Branch, string CommitSha, DateTimeOffset CreatedAt);
public sealed record WorkspaceCheckpointWrite(int TurnNumber, string CommitSha);
public interface IWorkspaceCheckpoints
{
    Task<WorkspaceCheckpoint?> LatestAsync(long workId, string repository, CancellationToken token);
    Task<bool> VerifiedAsync(long id, long attemptId, int turnNumber, CancellationToken token);
}
