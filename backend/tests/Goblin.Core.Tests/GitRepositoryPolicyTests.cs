using System;
using System.Text.Json;
using Goblin.Core.Work;
using Xunit;

namespace Goblin.Core.Tests;

public sealed class GitRepositoryPolicyTests
{
    private static readonly GitRepositoryGrant Grant = new()
    {
        ConnectionId = 1,
        Generation = "generation",
        AccountId = "42",
        Login = "owner",
        GitRepositoryId = 22,
        BaseBranch = "main",
        Branch = "goblin/10/20",
        AllowPush = true,
        AllowPullRequest = true
    };

    [Theory]
    [InlineData(GitRepositoryOperationKind.Publish)]
    [InlineData(GitRepositoryOperationKind.PullRequest)]
    [InlineData(GitRepositoryOperationKind.Fetch)]
    public void AssignedBranchCanBePublished(GitRepositoryOperationKind operation) => Grant.Authorize(10, 20, "owner/repo", "owner/repo", "goblin/10/20", operation);

    [Theory]
    [InlineData("owner/repo", "main", "publish")]
    [InlineData("owner/repo", "goblin/10/21", "publish")]
    [InlineData("owner/other", "goblin/10/20", "publish")]
    [InlineData("owner/repo", "goblin/10/20", "merge")]
    [InlineData("owner/repo", "goblin/10/20", "auto-merge")]
    [InlineData("owner/repo", "goblin/10/20", "delete")]
    public void OtherDestinationsAndOperationsAreRejected(string gitRepository, string branch, string operation) =>
        Assert.Throws<WorkRuleException>(() => Grant.Authorize(10, 20, "owner/repo", gitRepository, branch, GitRepositoryOperationNames.Parse(operation)));

    [Fact]
    public void AnotherAttemptCannotReuseTheGrant() =>
        Assert.Throws<WorkRuleException>(() => Grant.Authorize(10, 21, "owner/repo", "owner/repo", Grant.Branch, GitRepositoryOperationKind.Publish));

    [Theory]
    [InlineData("slack:T123/U456\nCo-authored-by: Other <other@example.test>")]
    [InlineData("slack:T123/U456\rForged")]
    [InlineData("")]
    public void RequesterCannotInjectCommitTrailers(string requester) =>
        Assert.Throws<WorkRuleException>(() => new GitRepositoryChange("owner/repo", "Author", "author@example.test", Grant) { RequestedBy = requester });

    [Fact]
    public void RequesterSurvivesSerializationWithoutChangingRepositoryAuthority()
    {
        var original = new GitRepositoryChange("owner/repo", "Author", "author@example.test", Grant) { RequestedBy = "slack:T123/U456" };
        GitRepositoryChange restored = JsonSerializer.Deserialize<GitRepositoryChange>(JsonSerializer.Serialize(original))!;
        Assert.Equal(original, restored);
        Assert.Equal("slack:T123/U456", restored.RequestedBy);
        Assert.Equal(Grant, restored.Grant);
        Assert.Null(JsonSerializer.Deserialize<GitRepositoryChange>("""{"GitRepository":"owner/repo","GitAuthorName":"Author","GitAuthorEmail":"author@example.test"}""")!.RequestedBy);
    }

    [Fact]
    public void GrantOwnsVersionedApprovalPublicationAndOperationPolicy()
    {
        GitRepositoryGrant local = Grant with { AllowPush = false, AllowPullRequest = false };
        Assert.True(local.HasSupportedPolicy());
        Assert.False(local.PublishesChanges());
        Assert.True(local.AllowsOperation(GitRepositoryOperationKind.Fetch));
        Assert.True(local.AllowsOperation(GitRepositoryOperationKind.Checkpoint));
        Assert.False(local.AllowsOperation(GitRepositoryOperationKind.Publish));
        Assert.False(local.AllowsOperation(GitRepositoryOperationKind.PullRequest));

        GitRepositoryGrant push = local with { AllowPush = true };
        Assert.True(push.PublishesChanges());
        Assert.True(push.AllowsOperation(GitRepositoryOperationKind.Publish));
        Assert.False(push.AllowsOperation(GitRepositoryOperationKind.PullRequest));
        Assert.True((push with { AllowPullRequest = true }).AllowsOperation(GitRepositoryOperationKind.PullRequest));

        foreach (int version in new[] { 1, 3 })
        {
            GitRepositoryGrant unsupported = Grant with { PolicyVersion = version };
            Assert.False(unsupported.HasSupportedPolicy());
            Assert.False(unsupported.PublishesChanges());
            Assert.All(Enum.GetValues<GitRepositoryOperationKind>(), operation => Assert.False(unsupported.AllowsOperation(operation)));
        }
        Assert.False(Grant.AllowsOperation((GitRepositoryOperationKind)99));
    }

    [Fact]
    public void RestoredAuthorityHasValueEqualityAndEveryGrantFieldParticipates()
    {
        var target = new ExecutionTarget("codex", 1, gitRepository: new("owner/repo", "Author", "author@example.test", Grant));
        ExecutionTarget restored = JsonSerializer.Deserialize<ExecutionTarget>(JsonSerializer.Serialize(target))!;
        Assert.Equal(target, restored);
        Assert.Equal(target.GetHashCode(), restored.GetHashCode());
        GitRepositoryGrant[] changed =
        [
            Grant with { ConnectionId = 2 },
            Grant with { Generation = "next-generation" },
            Grant with { AccountId = "another-account" },
            Grant with { Login = "another-owner" },
            Grant with { GitRepositoryId = 23 },
            Grant with { BaseBranch = "develop" },
            Grant with { Branch = "goblin/10/21" },
            Grant with { PolicyVersion = 1 },
            Grant with { AllowPush = false },
            Grant with { AllowPullRequest = false }
        ];
        foreach (GitRepositoryGrant grant in changed)
        {
            Assert.NotEqual(Grant, grant);
            Assert.NotEqual(target, new ExecutionTarget("codex", 1,
                gitRepository: new("owner/repo", "Author", "author@example.test", grant)));
        }
        Assert.Equal(Grant, restored.GitRepository!.Grant);
    }
}
