using System;
using System.Collections.Generic;
using System.Linq;
using Goblin.Core.Work;
using Xunit;

namespace Goblin.Core.Tests;

public sealed class WorkLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WorkExistsBeforeAssignmentConversationOrExecution()
    {
        var work = new WorkItem(Guid.NewGuid(), "Investigate the slow build", Now);
        Assert.Equal(WorkStatus.Ready, work.Status);
        Assert.Null(work.AgentId);
        Assert.Null(work.Attention);
        Assert.Empty(work.Attempts);
        Assert.Equal(WorkEventKind.Created, Assert.Single(work.History).Kind);
        Reject(WorkRule.AgentRequired, () => work.QueueExecution(Guid.NewGuid(), Target(), Now));
        Assert.Empty(work.Attempts);
        Assert.Single(work.History);
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("contract-test-runtime")]
    public void SuccessfulExecutionRequiresAnExplicitResultReview(string runtime)
    {
        (WorkItem? work, Guid attempt, Guid owner) = Running(runtime);
        work.ProposeResult(attempt, owner, "The first implementation is ready.", Now);

        Assert.Equal(AttemptStatus.Succeeded, work.CurrentAttempt!.Status);
        Assert.Equal(WorkStatus.NeedsAttention, work.Status);
        Assert.Equal(AttentionReason.ResultReview, work.Attention!.Reason);
        Assert.Null(Assert.Single(work.Results).ApprovedAt);
        Assert.Equal(runtime, work.CurrentAttempt.Target.Runtime);
        Assert.Equal(work.Id, work.CurrentAttempt.WorkId);
        Assert.Equal(work.AgentId, work.CurrentAttempt.AgentId);

        work.ApproveResult(attempt, Now.AddMinutes(1));
        Assert.Equal(WorkStatus.Completed, work.Status);
        Assert.Null(work.Attention);
        Assert.Equal(Now.AddMinutes(1), work.Results[0].ApprovedAt);
        Reject(WorkRule.InvalidTransition, () => work.ApproveResult(attempt, Now));
        Reject(WorkRule.InvalidTransition, () => work.RequestCancellation(Now));
        Reject(WorkRule.InvalidTransition, () => work.QueueExecution(Guid.NewGuid(), Target(), Now));
    }

    [Fact]
    public void DuplicateDeliveryAndCompetingOwnersCannotClaimAnAttemptAgain()
    {
        (WorkItem? work, Guid attempt) = Queued();
        Guid owner = Guid.NewGuid();
        Assert.True(work.TryClaimExecution(attempt, owner, "sandbox/attempt-one", Now));
        Assert.False(work.TryClaimExecution(attempt, owner, "sandbox/duplicate", Now));
        Assert.False(work.TryClaimExecution(attempt, Guid.NewGuid(), "sandbox/competitor", Now));
        Assert.Equal(owner, work.CurrentAttempt!.OwnerId);
        Assert.Equal("sandbox/attempt-one", work.CurrentAttempt.EnvironmentReference);
        Assert.Single(work.History, x => x.Kind == WorkEventKind.ExecutionClaimed);
    }

    public static IEnumerable<object[]> Failures() => Enum.GetValues<FailureKind>().Select(x => new object[] { x });

    [Theory]
    [MemberData(nameof(Failures))]
    public void EveryFailureBeforeExecutionRequiresAttentionAndAnExplicitRetry(FailureKind failure)
    {
        (WorkItem? work, Guid attempt) = Queued();
        work.DispatchFailed(attempt, failure, Now);
        Assert.Equal(WorkStatus.NeedsAttention, work.Status);
        Assert.Equal(new WorkAttention(AttentionReason.Failure, failure), work.Attention);
        Assert.Equal(AttemptStatus.Failed, work.CurrentAttempt!.Status);
        Assert.False(work.TryClaimExecution(attempt, Guid.NewGuid(), "sandbox/redelivery", Now));
        Reject(WorkRule.InvalidTransition, () => work.QueueExecution(Guid.NewGuid(), Target(), Now));
        Assert.Single(work.Attempts);

        Guid retry = Guid.NewGuid();
        work.RetryExecution(retry, Target(), Now.AddMinutes(1));
        Assert.Equal(WorkStatus.Queued, work.Status);
        Assert.Null(work.Attention);
        Assert.Equal(retry, work.CurrentAttempt.Id);
        Assert.Equal(2, work.Attempts.Count);
        Assert.Equal(AttemptStatus.Failed, work.Attempts[0].Status);
        Assert.Contains(work.History, x => x.Kind == WorkEventKind.RetryRequested && x.AttemptId == attempt);
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public void EveryConfirmedExecutionFailureRequiresAttention(FailureKind failure)
    {
        (WorkItem? work, Guid attempt, Guid owner) = Running();
        work.ExecutionFailed(attempt, owner, failure, Now);
        Assert.Equal(WorkStatus.NeedsAttention, work.Status);
        Assert.Equal(new WorkAttention(AttentionReason.Failure, failure), work.Attention);
        Assert.Equal(AttemptStatus.Failed, work.CurrentAttempt!.Status);
        Assert.Equal(Now, work.CurrentAttempt.FinishedAt);
        Assert.Single(work.Attempts);
        Assert.DoesNotContain(work.History, x => x.Kind == WorkEventKind.RetryRequested);
    }

    [Fact]
    public void ACrashAfterClaimRequiresReconciliationBeforeAnyRetry()
    {
        (WorkItem? work, Guid attempt) = Queued();
        Guid owner = Guid.NewGuid();
        Assert.True(work.TryClaimExecution(attempt, owner, "sandbox/possibly-started", Now));
        // A claim was committed, but the process crashed before recording a start.
        work.ExecutionUncertain(attempt, owner, FailureKind.HostUnavailable, Now);
        Assert.Equal(AttentionReason.UncertainExecution, work.Attention!.Reason);
        Assert.Null(work.CurrentAttempt!.FinishedAt);
        Assert.False(work.TryClaimExecution(attempt, owner, "sandbox/replacement", Now));
        Reject(WorkRule.ReconciliationRequired, () => work.RetryExecution(Guid.NewGuid(), Target(), Now));
        Reject(WorkRule.InvalidTransition, () => work.QueueExecution(Guid.NewGuid(), Target(), Now));

        work.ConfirmExecutionStopped(attempt, owner, Now.AddMinutes(1));
        Assert.Equal(WorkStatus.NeedsAttention, work.Status);
        Assert.Equal(AttentionReason.Failure, work.Attention.Reason);
        Assert.Single(work.Attempts);
        Assert.False(work.TryClaimExecution(attempt, owner, "sandbox/replacement", Now));

        work.RetryExecution(Guid.NewGuid(), Target(), Now.AddMinutes(2));
        Assert.Equal(2, work.Attempts.Count);
    }

    [Fact]
    public void AConfirmedLateResultResolvesUncertaintyWithoutRepeatingWork()
    {
        (WorkItem? work, Guid attempt, Guid owner) = Running();
        work.ExecutionUncertain(attempt, owner, FailureKind.RuntimeDisconnected, Now);
        work.ProposeResult(attempt, owner, "Recovered result from the original execution.", Now);
        Assert.Equal(AttemptStatus.Succeeded, work.CurrentAttempt!.Status);
        Assert.Equal(AttentionReason.ResultReview, work.Attention!.Reason);
        Assert.Single(work.Attempts);
        Assert.Contains(work.History, x => x.Kind == WorkEventKind.ExecutionUncertain);
        Reject(WorkRule.InvalidTransition, () => work.RetryExecution(Guid.NewGuid(), Target(), Now));
    }

    [Fact]
    public void OldOrUnownedExecutionEventsCannotChangeCurrentWork()
    {
        (WorkItem? work, Guid attempt, Guid owner) = Running();
        int events = work.History.Count;
        Reject(WorkRule.OwnershipMismatch, () => work.ProposeResult(attempt, Guid.NewGuid(), "Wrong owner", Now));
        Reject(WorkRule.InvalidTransition, () => work.DispatchFailed(attempt, FailureKind.DispatchFailed, Now));
        Assert.Equal(events, work.History.Count);
        work.ExecutionFailed(attempt, owner, FailureKind.ExecutionFailed, Now);
        Guid retry = Guid.NewGuid();
        work.RetryExecution(retry, Target(), Now);
        events = work.History.Count;
        Reject(WorkRule.AttemptNotCurrent, () => work.ProposeResult(attempt, owner, "Stale reply", Now));
        Assert.False(work.TryClaimExecution(attempt, owner, "sandbox/stale", Now));
        Assert.Equal(events, work.History.Count);
        Assert.Equal(retry, work.CurrentAttempt!.Id);
        Assert.Empty(work.Results);
    }

    [Fact]
    public void RetryingCapturesNewConfigurationWithoutRewritingProvenance()
    {
        (WorkItem? work, Guid attempt, Guid owner) = Running();
        ExecutionAttempt original = work.CurrentAttempt!;
        ExecutionTarget originalTarget = original.Target;
        work.ExecutionFailed(attempt, owner, FailureKind.ExecutionFailed, Now);
        var replacementTarget = new ExecutionTarget("codex", Guid.NewGuid(), "different-requested-model");
        work.RetryExecution(Guid.NewGuid(), replacementTarget, Now);
        Assert.Equal(originalTarget, original.Target);
        Assert.Equal("actual-model", original.Session!.Model);
        Assert.Equal("opaque-session", original.Session.SessionReference);
        Assert.Equal("opaque-operation", original.Session.OperationReference);
        Assert.Equal(replacementTarget, work.CurrentAttempt!.Target);
        Assert.Null(work.CurrentAttempt.Session);
        Assert.Equal(original.AgentId, work.CurrentAttempt.AgentId);
        Assert.NotEqual(original.Id, work.CurrentAttempt.Id);
    }

    [Fact]
    public void InvalidRetriesAreAtomicAndCannotReuseAttemptIdentity()
    {
        (WorkItem? work, Guid attempt) = Queued();
        work.DispatchFailed(attempt, FailureKind.ConnectionUnavailable, Now);
        int events = work.History.Count;
        Reject(WorkRule.AttemptAlreadyExists, () => work.RetryExecution(attempt, Target(), Now));
        Reject(WorkRule.InvalidValue, () => work.RetryExecution(Guid.Empty, Target(), Now));
        Assert.Equal(events, work.History.Count);
        Assert.Single(work.Attempts);
        Assert.Equal(AttentionReason.Failure, work.Attention!.Reason);
    }

    [Fact]
    public void InputAndRevisionsStayWithWorkAcrossSeparateAttempts()
    {
        (WorkItem? work, Guid attempt, Guid owner) = Running();
        Guid agent = work.AgentId!.Value;
        Guid decision = Guid.NewGuid();
        work.RequestInput(attempt, owner, decision, "Which setup approach?", Now);
        Assert.Equal(AttemptStatus.Succeeded, work.CurrentAttempt!.Status);
        Assert.Equal(AttentionReason.InputRequired, work.Attention!.Reason);
        Reject(WorkRule.InvalidTransition, () => work.ApproveResult(attempt, Now));
        Reject(WorkRule.DecisionNotCurrent, () => work.AnswerDecision(Guid.NewGuid(), "Guided", Now));
        work.AnswerDecision(decision, "Keep setup guided.", Now);
        Assert.Equal(WorkStatus.Ready, work.Status);
        Assert.Single(work.Attempts); // Answering does not silently launch anything.
        Assert.Equal("Keep setup guided.", Assert.Single(work.Decisions).Answer);
        Reject(WorkRule.InvalidTransition, () => work.AnswerDecision(decision, "Repeated", Now));

        Guid next = StartAnother(work);
        work.ProposeResult(next, Owner(work), "First draft", Now);
        work.RequestChanges(next, "Include recovery instructions.", Now);
        Assert.Equal(WorkStatus.Ready, work.Status);
        Assert.Equal("Include recovery instructions.", work.Results[0].RequestedChanges);
        Guid final = StartAnother(work);
        work.ProposeResult(final, Owner(work), "Revised draft", Now);
        Reject(WorkRule.AttemptNotCurrent, () => work.ApproveResult(next, Now));
        work.ApproveResult(final, Now);

        Assert.Equal(WorkStatus.Completed, work.Status);
        Assert.Equal(2, work.Results.Count);
        Assert.Null(work.Results[0].ApprovedAt);
        Assert.NotNull(work.Results[1].ApprovedAt);
        Assert.Equal(agent, work.AgentId);
        Assert.All(work.Attempts, x => Assert.Equal(agent, x.AgentId));
        Assert.Equal(Enumerable.Range(1, work.History.Count).Select(x => (long)x), work.History.Select(x => x.Sequence));
    }

    [Fact]
    public void CompletionBeforeStartAcknowledgementStillRecordsProvenanceWithoutResuming()
    {
        (WorkItem? work, Guid attempt) = Queued();
        Guid owner = Guid.NewGuid();
        work.TryClaimExecution(attempt, owner, "sandbox/fast-result", Now);
        work.ProposeResult(attempt, owner, "Result notification arrived before the start response.", Now);
        work.ExecutionStarted(attempt, owner, new("reported-model", "reported-session"), Now);
        Assert.Equal(AttemptStatus.Succeeded, work.CurrentAttempt!.Status);
        Assert.Equal(WorkStatus.NeedsAttention, work.Status);
        Assert.Equal(AttentionReason.ResultReview, work.Attention!.Reason);
        Assert.Equal("reported-model", work.CurrentAttempt.Session!.Model);
        Assert.Equal("reported-session", work.CurrentAttempt.Session.SessionReference);
        Reject(WorkRule.InvalidTransition, () => work.ExecutionStarted(attempt, owner, new("changed-model"), Now));
        Assert.Equal("reported-model", work.CurrentAttempt.Session.Model);
    }

    [Fact]
    public void ADelayedStartAcknowledgementDoesNotClearFailureAttention()
    {
        (WorkItem? work, Guid attempt) = Queued();
        Guid owner = Guid.NewGuid();
        work.TryClaimExecution(attempt, owner, "sandbox/lost-acknowledgement", Now);
        work.ExecutionUncertain(attempt, owner, FailureKind.RuntimeDisconnected, Now);
        work.ExecutionStarted(attempt, owner, new("model", "session"), Now);
        Assert.Equal(AttemptStatus.Uncertain, work.CurrentAttempt!.Status);
        Assert.Equal(AttentionReason.UncertainExecution, work.Attention!.Reason);
        Reject(WorkRule.ReconciliationRequired, () => work.RetryExecution(Guid.NewGuid(), Target(), Now));
    }

    [Fact]
    public void CancellationBeforeClaimPreventsDispatch()
    {
        (WorkItem? work, Guid attempt) = Queued();
        work.RequestCancellation(Now);
        Assert.Equal(WorkStatus.Cancelled, work.Status);
        Assert.Equal(AttemptStatus.Cancelled, work.CurrentAttempt!.Status);
        Assert.False(work.TryClaimExecution(attempt, Guid.NewGuid(), "sandbox/cancelled", Now));
        int events = work.History.Count;
        work.RequestCancellation(Now);
        Assert.Equal(events, work.History.Count);
    }

    [Fact]
    public void CancellationRequiresConfirmationEvenWhenRequestedBeforeStartNotification()
    {
        (WorkItem? work, Guid attempt) = Queued();
        Guid owner = Guid.NewGuid();
        work.TryClaimExecution(attempt, owner, "sandbox/starting", Now);
        work.RequestCancellation(Now);
        Assert.Equal(WorkStatus.Cancelling, work.Status);
        Assert.Null(work.CurrentAttempt!.FinishedAt);
        work.ExecutionStarted(attempt, owner, new("model"), Now);
        Assert.Equal(AttemptStatus.CancellationRequested, work.CurrentAttempt.Status);
        Assert.Equal(WorkStatus.Cancelling, work.Status);
        work.ConfirmExecutionStopped(attempt, owner, Now);
        Assert.Equal(WorkStatus.Cancelled, work.Status);
        Assert.Equal(AttemptStatus.Cancelled, work.CurrentAttempt.Status);
    }

    [Fact]
    public void FailedCancellationRequiresAttentionAndCannotEnableAReplacement()
    {
        (WorkItem? work, Guid attempt, Guid owner) = Running();
        work.RequestCancellation(Now);
        work.ExecutionUncertain(attempt, owner, FailureKind.CancellationFailed, Now);
        Assert.Equal(WorkStatus.NeedsAttention, work.Status);
        Assert.Equal(new WorkAttention(AttentionReason.UncertainExecution, FailureKind.CancellationFailed), work.Attention);
        Reject(WorkRule.ReconciliationRequired, () => work.RetryExecution(Guid.NewGuid(), Target(), Now));
        work.ConfirmExecutionStopped(attempt, owner, Now);
        Assert.Equal(WorkStatus.Cancelled, work.Status);
        Assert.Single(work.Attempts);
        Assert.Contains(work.History, x => x.Failure == FailureKind.CancellationFailed);
    }

    [Fact]
    public void CompletionRacingCancellationStillRequiresReviewOfTheActualResult()
    {
        (WorkItem? work, Guid attempt, Guid owner) = Running();
        work.RequestCancellation(Now);
        work.ProposeResult(attempt, owner, "Completed before cancellation reached the runtime.", Now);
        Assert.Equal(AttemptStatus.Succeeded, work.CurrentAttempt!.Status);
        Assert.NotNull(work.CurrentAttempt.CancellationRequestedAt);
        Assert.Equal(AttentionReason.ResultReview, work.Attention!.Reason);
        Assert.DoesNotContain(work.History, x => x.Kind == WorkEventKind.Cancelled);
    }

    [Fact]
    public void CancellationOfAnUncertainAttemptMustStillWaitForTheHost()
    {
        (WorkItem? work, Guid attempt, Guid owner) = Running();
        work.ExecutionUncertain(attempt, owner, FailureKind.TimedOut, Now);
        work.RequestCancellation(Now);
        Assert.Equal(WorkStatus.Cancelling, work.Status);
        Assert.Null(work.CurrentAttempt!.FinishedAt);
        Reject(WorkRule.InvalidTransition, () => work.RetryExecution(Guid.NewGuid(), Target(), Now));
        work.ConfirmExecutionStopped(attempt, owner, Now);
        Assert.Equal(WorkStatus.Cancelled, work.Status);
    }

    [Fact]
    public void ReadModelsCannotMutateTheAggregatesCollections()
    {
        (WorkItem? work, Guid _, Guid _) = Running();
        Assert.Throws<NotSupportedException>(() => ((IList<WorkEvent>)work.History).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<ExecutionAttempt>)work.Attempts).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<WorkDecision>)work.Decisions).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<WorkResult>)work.Results).Clear());
    }

    private static void Reject(WorkRule rule, Action action) =>
        Assert.Equal(rule, Assert.Throws<WorkRuleException>(action).Rule);

    private static ExecutionTarget Target(string runtime = "codex") => new(runtime, Guid.NewGuid(), "requested-model");

    private static (WorkItem Work, Guid Attempt) Queued(string runtime = "codex")
    {
        var work = new WorkItem(Guid.NewGuid(), "Prepare a reviewable change", Now);
        work.Assign(Guid.NewGuid(), Now);
        Guid attempt = Guid.NewGuid();
        work.QueueExecution(attempt, Target(runtime), Now);
        return (work, attempt);
    }

    private static (WorkItem Work, Guid Attempt, Guid Owner) Running(string runtime = "codex")
    {
        (WorkItem? work, Guid attempt) = Queued(runtime);
        Guid owner = Guid.NewGuid();
        work.TryClaimExecution(attempt, owner, "sandbox/" + attempt, Now);
        work.ExecutionStarted(attempt, owner, new("actual-model", "opaque-session", "opaque-operation"), Now);
        return (work, attempt, owner);
    }

    private static Guid StartAnother(WorkItem work)
    {
        Guid attempt = Guid.NewGuid();
        Guid owner = Guid.NewGuid();
        work.QueueExecution(attempt, Target(), Now);
        work.TryClaimExecution(attempt, owner, "sandbox/" + attempt, Now);
        work.ExecutionStarted(attempt, owner, new("actual-model"), Now);
        return attempt;
    }

    private static Guid Owner(WorkItem work) => work.CurrentAttempt!.OwnerId!.Value;
}
