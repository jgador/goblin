using System;
using System.Linq;
using Goblin.Core.Work;
using Xunit;

namespace Goblin.Core.Tests;

public sealed class WorkspaceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static WorkItem Running()
    {
        var work = new WorkItem(1, "Investigate and fix the issue", Now);
        work.Assign(1, Now);
        work.QueueExecution(1, new("codex", 1, repository: new("owner/repo", "Goblin", "agent@example.com")), Now);
        work.TryClaimExecution(1, 10, "k8s/agents/run-1/1", Now);
        work.ExecutionStarted(1, 10, new("model", "thread", "turn-1"), Now);
        return work;
    }
    [Fact]
    public void SuspensionRetainsWorkspaceWithoutAFileArchive()
    {
        WorkItem work = Running();
        WorkWorkspace workspace = work.Workspace!;
        work.PauseForInput(1, 10, 11, "Which database?", true, Now);
        work.RequireCleanup(1, 10, Now);
        work.ConfirmCleanup(1, 10, Now);
        Assert.Equal(AttemptStatus.Waiting, work.CurrentAttempt!.Status);
        Assert.Null(work.CurrentAttempt.CheckpointId);
        Assert.Equal(workspace, WorkItem.Restore(work.Snapshot()).Workspace);
    }
    [Fact]
    public void RetainedWorkspaceContinuesSameAttemptAndPreservesTurnOwnership()
    {
        WorkItem work = Running();
        work.PauseForInput(1, 10, 11, "Which database?", false, Now);
        work.AddContext(12, "Consider the deployment constraints", Now);
        Assert.Single(work.Attempts);
        Assert.Equal(AttemptStatus.Waiting, work.CurrentAttempt!.Status);
        work.AnswerDecision(11, "PostgreSQL", Now);
        Assert.Single(work.Attempts);
        Assert.Equal(2, work.CurrentAttempt.TurnNumber);
        Assert.Equal(1, work.CurrentAttempt.WorkspaceNumber);
        Assert.False(work.CurrentAttempt.ReasoningOnly);
        Assert.Equal(10, Assert.Single(work.CurrentAttempt.PriorTurns).OwnerId);
        Assert.True(work.TryClaimExecution(1, 20, "k8s/agents/run-1/1", Now));
        Assert.Throws<WorkRuleException>(() => work.ProposeResult(1, 10, "Stale result", Now));
        var restored = WorkItem.Restore(work.Snapshot());
        Assert.Equal(2, restored.CurrentAttempt!.TurnNumber);
        Assert.Equal("thread", restored.CurrentAttempt.PriorTurns.Single().Session!.SessionReference);
    }
    [Fact]
    public void ReleasedWorkspaceContinuesWithReasoningBeforeAllocatingRepositoryCompute()
    {
        WorkItem work = Running();
        long checkpoint = 9007199254740993;
        work.SaveWorkspace(1, 10, checkpoint, Now);
        work.PauseForInput(1, 10, 11, "Which database?", true, Now);
        work.RequireCleanup(1, 10, Now);
        Assert.Throws<WorkRuleException>(() => work.AnswerDecision(11, "PostgreSQL", Now));
        work.ConfirmCleanup(1, 10, Now);
        work.AnswerDecision(11, "PostgreSQL", Now);
        Assert.True(work.CurrentAttempt!.ReasoningOnly);
        Assert.Equal(2, work.CurrentAttempt.WorkspaceNumber);
        work.TryClaimExecution(1, 20, "text/1", Now);
        work.RequireRepositoryExecution(1, 20, Now);
        Assert.False(work.CurrentAttempt.ReasoningOnly);
        Assert.Equal(3, work.CurrentAttempt.TurnNumber);
        Assert.Equal(2, work.CurrentAttempt.WorkspaceNumber);
        Assert.Single(work.Attempts);
        Assert.Equal(checkpoint, work.CurrentAttempt.CheckpointId);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CheckpointIdentityMustBePositive(long checkpoint)
    {
        WorkItem work = Running();
        Assert.Throws<WorkRuleException>(() => work.SaveWorkspace(1, 10, checkpoint, Now));
        Assert.Null(work.CurrentAttempt!.CheckpointId);
    }
    [Fact]
    public void CancellingARetainedWorkspaceRequiresHostConfirmation()
    {
        WorkItem work = Running();
        work.PauseForInput(1, 10, 11, "Which database?", false, Now);
        work.RequestCancellation(Now);
        Assert.Equal(WorkStatus.Cancelling, work.Status);
        work.ConfirmExecutionStopped(1, 10, Now);
        Assert.Equal(WorkStatus.Cancelled, work.Status);
    }

    [Fact]
    public void RetryKeepsWorkWorkspaceButRequiresConfirmedCleanupAndNewOwnership()
    {
        WorkItem work = Running();
        WorkWorkspace original = work.Workspace!;
        work.ExecutionFailed(1, 10, FailureKind.RuntimeDisconnected, Now);
        work.RequireCleanup(1, 10, Now);
        work.ReportCleanupFailure(1, 10, Now);
        Assert.Throws<WorkRuleException>(() => work.RetryExecution(2, work.CurrentAttempt!.Target, Now));
        work.ConfirmCleanup(1, 10, Now);
        work.RetryExecution(2, work.CurrentAttempt!.Target, Now);
        work.TryClaimExecution(2, 20, "another-host", Now);
        WorkItem restored = WorkItem.Restore(work.Snapshot());
        Assert.Equal(original.EnvironmentReference, restored.Workspace!.EnvironmentReference);
        Assert.Equal(original.EnvironmentReference, restored.CurrentAttempt!.EnvironmentReference);
        Assert.Equal(2, restored.Workspace.AttemptId);
        Assert.Throws<WorkRuleException>(() => restored.ConfirmCleanup(1, 10, Now));
        restored.ProposeResult(2, 20, "Ready", Now);
        restored.RequireCleanup(2, 20, Now);
        Assert.Throws<WorkRuleException>(() => restored.ApproveResult(2, Now));
        restored.ConfirmCleanup(2, 20, Now);
        restored.ApproveResult(2, Now);
        Assert.Equal(original.EnvironmentReference, restored.Workspace!.EnvironmentReference);
    }

    [Fact]
    public void AWorkCannotReuseItsWorkspaceForAnotherRepository()
    {
        WorkItem work = Running();
        work.ExecutionFailed(1, 10, FailureKind.ExecutionFailed, Now);
        Assert.Throws<WorkRuleException>(() => work.RetryExecution(2,
            new("codex", 1, repository: new("owner/other", "Goblin", "agent@example.com")), Now));
    }
}
