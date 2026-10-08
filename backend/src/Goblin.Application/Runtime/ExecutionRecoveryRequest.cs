namespace Goblin.Application.Runtime;

internal enum ExecutionRecoveryAction
{
    Dispatch,
    Reconcile
}

// Recovery schedules delivery or observation; it does not grant execution ownership.
internal sealed class ExecutionRecoveryRequest
{
    internal ExecutionRecoveryRequest(long workId, long attemptId, int turnNumber, ExecutionRecoveryAction action)
    {
        WorkId = workId;
        AttemptId = attemptId;
        TurnNumber = turnNumber;
        Action = action;
    }

    internal long WorkId { get; init; }
    internal long AttemptId { get; init; }
    internal int TurnNumber { get; init; }
    internal ExecutionRecoveryAction Action { get; init; }
}
