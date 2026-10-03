using System;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Core.Work;

namespace Goblin.Contracts.Runtime;

public sealed class WorkspaceLimits
{
    public WorkspaceLimits(int maxSandboxes = 2, int maxCachedVolumes = 4)
    {
        MaxSandboxes = maxSandboxes;
        MaxCachedVolumes = maxCachedVolumes;
    }

    public int MaxSandboxes { get; init; }

    public int MaxCachedVolumes { get; init; }
}

public sealed class WorkspaceCheckpoint
{
    public WorkspaceCheckpoint()
    {
    }

    public long Id { get; init; }

    public long WorkId { get; init; }

    public long AttemptId { get; init; }

    public int TurnNumber { get; init; }

    public int WorkspaceNumber { get; init; }

    public string GitRepository { get; init; } = null!;

    public string Branch { get; init; } = null!;

    public string CommitSha { get; init; } = null!;

    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class WorkspaceCheckpointWrite
{
    public WorkspaceCheckpointWrite(int turnNumber, string commitSha)
    {
        TurnNumber = turnNumber;
        CommitSha = commitSha;
    }

    [JsonPropertyName("turnNumber")]
    public int TurnNumber { get; init; }

    [JsonPropertyName("commitSha")]
    public string CommitSha { get; init; }
}

public interface IWorkspaceCheckpoints
{
    Task<WorkspaceCheckpoint?> LatestAsync(long workId, string gitRepository, CancellationToken token);

    Task<bool> VerifiedAsync(long id, long attemptId, int turnNumber, CancellationToken token);
}
