using System;
using Goblin.Core.GitRepositories;

namespace Goblin.Contracts.Runtime;

public sealed class GitRepositorySetupMemory
{
    public GitRepositorySetupMemory()
    {
    }

    public long Id { get; init; }

    public long WorkId { get; init; }

    public long AttemptId { get; init; }

    public int TurnNumber { get; init; }

    public string Branch { get; init; } = null!;

    public string Commit { get; init; } = null!;

    public string Environment { get; init; } = null!;

    public DateTimeOffset VerifiedAt { get; init; }

    public VerifiedGitRepositorySetup Observation { get; init; } = null!;
}

public sealed class SetupMemoryWrite
{
    public SetupMemoryWrite(int turnNumber, long checkpointId, string environment,
        VerifiedGitRepositorySetup[] observations)
    {
        TurnNumber = turnNumber;
        CheckpointId = checkpointId;
        Environment = environment;
        Observations = observations;
    }

    public int TurnNumber { get; init; }

    public long CheckpointId { get; init; }

    public string Environment { get; init; }

    public VerifiedGitRepositorySetup[] Observations { get; init; }
}
