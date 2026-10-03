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
