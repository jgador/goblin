using System;
using Goblin.Application.Runtime;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;
using Xunit;

namespace Goblin.Application.Tests;

public sealed class ExecutionObservationTransitionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ResultCapturesRuntimeEvidenceAndRequiresCleanup()
    {
        WorkItem work = Claimed();
        AttemptSnapshot observed = work.Snapshot().Attempts[^1];
        var observation = new ExecutionObservation(ObservationKind.Result,
            new("actual-model", "session", "operation"), "Completed", ArtifactReference: "artifact://workspace")
        { CheckpointId = 6 };

        Apply(work, observed, observation);

        AttemptSnapshot attempt = work.Snapshot().Attempts[^1];
        Assert.Equal(AttemptStatus.Succeeded, attempt.Status);
        Assert.Equal(6, attempt.CheckpointId);
        Assert.True(attempt.CleanupPending);
        Assert.Equal("session", attempt.Session!.SessionReference);
        Assert.Equal("Completed", Assert.Single(work.Results).Text);
        Assert.Equal("artifact://workspace", Assert.Single(work.Artifacts).Reference);
    }

    [Fact]
    public void RepeatedRunningObservationDoesNotDuplicateProgress()
    {
        WorkItem work = Running();
        AttemptSnapshot observed = work.Snapshot().Attempts[^1];
        var observation = new ExecutionObservation(ObservationKind.Running, Text: "Inspecting dependencies");

        Apply(work, observed, observation);
        Apply(work, observed, observation);

        Assert.Single(work.History, x => x.Kind == WorkEventKind.ProgressReported);
    }

    [Fact]
    public void PendingObservationCanCaptureSessionAndCheckpointWithoutCompleting()
    {
        WorkItem work = Claimed();
        var observation = new ExecutionObservation(ObservationKind.Pending, new("actual-model", "session"))
        { CheckpointId = 7 };

        Apply(work, work.Snapshot().Attempts[^1], observation);

        AttemptSnapshot attempt = work.Snapshot().Attempts[^1];
        Assert.Equal(AttemptStatus.Running, attempt.Status);
        Assert.Equal("session", attempt.Session!.SessionReference);
        Assert.Equal(7, attempt.CheckpointId);
        Assert.False(attempt.CleanupPending);
    }

    [Fact]
    public void InputRequestRecordsDecisionArtifactAndCleanupIntent()
    {
        WorkItem work = Running();
        var observation = new ExecutionObservation(ObservationKind.InputRequired, Text: "Which environment?",
            ArtifactReference: "artifact://workspace");

        Apply(work, work.Snapshot().Attempts[^1], observation, decisionId: 12);

        AttemptSnapshot attempt = work.Snapshot().Attempts[^1];
        Assert.Equal(AttemptStatus.Succeeded, attempt.Status);
        Assert.True(attempt.CleanupPending);
        Assert.Equal(12, Assert.Single(work.Decisions).Id);
        Assert.Equal("artifact://workspace", Assert.Single(work.Artifacts).Reference);
    }

    [Theory]
    [InlineData(ObservationKind.Failed, AttemptStatus.Failed, true)]
    [InlineData(ObservationKind.Uncertain, AttemptStatus.Uncertain, false)]
    [InlineData(ObservationKind.Stopped, AttemptStatus.Failed, true)]
    public void RuntimeOutcomeControlsTerminalStateAndCleanup(ObservationKind kind, AttemptStatus status, bool cleanup)
    {
        WorkItem work = Running();
        Apply(work, work.Snapshot().Attempts[^1], new(kind, Failure: FailureKind.HostUnavailable));

        AttemptSnapshot attempt = work.Snapshot().Attempts[^1];
        Assert.Equal(status, attempt.Status);
        Assert.Equal(cleanup, attempt.CleanupPending);
    }

    [Fact]
    public void PausedObservationIsAppliedOnceWhileWaiting()
    {
        WorkItem work = Running();
        AttemptSnapshot observed = work.Snapshot().Attempts[^1];
        var observation = new ExecutionObservation(ObservationKind.Paused, Text: "Choose a deployment target")
        { ReleaseWorkspace = true };

        Apply(work, observed, observation, decisionId: 10);
        Apply(work, observed, observation, decisionId: 11);

        AttemptSnapshot attempt = work.Snapshot().Attempts[^1];
        Assert.Equal(AttemptStatus.Waiting, attempt.Status);
        Assert.True(attempt.CleanupPending);
        Assert.Equal(10, Assert.Single(work.Decisions).Id);
    }

    [Fact]
    public void WorkspaceRequirementBecomesARepositoryRequest()
    {
        WorkItem work = Running();

        Apply(work, work.Snapshot().Attempts[^1], new(ObservationKind.WorkspaceRequired), ["jgador/goblin"]);

        Assert.Equal(new[] { "jgador/goblin" }, work.GitRepositoryRequest!.GitRepositories);
        Assert.Equal(AttemptStatus.Waiting, work.Snapshot().Attempts[^1].Status);
        Assert.True(work.Snapshot().Attempts[^1].CleanupPending);
    }

    private static void Apply(WorkItem work, AttemptSnapshot observed, ExecutionObservation observation,
        string[]? gitRepositories = null, long decisionId = 0) =>
        ExecutionObservationTransition.Apply(work, observed, observation, gitRepositories ?? [], decisionId, Now.AddMinutes(1));

    private static WorkItem Claimed()
    {
        var work = new WorkItem(1, "Inspect production", Now);
        work.Assign(2, Now);
        work.QueueExecution(3, new("codex", 4), Now);
        Assert.True(work.TryClaimExecution(3, 5, "sandbox/3", Now));
        return work;
    }

    private static WorkItem Running()
    {
        WorkItem work = Claimed();
        work.ExecutionStarted(3, 5, new("actual-model", "session"), Now);
        return work;
    }
}
