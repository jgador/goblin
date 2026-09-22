using System;

namespace Goblin.Core.Work;

public sealed partial class WorkItem
{
    // Commit cleanup intent with the outcome, before removing runtime evidence
    // or credentials. A failed cleanup blocks replacement execution and approval.
    public void RequireCleanup(long attemptId, long ownerId, DateTimeOffset now)
    {
        ExecutionAttempt attempt = OwnedAttempt(attemptId, ownerId);
        Require(attempt.Status is AttemptStatus.Succeeded or AttemptStatus.Failed or AttemptStatus.Cancelled or AttemptStatus.Waiting,
            WorkRule.InvalidTransition);
        if (attempt.CleanupPending) return;
        attempt.CleanupPending = true;
        Record(WorkEventKind.CleanupRequired, now, attemptId);
    }

    public void ReportCleanupFailure(long attemptId, long ownerId, DateTimeOffset now)
    {
        ExecutionAttempt attempt = OwnedAttempt(attemptId, ownerId);
        Require(attempt.CleanupPending, WorkRule.InvalidTransition);
        if (attempt.CleanupFailed) return;
        attempt.CleanupFailed = true;
        Status = WorkStatus.NeedsAttention;
        Attention = new(AttentionReason.CleanupRequired, FailureKind.CleanupFailed);
        Record(WorkEventKind.CleanupFailed, now, attemptId, failure: FailureKind.CleanupFailed);
    }

    public void ConfirmCleanup(long attemptId, long ownerId, DateTimeOffset now)
    {
        ExecutionAttempt attempt = OwnedAttempt(attemptId, ownerId);
        if (!attempt.CleanupPending) return;
        attempt.CleanupPending = false;
        if (attempt.CleanupFailed)
        {
            attempt.CleanupFailed = false;
            if (attempt.Status == AttemptStatus.Cancelled)
            {
                Status = WorkStatus.Cancelled;
                Attention = null;
            }
            else
            {
                Status = WorkStatus.NeedsAttention;
                Attention = attempt.Status == AttemptStatus.Failed
                    ? new(AttentionReason.Failure, attempt.Failure)
                    : new(_decisions.Exists(x => x.AttemptId == attemptId && x.Answer is null)
                        ? AttentionReason.InputRequired : AttentionReason.ResultReview);
            }
        }
        Record(WorkEventKind.CleanupCompleted, now, attemptId);
        if (attempt.CheckpointId is not null) Record(WorkEventKind.WorkspaceReleased, now, attemptId);
    }
}
