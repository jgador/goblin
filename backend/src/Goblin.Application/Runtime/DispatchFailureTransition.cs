using System;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;

namespace Goblin.Application.Runtime;

// Converts durable failure evidence into aggregate state without coupling the
// safety policy to journal iteration or database scopes.
internal static class DispatchFailureTransition
{
    internal static void Apply(WorkItem work, DispatchFailureEvidence failure, DateTimeOffset now)
    {
        ExecutionAttempt? attempt = work.CurrentAttempt;
        if (attempt is null || attempt.Id != failure.AttemptId ||
            failure.TurnNumber != 0 && failure.TurnNumber != attempt.TurnNumber) return;

        if (attempt.CleanupPending && attempt.Status is AttemptStatus.Succeeded or AttemptStatus.Failed or
            AttemptStatus.Cancelled or AttemptStatus.Waiting)
            work.ReportCleanupFailure(attempt.Id, attempt.OwnerId!.Value, now);
        else if (attempt.Status == AttemptStatus.Queued)
            work.DispatchFailed(attempt.Id, failure.Failure, now);
        else if (attempt.Status is AttemptStatus.Starting or AttemptStatus.Running or
            AttemptStatus.CancellationRequested or AttemptStatus.Waiting)
            work.ExecutionUncertain(attempt.Id, attempt.OwnerId!.Value, failure.Failure, now);
    }
}
