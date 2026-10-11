using System;
using System.Text.Json;
using Goblin.Core.Work;
using Xunit;

namespace Goblin.Core.Tests;

public sealed class GitRepositoryAuthorizationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static ExecutionTarget Target(long id, bool push = false, bool pr = false) => new("codex", 1, gitRepository:
        new("owner/repo", "Goblin", "agent@example.com", new()
        {
            ConnectionId = 1,
            Generation = "generation",
            AccountId = "42",
            Login = "owner",
            GitRepositoryId = 22,
            BaseBranch = "develop",
            Branch = $"goblin/1/{id}",
            PolicyVersion = 2,
            AllowPush = push,
            AllowPullRequest = pr
        }));

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
        work.PrepareGitRepositoryAuthorization(10, Target(10), true, false, Now, "main");
        work = WorkItem.Restore(work.Snapshot());
        Assert.Empty(work.Attempts);
        Assert.True(work.GitRepositoryAuthorization!.EnableGitRepository);
        Assert.False(work.TryClaimExecution(10, 100, "sandbox", Now));
        Assert.Throws<WorkRuleException>(() => work.AuthorizeGitRepository(11, Target(10), Now));
        Assert.Throws<WorkRuleException>(() => work.AuthorizeGitRepository(10, Target(10, true), Now));
        work.AuthorizeGitRepository(10, Target(10), Now);
        Assert.True(work.TryClaimExecution(10, 100, "sandbox", Now));
        Assert.False(work.TryClaimExecution(10, 101, "sandbox", Now));
        Assert.Equal(GitRepositoryAuthorizationStatus.Authorized, work.GitRepositoryAuthorization.Status);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void RestoredUnsupportedPolicyCannotClaimEvenWithMatchingApproval(int policyVersion)
    {
        WorkItem work = Ready();
        work.QueueExecution(10, Target(10), Now);
        work.AuthorizeGitRepository(10, Target(10), Now);
        string state = JsonSerializer.Serialize(work.Snapshot())
            .Replace("\"PolicyVersion\":2", $"\"PolicyVersion\":{policyVersion}", StringComparison.Ordinal);
        work = WorkItem.Restore(JsonSerializer.Deserialize<WorkSnapshot>(state)!);
        Assert.False(work.TryClaimExecution(10, 100, "sandbox", Now));
        Assert.Equal(AttemptStatus.Queued, work.CurrentAttempt!.Status);
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
        if (action == "deny") work.DenyGitRepositoryAuthorization(10, Now);
        if (action == "cancel") work.RequestCancellation(Now);
        Assert.Throws<WorkRuleException>(() => work.AuthorizeGitRepository(10, Target(10), Now));
        Assert.Empty(work.Attempts);
    }

    [Fact]
    public void RetryNeedsFreshApprovalAndPreservesOriginalAttempt()
    {
        WorkItem work = Ready();
        work.QueueExecution(10, Target(10), Now);
        work.AuthorizeGitRepository(10, Target(10), Now);
        work.DispatchFailed(10, FailureKind.HostUnavailable, Now);
        work.RetryExecution(11, Target(11, true), Now);
        Assert.Single(work.Attempts);
        Assert.True(work.GitRepositoryAuthorization!.Retry);
        work.DenyGitRepositoryAuthorization(11, Now);
        work.PrepareGitRepositoryAuthorization(12, Target(12), false, true, Now);
        work.AuthorizeGitRepository(12, Target(12), Now);
        Assert.Equal(AttemptStatus.Failed, work.Attempts[0].Status);
        Assert.Equal(12, work.CurrentAttempt!.Id);
    }

    [Fact]
    public void ChangingScopeReleasesRetainedComputeBeforeAnotherAttemptCanQueue()
    {
        WorkItem work = Ready();
        work.QueueExecution(10, Target(10), Now);
        work.AuthorizeGitRepository(10, Target(10), Now);
        work.TryClaimExecution(10, 100, "sandbox", Now);
        work.PauseForInput(10, 100, 101, "Deliver changes?", false, Now);
        work.PrepareGitRepositoryAuthorization(11, Target(11, true), false, false, Now);
        Assert.True(work.CurrentAttempt!.CleanupPending);
        Assert.True(work.CurrentAttempt.ReleaseWorkspace);
        Assert.Throws<WorkRuleException>(() => work.AuthorizeGitRepository(11, Target(11, true), Now));
        work.ConfirmCleanup(10, 100, Now);
        work.AuthorizeGitRepository(11, Target(11, true), Now);
        Assert.Equal(AttemptStatus.Succeeded, work.Attempts[0].Status);
        Assert.Equal(WorkStatus.Queued, work.Status);
    }

    [Fact]
    public void LocalGrantRejectsPushAndPullRequestButAllowsFetchAndCheckpoint()
    {
        GitRepositoryGrant grant = Target(10).GitRepository!.Grant!;
        foreach (GitRepositoryOperationKind operation in new[] { GitRepositoryOperationKind.Publish, GitRepositoryOperationKind.PullRequest })
            Assert.Throws<WorkRuleException>(() => grant.Authorize(1, 10, "owner/repo", "owner/repo", grant.Branch, operation));
        foreach (string operation in new[] { "fetch", "checkpoint" })
            grant.Authorize(1, 10, "owner/repo", "owner/repo", grant.Branch, GitRepositoryOperationNames.Parse(operation));
        grant = Target(10, true).GitRepository!.Grant!;
        Assert.Throws<WorkRuleException>(() => grant.Authorize(1, 10, "owner/repo", "owner/repo", grant.Branch, GitRepositoryOperationKind.PullRequest));
    }
}
