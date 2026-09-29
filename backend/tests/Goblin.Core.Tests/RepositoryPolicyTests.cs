using Goblin.Core.Work;
using Xunit;

namespace Goblin.Core.Tests;

public sealed class RepositoryPolicyTests
{
    private static readonly RepositoryGrant Grant = new(1, "generation", "42", "owner", 22, "main", "goblin/10/20", AllowPush: true, AllowPullRequest: true);
    [Theory]
    [InlineData(RepositoryOperationKind.Publish)]
    [InlineData(RepositoryOperationKind.PullRequest)]
    [InlineData(RepositoryOperationKind.Fetch)]
    public void AssignedBranchCanBePublished(RepositoryOperationKind operation) => Grant.Authorize(10, 20, "owner/repo", "owner/repo", "goblin/10/20", operation);
    [Theory]
    [InlineData("owner/repo", "main", "publish")]
    [InlineData("owner/repo", "goblin/10/21", "publish")]
    [InlineData("owner/other", "goblin/10/20", "publish")]
    [InlineData("owner/repo", "goblin/10/20", "merge")]
    [InlineData("owner/repo", "goblin/10/20", "auto-merge")]
    [InlineData("owner/repo", "goblin/10/20", "delete")]
    public void OtherDestinationsAndOperationsAreRejected(string repository, string branch, string operation) =>
        Assert.Throws<WorkRuleException>(() => Grant.Authorize(10, 20, "owner/repo", repository, branch, RepositoryOperationNames.Parse(operation)));
    [Fact]
    public void AnotherAttemptCannotReuseTheGrant() =>
        Assert.Throws<WorkRuleException>(() => Grant.Authorize(10, 21, "owner/repo", "owner/repo", Grant.Branch, RepositoryOperationKind.Publish));
}
