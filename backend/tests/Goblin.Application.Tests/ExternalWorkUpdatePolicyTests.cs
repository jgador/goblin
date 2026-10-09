using System;
using Goblin.Application.Work;
using Goblin.Contracts.Conversations;
using Goblin.Core.Work;
using Xunit;

namespace Goblin.Application.Tests;

public sealed class ExternalWorkUpdatePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ResultAndApprovalAreDistinctAndUnrelatedHistoryDoesNotRepeatUpdates()
    {
        WorkItem work = Running();
        Assert.Null(Pending(work));
        work.ProposeResult(3, 5, "All checks passed.", Now);
        ExternalWorkUpdateContent result = Assert.IsType<ExternalWorkUpdateContent>(Pending(work));
        Assert.Equal(ExternalWorkUpdateKind.ResultReady, result.Kind);
        Assert.Equal("All checks passed.", result.Text);
        Assert.Equal(WorkStatus.NeedsAttention, work.Status);
        work.AddContext(6, "Thanks", Now);
        Assert.Null(Pending(work, result.Sequence));
        work.ApproveResult(3, Now);
        ExternalWorkUpdateContent complete = Assert.IsType<ExternalWorkUpdateContent>(Pending(work, result.Sequence));
        Assert.Equal(ExternalWorkUpdateKind.Completed, complete.Kind);
        Assert.Equal(result.Text, complete.Text);
        Assert.True(complete.Sequence > result.Sequence);
        Assert.Null(Pending(work, complete.Sequence));
    }

    [Theory]
    [InlineData(FailureKind.DispatchFailed)]
    [InlineData(FailureKind.ConnectionUnavailable)]
    [InlineData(FailureKind.CapabilityUnavailable)]
    [InlineData(FailureKind.HostUnavailable)]
    [InlineData(FailureKind.ExecutionFailed)]
    [InlineData(FailureKind.TimedOut)]
    [InlineData(FailureKind.RuntimeDisconnected)]
    [InlineData(FailureKind.CancellationFailed)]
    [InlineData(FailureKind.CleanupFailed)]
    [InlineData(FailureKind.StorageUnavailable)]
    public void EveryConfirmedFailureNotifiesWithoutRetrying(FailureKind failure)
    {
        WorkItem work = Running();
        work.ExecutionFailed(3, 5, failure, Now);
        ExternalWorkUpdateContent update = Assert.IsType<ExternalWorkUpdateContent>(Pending(work));
        Assert.Equal(ExternalWorkUpdateKind.Failed, update.Kind);
        Assert.NotEmpty(update.Text);
        Assert.Equal(AttemptStatus.Failed, Assert.Single(work.Attempts).Status);
        Assert.Null(Pending(work, update.Sequence));
    }

    [Fact]
    public void FailureBeforeClaimAlsoNotifies()
    {
        var work = new WorkItem(1, "Inspect", Now);
        work.Assign(2, Now);
        work.QueueExecution(3, new("codex", 4), Now);
        work.DispatchFailed(3, FailureKind.ConnectionUnavailable, Now);
        Assert.Equal(ExternalWorkUpdateKind.Failed, Pending(work)!.Kind);
        Assert.Null(work.CurrentAttempt!.OwnerId);
    }

    [Fact]
    public void UncertaintyAndConfirmedFailureHaveSeparateNotifications()
    {
        WorkItem work = Running();
        work.ExecutionUncertain(3, 5, FailureKind.RuntimeDisconnected, Now);
        ExternalWorkUpdateContent uncertain = Assert.IsType<ExternalWorkUpdateContent>(Pending(work));
        Assert.Equal(ExternalWorkUpdateKind.Uncertain, uncertain.Kind);
        Assert.Contains("cannot confirm", uncertain.Text);
        work.ConfirmExecutionStopped(3, 5, Now);
        ExternalWorkUpdateContent stopped = Assert.IsType<ExternalWorkUpdateContent>(Pending(work, uncertain.Sequence));
        Assert.Equal(ExternalWorkUpdateKind.Failed, stopped.Kind);
        Assert.True(stopped.Sequence > uncertain.Sequence);
    }

    [Fact]
    public void CleanupFailureAndRecoveredResultBothNotify()
    {
        WorkItem work = Running();
        work.ProposeResult(3, 5, "Saved result", Now);
        work.RequireCleanup(3, 5, Now);
        Assert.Null(Pending(work));
        work.ReportCleanupFailure(3, 5, Now);
        ExternalWorkUpdateContent failure = Assert.IsType<ExternalWorkUpdateContent>(Pending(work));
        Assert.Equal(ExternalWorkUpdateKind.CleanupFailed, failure.Kind);
        work.ConfirmCleanup(3, 5, Now);
        ExternalWorkUpdateContent recovered = Assert.IsType<ExternalWorkUpdateContent>(Pending(work, failure.Sequence));
        Assert.Equal(ExternalWorkUpdateKind.ResultReady, recovered.Kind);
        Assert.Equal("Saved result", recovered.Text);
        Assert.True(recovered.Sequence > failure.Sequence);
        Assert.Null(Pending(work, recovered.Sequence));
    }

    [Fact]
    public void OrdinaryCleanupDoesNotRepeatAnAlreadyDeliveredFailure()
    {
        WorkItem work = Running();
        work.ExecutionFailed(3, 5, FailureKind.ExecutionFailed, Now);
        ExternalWorkUpdateContent failure = Pending(work)!;
        work.RequireCleanup(3, 5, Now);
        work.ConfirmCleanup(3, 5, Now);
        Assert.Null(Pending(work, failure.Sequence));
    }

    [Fact]
    public void SupersededResultsAndFailuresAreNotAnnouncedAsCurrent()
    {
        WorkItem result = Running();
        result.ProposeResult(3, 5, "First result", Now);
        result.RequestChanges(3, "Change this", Now);
        Assert.Null(Pending(result));

        WorkItem failure = Running();
        failure.ExecutionFailed(3, 5, FailureKind.ExecutionFailed, Now);
        failure.RetryExecution(6, new("codex", 4), Now);
        Assert.Null(Pending(failure));
        failure.DispatchFailed(6, FailureKind.ConnectionUnavailable, Now);
        Assert.Equal("The agent connection is unavailable.", Pending(failure)!.Text);
    }

    [Fact]
    public void QuestionsStayOnTheirExistingDeliveryPathAndCancellationNotifies()
    {
        WorkItem work = Running();
        work.RequestInput(3, 5, 6, "Which region?", Now);
        Assert.Null(Pending(work));
        Assert.NotNull(ExternalConversationPolicy.PendingQuestion(work.Snapshot(), null));
        work.RequestCancellation(Now);
        Assert.Equal(ExternalWorkUpdateKind.Cancelled, Pending(work)!.Kind);
    }

    private static ExternalWorkUpdateContent? Pending(WorkItem work, long? sequence = null) =>
        ExternalWorkUpdatePolicy.Pending(work.Snapshot(), sequence);

    private static WorkItem Running()
    {
        var work = new WorkItem(1, "Inspect", Now);
        work.Assign(2, Now);
        work.QueueExecution(3, new("codex", 4), Now);
        Assert.True(work.TryClaimExecution(3, 5, "sandbox/3", Now));
        work.ExecutionStarted(3, 5, new("model", "session"), Now);
        return work;
    }
}
