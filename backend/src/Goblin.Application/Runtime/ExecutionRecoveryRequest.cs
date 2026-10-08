namespace Goblin.Application.Runtime;

internal enum ExecutionRecoveryAction
{
    Dispatch,
    Reconcile
}

// Recovery schedules delivery or observation; it does not grant execution ownership.
internal sealed record ExecutionRecoveryRequest(long WorkId, long AttemptId, int TurnNumber,
    ExecutionRecoveryAction Action);
