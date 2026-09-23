using System;
using Goblin.Core.Repositories;

namespace Goblin.Contracts.Runtime;

public sealed record RepositorySetupMemory(long Id, long WorkId, long AttemptId, int TurnNumber,
    string Branch, string Commit, string Environment, DateTimeOffset VerifiedAt, VerifiedRepositorySetup Observation);
public sealed record SetupMemoryWrite(int TurnNumber, long CheckpointId, string Environment,
    VerifiedRepositorySetup[] Observations);
