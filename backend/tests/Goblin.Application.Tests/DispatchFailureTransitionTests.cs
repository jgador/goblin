using System;
using Goblin.Application.Runtime;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;
using Xunit;

namespace Goblin.Application.Tests;

public sealed class DispatchFailureTransitionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FailureBeforeClaimFailsTheQueuedAttempt()
    {
        WorkItem work = Queued();

        Apply(work, FailureKind.DispatchFailed);

        AttemptSnapshot attempt = work.Snapshot().Attempts[^1];
        Assert.Equal(AttemptStatus.Failed, attempt.Status);
        Assert.Equal(FailureKind.DispatchFailed, attempt.Failure);
        Assert.Equal(WorkStatus.NeedsAttention, work.Status);
        Assert.Equal(AttentionReason.Failure, work.Attention!.Reason);
    }

    [Theory]
    [InlineData("starting")]
    [InlineData("running")]
    [InlineData("cancelling")]
    [InlineData("waiting")]
    public void FailureAfterClaimMakesActiveExecutionUncertain(string state)
    {
        WorkItem work = Active(state);

        Apply(work, FailureKind.HostUnavailable);

        AttemptSnapshot attempt = work.Snapshot().Attempts[^1];
        Assert.Equal(AttemptStatus.Uncertain, attempt.Status);
        Assert.Equal(FailureKind.HostUnavailable, attempt.Failure);
        Assert.Equal(AttentionReason.UncertainExecution, work.Attention!.Reason);
    }

    [Fact]
    public void FailureDuringPendingCleanupReportsCleanupAttention()
    {
        WorkItem work = Active("running");
        work.ProposeResult(3, 5, "Completed", Now);
        work.RequireCleanup(3, 5, Now);

        Apply(work, FailureKind.HostUnavailable);

        AttemptSnapshot attempt = work.Snapshot().Attempts[^1];
        Assert.Equal(AttemptStatus.Succeeded, attempt.Status);
        Assert.True(attempt.CleanupPending);
        Assert.True(attempt.CleanupFailed);
        Assert.Equal(AttentionReason.CleanupRequired, work.Attention!.Reason);
        Assert.Equal(FailureKind.CleanupFailed, work.Attention.Failure);
    }

    [Fact]
    public void StaleAttemptAndTurnEvidenceCannotChangeCurrentExecution()
    {
        WorkItem work = Active("running");
        int history = work.History.Count;

        DispatchFailureTransition.Apply(work, new(1, 99, FailureKind.HostUnavailable), Now);
        DispatchFailureTransition.Apply(work, new(1, 3, FailureKind.HostUnavailable, turnNumber: 2), Now);

        Assert.Equal(AttemptStatus.Running, work.Snapshot().Attempts[^1].Status);
        Assert.Equal(history, work.History.Count);
    }

    [Fact]
    public void RepeatedEvidenceDoesNotRewriteAnUncertainAttempt()
    {
        WorkItem work = Active("running");
        DispatchFailureEvidence evidence = new(1, 3, FailureKind.HostUnavailable);

        DispatchFailureTransition.Apply(work, evidence, Now);
        int history = work.History.Count;
        DispatchFailureTransition.Apply(work, evidence, Now.AddMinutes(1));

        Assert.Equal(AttemptStatus.Uncertain, work.Snapshot().Attempts[^1].Status);
        Assert.Equal(history, work.History.Count);
    }

    private static void Apply(WorkItem work, FailureKind failure) =>
        DispatchFailureTransition.Apply(work, new(1, 3, failure), Now.AddMinutes(1));

    private static WorkItem Queued()
    {
        var work = new WorkItem(1, "Inspect production", Now);
        work.Assign(2, Now);
        work.QueueExecution(3, new("codex", 4), Now);
        return work;
    }

    private static WorkItem Active(string state)
    {
        WorkItem work = Queued();
        Assert.True(work.TryClaimExecution(3, 5, "sandbox/3", Now));
        if (state != "starting") work.ExecutionStarted(3, 5, new("model", "session"), Now);
        if (state == "cancelling") work.RequestCancellation(Now);
        if (state == "waiting") work.PauseForInput(3, 5, 6, "Continue?", false, Now);
        return work;
    }
}
