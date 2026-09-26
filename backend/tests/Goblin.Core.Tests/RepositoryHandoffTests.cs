using System;
using Goblin.Core.Work;
using Xunit;

namespace Goblin.Core.Tests;

public sealed class RepositoryHandoffTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly ExecutionTarget Text = new("codex", 1, "chosen-model", requestedEffort: "high");
    private static ExecutionTarget Repository(long attempt) => new("codex", 1, "chosen-model",
        new("owner/repo", "Goblin", "agent@example.com", new(1, "generation", "42", "owner", 7, "main", $"goblin/1/{attempt}")), "high");

    private static WorkItem Ready()
    {
        var work = new WorkItem(1, "Update the repository", Now);
        work.Assign(1, Now);
        return work;
    }
    private static WorkItem Running()
    {
        WorkItem work = Ready();
        work.QueueExecution(10, Text, Now);
        work.TryClaimExecution(10, 100, "text/10", Now);
        work.ExecutionStarted(10, 100, new("chosen-model", "text-session", "turn"), Now);
        return work;
    }

    [Fact]
    public void InitialSetupPersistsWithoutCreatingAnAttemptOrWorkspace()
    {
        WorkItem work = Ready();
        work.RequestRepositorySetup(Text, ["owner/repo"], Now);
        work = WorkItem.Restore(work.Snapshot());
        Assert.Equal(AttentionReason.RepositoryRequired, work.Attention!.Reason);
        Assert.Empty(work.Attempts);
        Assert.Null(work.Workspace);
        Assert.Equal(Text, work.RepositoryRequest!.Target);
        work.AuthorizeRepository(11, Repository(11), Now);
        Assert.Equal(WorkStatus.Queued, work.Status);
        Assert.Null(work.RepositoryRequest);
        Assert.Equal("high", work.CurrentAttempt!.Target.RequestedEffort);
    }

    [Fact]
    public void RuntimeHandoffWaitsForConfirmedCleanupAndPreservesPriorProvenance()
    {
        WorkItem work = Running();
        work.RequestRepositorySetupFromExecution(10, 100, [], Now);
        Assert.Throws<WorkRuleException>(() => work.AuthorizeRepository(11, Repository(11), Now));
        work.ReportCleanupFailure(10, 100, Now);
        Assert.Equal(AttentionReason.CleanupRequired, work.Attention!.Reason);
        work = WorkItem.Restore(work.Snapshot());
        work.ConfirmCleanup(10, 100, Now);
        Assert.Equal(AttentionReason.RepositoryRequired, work.Attention!.Reason);
        work.AuthorizeRepository(11, Repository(11), Now);
        Assert.Equal(2, work.Attempts.Count);
        Assert.Equal(AttemptStatus.Succeeded, work.Attempts[0].Status);
        Assert.Equal(Text, work.Attempts[0].Target);
        Assert.Equal("text/10", work.Attempts[0].EnvironmentReference);
        Assert.Equal("text-session", work.Attempts[0].Session!.SessionReference);
        Assert.Throws<WorkRuleException>(() => work.ProposeResult(10, 100, "Late result", Now));
        Assert.True(work.TryClaimExecution(11, 101, "k8s/agents/work-1", Now));
        Assert.Equal("k8s/agents/work-1", work.Workspace!.EnvironmentReference);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WaitingConversationCanAuthorizeRepositoryWithOrWithoutAnotherAnswer(bool answerFirst)
    {
        WorkItem work = Running();
        work.PauseForInput(10, 100, 101, "Provide repository contents", true, Now);
        if (answerFirst) work.RequestRepositorySetupForAnswer(101, "Clone owner/repo", ["owner/repo"], Now);
        work.AuthorizeRepository(11, Repository(11), Now);
        Assert.NotNull(Assert.Single(work.Decisions).Answer);
        Assert.Equal(1, work.Attempts[0].TurnNumber);
        Assert.Equal(2, work.Attempts.Count);
        Assert.Equal(WorkStatus.Queued, work.Status);
    }

    [Fact]
    public void AuthorizationCannotBypassUncertaintyOrFailureOrChangeRuntimeIdentity()
    {
        WorkItem work = Running();
        work.ExecutionUncertain(10, 100, FailureKind.RuntimeDisconnected, Now);
        Assert.Throws<WorkRuleException>(() => work.AuthorizeRepository(11, Repository(11), Now));
        work.ConfirmExecutionStopped(10, 100, Now);
        Assert.Throws<WorkRuleException>(() => work.AuthorizeRepository(11, Repository(11), Now));
        Assert.Equal(AttentionReason.Failure, work.Attention!.Reason);

        work = Ready();
        work.RequestRepositorySetup(Text, [], Now);
        Assert.Throws<WorkRuleException>(() => work.AuthorizeRepository(11,
            new("another-runtime", 2, repository: Repository(11).Repository), Now));
        Assert.Throws<WorkRuleException>(() => work.AuthorizeRepository(11,
            new("codex", 1, "chosen-model", new("owner/repo", "Goblin", "agent@example.com"), "high"), Now));
        Assert.Empty(work.Attempts);
    }
}
