using System;
using System.Linq;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;

namespace Goblin.Application.Runtime;

internal static class ExecutionObservationTransition
{
    internal static void Apply(WorkItem work, AttemptSnapshot observedAttempt, ExecutionObservation observation,
        string[] gitRepositories, long decisionId, DateTimeOffset now)
    {
        ExecutionAttempt? attempt = work.CurrentAttempt;
        if (attempt is null || attempt.Id != observedAttempt.Id || attempt.OwnerId != observedAttempt.OwnerId ||
            observation.TurnNumber != attempt.TurnNumber ||
            attempt.Status is AttemptStatus.Succeeded or AttemptStatus.Failed or AttemptStatus.Cancelled) return;
        if (attempt.Status == AttemptStatus.Waiting && observation.Kind is ObservationKind.Paused or
            ObservationKind.InputRequired or ObservationKind.Pending or ObservationKind.WorkspaceRequired) return;
        if (attempt.StartedAt is null && observation.Session is not null)
            work.ExecutionStarted(attempt.Id, attempt.OwnerId!.Value, observation.Session, now);
        if (observation.CheckpointId is not null)
            work.SaveWorkspace(attempt.Id, attempt.OwnerId!.Value, observation.CheckpointId.Value, now);
        switch (observation.Kind)
        {
            case ObservationKind.Running:
                if (!string.IsNullOrWhiteSpace(observation.Text) && attempt.Status != AttemptStatus.Uncertain &&
                    work.History.LastOrDefault(x => x.AttemptId == attempt.Id && x.Kind == WorkEventKind.ProgressReported)?.Text != observation.Text)
                    work.ReportProgress(attempt.Id, attempt.OwnerId!.Value, observation.Text, now);
                break;
            case ObservationKind.Result:
                work.ProposeResult(attempt.Id, attempt.OwnerId!.Value, observation.Text!, now);
                if (observation.ArtifactReference is not null)
                    work.AddArtifact(attempt.Id, attempt.OwnerId.Value, observation.ArtifactReference, "Execution workspace", now);
                break;
            case ObservationKind.WorkspaceRequired:
                if (attempt.Status == AttemptStatus.CancellationRequested && attempt.Target.GitRepository is null)
                    work.ConfirmExecutionStopped(attempt.Id, attempt.OwnerId!.Value, now);
                else if (attempt.Target.GitRepository is null)
                    work.RequestGitRepositorySetupFromExecution(attempt.Id, attempt.OwnerId!.Value, gitRepositories, now);
                else work.RequireGitRepositoryExecution(attempt.Id, attempt.OwnerId!.Value, now);
                break;
            case ObservationKind.Paused:
                work.PauseForInput(attempt.Id, attempt.OwnerId!.Value, decisionId, observation.Text!, observation.ReleaseWorkspace, now);
                if (observation.ReleaseWorkspace) work.RequireCleanup(attempt.Id, attempt.OwnerId.Value, now);
                break;
            case ObservationKind.InputRequired:
                work.RequestInput(attempt.Id, attempt.OwnerId!.Value, decisionId, observation.Text!, now);
                if (observation.ArtifactReference is not null)
                    work.AddArtifact(attempt.Id, attempt.OwnerId.Value, observation.ArtifactReference, "Execution workspace", now);
                break;
            case ObservationKind.Failed:
                // The host only reports Failed after proving the process stopped.
                if (attempt.Status == AttemptStatus.Uncertain)
                    work.ConfirmExecutionStopped(attempt.Id, attempt.OwnerId!.Value, now);
                else work.ExecutionFailed(attempt.Id, attempt.OwnerId!.Value, observation.Failure ?? FailureKind.ExecutionFailed, now);
                break;
            case ObservationKind.Uncertain:
                if (attempt.Status != AttemptStatus.Uncertain)
                    work.ExecutionUncertain(attempt.Id, attempt.OwnerId!.Value, observation.Failure ?? FailureKind.RuntimeDisconnected, now);
                break;
            case ObservationKind.Stopped:
                if (attempt.Status is not (AttemptStatus.Uncertain or AttemptStatus.CancellationRequested))
                    work.ExecutionUncertain(attempt.Id, attempt.OwnerId!.Value, FailureKind.RuntimeDisconnected, now);
                work.ConfirmExecutionStopped(attempt.Id, attempt.OwnerId!.Value, now);
                break;
        }
        if (attempt.Status is AttemptStatus.Succeeded or AttemptStatus.Failed or AttemptStatus.Cancelled)
            work.RequireCleanup(attempt.Id, attempt.OwnerId!.Value, now);
    }
}
