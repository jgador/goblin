using System;
using Goblin.Core.Work;
using Xunit;

namespace Goblin.Core.Tests;

public sealed class RepositoryAuthorizationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static ExecutionTarget Target(long id, bool push = false, bool pr = false) => new("codex", 1, repository:
        new("owner/repo", "Goblin", "agent@example.com", new(1, "generation", "42", "owner", 22, "develop", $"goblin/1/{id}", 2, push, pr)));
    private static WorkItem Ready()
    {
        var work = new WorkItem(1, "Fix owner/repo", Now);
        work.Assign(1, Now);
        return work;
    }

    [Fact]
    public void PreviewSurvivesRestoreAndCannotExecuteUntilExactRequestIsApproved()
    {
        WorkItem work = Ready();
        work.PrepareRepositoryAuthorization(10, Target(10), true, false, Now, "main");
        work = WorkItem.Restore(work.Snapshot());
        Assert.Empty(work.Attempts);
        Assert.True(work.RepositoryAuthorization!.EnableRepository);
        Assert.False(work.TryClaimExecution(10, 100, "sandbox", Now));
        Assert.Throws<WorkRuleException>(() => work.AuthorizeRepository(11, Target(10), Now));
        Assert.Throws<WorkRuleException>(() => work.AuthorizeRepository(10, Target(10, true), Now));
        work.AuthorizeRepository(10, Target(10), Now);
        Assert.True(work.TryClaimExecution(10, 100, "sandbox", Now));
        Assert.False(work.TryClaimExecution(10, 101, "sandbox", Now));
        Assert.Equal(RepositoryAuthorizationStatus.Authorized, work.RepositoryAuthorization.Status);
    }

    [Theory]
    [InlineData("context")]
    [InlineData("deny")]
    [InlineData("cancel")]
    public void ChangedOrDeclinedRequestCannotBeApproved(string action)
    {
        WorkItem work = Ready();
        work.QueueExecution(10, Target(10), Now);
        if (action == "context") work.AddContext(11, "Do not push", Now);
        if (action == "deny") work.DenyRepositoryAuthorization(10, Now);
        if (action == "cancel") work.RequestCancellation(Now);
        Assert.Throws<WorkRuleException>(() => work.AuthorizeRepository(10, Target(10), Now));
        Assert.Empty(work.Attempts);
    }

    [Fact]
    public void RetryNeedsFreshApprovalAndPreservesOriginalAttempt()
    {
        WorkItem work = Ready();
        work.QueueExecution(10, Target(10), Now);
        work.AuthorizeRepository(10, Target(10), Now);
        work.DispatchFailed(10, FailureKind.HostUnavailable, Now);
        work.RetryExecution(11, Target(11, true), Now);
        Assert.Single(work.Attempts);
        Assert.True(work.RepositoryAuthorization!.Retry);
        work.DenyRepositoryAuthorization(11, Now);
        work.PrepareRepositoryAuthorization(12, Target(12), false, true, Now);
        work.AuthorizeRepository(12, Target(12), Now);
        Assert.Equal(AttemptStatus.Failed, work.Attempts[0].Status);
        Assert.Equal(12, work.CurrentAttempt!.Id);
    }

    [Fact]
    public void ChangingScopeReleasesRetainedComputeBeforeAnotherAttemptCanQueue()
    {
        WorkItem work = Ready();
        work.QueueExecution(10, Target(10), Now);
        work.AuthorizeRepository(10, Target(10), Now);
        work.TryClaimExecution(10, 100, "sandbox", Now);
        work.PauseForInput(10, 100, 101, "Deliver changes?", false, Now);
        work.PrepareRepositoryAuthorization(11, Target(11, true), false, false, Now);
        Assert.True(work.CurrentAttempt!.CleanupPending);
        Assert.True(work.CurrentAttempt.ReleaseWorkspace);
        Assert.Throws<WorkRuleException>(() => work.AuthorizeRepository(11, Target(11, true), Now));
        work.ConfirmCleanup(10, 100, Now);
        work.AuthorizeRepository(11, Target(11, true), Now);
        Assert.Equal(AttemptStatus.Succeeded, work.Attempts[0].Status);
        Assert.Equal(WorkStatus.Queued, work.Status);
    }

    [Fact]
    public void LocalGrantRejectsPushAndPullRequestButAllowsFetchAndCheckpoint()
    {
        RepositoryGrant grant = Target(10).Repository!.Grant!;
        foreach (string operation in new[] { "publish", "pull-request" })
            Assert.Throws<WorkRuleException>(() => grant.Authorize(1, 10, "owner/repo", "owner/repo", grant.Branch, operation));
        foreach (string operation in new[] { "fetch", "checkpoint" })
            grant.Authorize(1, 10, "owner/repo", "owner/repo", grant.Branch, operation);
        grant = Target(10, true).Repository!.Grant!;
        Assert.Throws<WorkRuleException>(() => grant.Authorize(1, 10, "owner/repo", "owner/repo", grant.Branch, "pull-request"));
    }
}
