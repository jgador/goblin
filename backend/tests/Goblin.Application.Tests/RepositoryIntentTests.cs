using System;
using Goblin.Application.Work;
using Goblin.Core.Work;
using Xunit;

namespace Goblin.Application.Tests;

public sealed class RepositoryIntentTests
{
    [Theory]
    [InlineData("Fix the repository", false, false, null)]
    [InlineData("Create a branch and push the changes", true, false, null)]
    [InlineData("Open a PR from base branch develop", true, true, "develop")]
    [InlineData("Use release/next as the base and open a draft pull request", true, true, "release/next")]
    [InlineData("Don't push or open a PR", false, false, null)]
    [InlineData("Do not publish the changes", false, false, null)]
    public void DeliveryComesFromUserIntent(string text, bool push, bool pr, string? branch) =>
        Assert.Equal(new GitDeliveryIntent(branch, push, pr), RepositoryIntent.Parse(text));

    [Theory]
    [InlineData("Do not push, but open a PR")]
    [InlineData("Use base branch main and base branch develop")]
    public void ContradictionsNeedClarification(string text) =>
        Assert.Throws<ApplicationFailure>(() => RepositoryIntent.Parse(text));

    [Fact]
    public void ShortNamesKeepAllCandidatesIncludingDisabledRepositories()
    {
        var work = new WorkItem(1, "Fix goblin", DateTimeOffset.UtcNow);
        Assert.Equal(new[] { "owner/goblin" }, RepositoryReferences.Find(work.Snapshot(), ["owner/goblin"]));
        Assert.Equal(new[] { "other/goblin", "owner/goblin" }, RepositoryReferences.Find(work.Snapshot(), ["owner/goblin", "other/goblin"]));
    }

    [Fact]
    public void BaseBranchNamesAreNotMistakenForRepositories()
    {
        var work = new WorkItem(1, "Fix owner/repo and use release/next as the base", DateTimeOffset.UtcNow);
        Assert.Equal(new[] { "owner/repo" }, RepositoryReferences.Find(work.Snapshot(), ["owner/repo", "someone/next"]));
    }

    [Fact]
    public void LaterUserCorrectionOverridesEarlierDelivery()
    {
        var work = new WorkItem(1, "Open a PR using base branch main", DateTimeOffset.UtcNow);
        work.AddContext(2, "Actually don't push. Use develop as base", DateTimeOffset.UtcNow);
        Assert.Equal(new GitDeliveryIntent("develop"), RepositoryIntent.Delivery(work.Snapshot()));
    }
}
