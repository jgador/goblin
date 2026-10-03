using System;
using Goblin.Core.Repositories;

namespace Goblin.Contracts.Runtime;

public sealed class RepositorySetupMemory
{
    public RepositorySetupMemory(long id, long workId, long attemptId, int turnNumber, string branch,
        string commit, string environment, DateTimeOffset verifiedAt, VerifiedRepositorySetup observation)
    {
        Id = id;
        WorkId = workId;
        AttemptId = attemptId;
        TurnNumber = turnNumber;
        Branch = branch;
        Commit = commit;
        Environment = environment;
        VerifiedAt = verifiedAt;
        Observation = observation;
    }

    public long Id { get; init; }

    public long WorkId { get; init; }

    public long AttemptId { get; init; }

    public int TurnNumber { get; init; }

    public string Branch { get; init; }

    public string Commit { get; init; }

    public string Environment { get; init; }

    public DateTimeOffset VerifiedAt { get; init; }

    public VerifiedRepositorySetup Observation { get; init; }
}

public sealed class SetupMemoryWrite
{
    public SetupMemoryWrite(int turnNumber, long checkpointId, string environment,
        VerifiedRepositorySetup[] observations)
    {
        TurnNumber = turnNumber;
        CheckpointId = checkpointId;
        Environment = environment;
        Observations = observations;
    }

    public int TurnNumber { get; init; }

    public long CheckpointId { get; init; }

    public string Environment { get; init; }

    public VerifiedRepositorySetup[] Observations { get; init; }
}
