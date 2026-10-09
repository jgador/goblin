using System.Linq;
using Goblin.Contracts.Conversations;
using Goblin.Core.Work;

namespace Goblin.Application.Work;

// Projects committed Work into conversational updates. It never changes Work or
// treats runtime success as approval. Superseded outcomes are not announced as current.
internal static class ExternalWorkUpdatePolicy
{
    internal static ExternalWorkUpdateContent? Pending(WorkSnapshot work, long? notifiedSequence)
    {
        (ExternalWorkUpdateKind Kind, WorkEventKind Event)? outcome = (work.Status, work.Attention?.Reason) switch
        {
            (WorkStatus.Completed, _) => (ExternalWorkUpdateKind.Completed, WorkEventKind.ResultApproved),
            (WorkStatus.Cancelled, _) => (ExternalWorkUpdateKind.Cancelled, WorkEventKind.Cancelled),
            (WorkStatus.NeedsAttention, AttentionReason.ResultReview) => (ExternalWorkUpdateKind.ResultReady, WorkEventKind.ResultProposed),
            (WorkStatus.NeedsAttention, AttentionReason.Failure) => (ExternalWorkUpdateKind.Failed, WorkEventKind.ExecutionFailed),
            (WorkStatus.NeedsAttention, AttentionReason.UncertainExecution) => (ExternalWorkUpdateKind.Uncertain, WorkEventKind.ExecutionUncertain),
            (WorkStatus.NeedsAttention, AttentionReason.CleanupRequired) => (ExternalWorkUpdateKind.CleanupFailed, WorkEventKind.CleanupFailed),
            _ => null
        };
        if (outcome is not { } selected) return null;
        if (selected.Kind == ExternalWorkUpdateKind.ResultReady && work.Attempts.LastOrDefault()?.CleanupPending == true) return null;
        long? attemptId = work.Attempts.LastOrDefault()?.Id;
        WorkEvent? change = work.History.LastOrDefault(item => item.AttemptId == attemptId &&
            (item.Kind == selected.Event || selected.Kind == ExternalWorkUpdateKind.Failed && item.Kind == WorkEventKind.ExecutionStopped));
        // Cleanup can temporarily replace result/failure attention. Its completion
        // makes the saved outcome actionable again, even after a cleanup-error update.
        WorkEvent? restored = work.History.LastOrDefault(item => item.AttemptId == attemptId && item.Kind == WorkEventKind.CleanupCompleted);
        long sequence = change?.Sequence ?? 0;
        if (restored is not null && restored.Sequence > sequence &&
            work.History.Any(item => item.AttemptId == attemptId && item.Kind == WorkEventKind.CleanupFailed && item.Sequence > sequence))
            sequence = restored.Sequence;
        if (change is null || sequence <= (notifiedSequence ?? 0)) return null;

        string text = selected.Kind switch
        {
            ExternalWorkUpdateKind.ResultReady or ExternalWorkUpdateKind.Completed =>
                work.Results.Last(item => item.AttemptId == attemptId).Text,
            ExternalWorkUpdateKind.Failed => FailureText(work.Attention?.Failure),
            ExternalWorkUpdateKind.Uncertain => "Goblin cannot confirm whether execution has stopped. Reconcile the existing execution in Goblin before retrying.",
            ExternalWorkUpdateKind.CleanupFailed => "Goblin could not finish execution cleanup. Any recorded outcome is saved. Open Work to reconcile cleanup before continuing.",
            ExternalWorkUpdateKind.Cancelled => "Work has been cancelled.",
            _ => throw new System.InvalidOperationException("Unknown Work update kind.")
        };
        return new(sequence, selected.Kind, text);
    }

    private static string FailureText(FailureKind? failure) => failure switch
    {
        FailureKind.DispatchFailed => "Goblin could not start execution.",
        FailureKind.ConnectionUnavailable => "The agent connection is unavailable.",
        FailureKind.CapabilityUnavailable => "The selected agent does not support this execution.",
        FailureKind.HostUnavailable => "The execution environment is unavailable.",
        FailureKind.ExecutionFailed => "The agent execution failed.",
        FailureKind.TimedOut => "The execution timed out.",
        FailureKind.RuntimeDisconnected => "The agent runtime disconnected.",
        FailureKind.CancellationFailed => "Goblin could not confirm cancellation.",
        FailureKind.CleanupFailed => "Goblin could not finish execution cleanup.",
        FailureKind.StorageUnavailable => "Work storage is unavailable.",
        _ => "Execution failed and needs your attention."
    };
}
