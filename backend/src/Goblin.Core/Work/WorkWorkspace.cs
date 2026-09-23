using System;

namespace Goblin.Core.Work;

public sealed partial class WorkItem
{
    public void SaveWorkspace(long attemptId, long ownerId, long checkpointId, DateTimeOffset now)
    {
        ExecutionAttempt attempt = CompletableAttempt(attemptId, ownerId);
        Require(checkpointId > 0, WorkRule.InvalidValue);
        if (attempt.CheckpointId == checkpointId) return;
        attempt.CheckpointId = checkpointId;
        Record(WorkEventKind.WorkspaceSaved, now, attemptId, text: checkpointId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public void PauseForInput(long attemptId, long ownerId, long decisionId, string question,
        bool releaseWorkspace, DateTimeOffset now)
    {
        ExecutionAttempt attempt = OwnedActiveAttempt(attemptId, ownerId);
        RequireId(decisionId);
        // A release of repository compute requires a system-verified checkpoint.
        Require(!releaseWorkspace || attempt.Target.Repository is null || attempt.CheckpointId is not null,
            WorkRule.ReconciliationRequired);
        attempt.Status = AttemptStatus.Waiting;
        attempt.ReleaseWorkspace = releaseWorkspace;
        attempt.FinishedAt = now;
        _decisions.Add(new(decisionId, attemptId, RequireText(question), now));
        Status = WorkStatus.NeedsAttention;
        Attention = new(AttentionReason.InputRequired);
        Record(WorkEventKind.InputRequested, now, attemptId, decisionId: decisionId, text: question);
    }

    public void RequireRepositoryExecution(long attemptId, long ownerId, DateTimeOffset now)
    {
        ExecutionAttempt attempt = OwnedActiveAttempt(attemptId, ownerId);
        Require(attempt.ReasoningOnly && attempt.Target.Repository is not null, WorkRule.InvalidTransition);
        attempt.Status = AttemptStatus.Waiting;
        attempt.FinishedAt = now;
        ContinueExecution(now);
        attempt.ReasoningOnly = false;
    }

    private void ContinueExecution(DateTimeOffset now)
    {
        ExecutionAttempt attempt = CurrentAttempt!;
        Require(attempt.Status == AttemptStatus.Waiting && !attempt.CleanupPending, WorkRule.InvalidTransition);
        attempt.PriorTurns = [.. attempt.PriorTurns, new(attempt.TurnNumber, attempt.WorkspaceNumber,
            attempt.OwnerId, attempt.EnvironmentReference, attempt.Session, attempt.StartedAt, attempt.FinishedAt, attempt.CheckpointId)];
        attempt.TurnNumber++;
        if (attempt.ReleaseWorkspace && !attempt.ReasoningOnly) attempt.WorkspaceNumber++;
        attempt.ReasoningOnly = attempt.ReleaseWorkspace && attempt.Target.Repository is not null;
        attempt.Status = AttemptStatus.Queued;
        attempt.OwnerId = null;
        attempt.EnvironmentReference = null;
        attempt.Session = null;
        attempt.ClaimedAt = null;
        attempt.StartedAt = null;
        attempt.FinishedAt = null;
        Status = WorkStatus.Queued;
        Record(WorkEventKind.ExecutionContinued, now, attempt.Id);
    }
}
