using System;
using Goblin.Application.Work;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;
using Xunit;

namespace Goblin.Application.Tests;

public sealed class GitRepositoryIntentTests
{
    [Fact]
    public void DefaultCommitIdentityUsesConnectedAccountWithoutReadingPrivateEmail()
    {
        GitRepositoryChange repository = WorkStore.RequestedGitRepository("owner/repo", new("generation", "42", "connected-user"));
        Assert.Equal("connected-user", repository.GitAuthorName);
        Assert.Equal("42+connected-user@users.noreply.github.com", repository.GitAuthorEmail);
        repository = WorkStore.RequestedGitRepository("owner/repo", new("generation", "42", "connected-user"), new("owner/repo", "Reviewer", "reviewer@example.test"));
        Assert.Equal("Reviewer", repository.GitAuthorName);
        Assert.Equal("reviewer@example.test", repository.GitAuthorEmail);
    }
    [Theory]
    [InlineData("Fix the repository", false, false, null)]
    [InlineData("Fix push notifications in owner/repo", false, false, null)]
    [InlineData("Explain how to push changes and open a PR", false, false, null)]
    [InlineData("Should we push changes and open a PR?", false, false, null)]
    [InlineData("Investigate whether we should push changes", false, false, null)]
    [InlineData("Can you push the changes?", true, false, null)]
    [InlineData("Please open a draft PR", true, true, null)]
    [InlineData("Fix owner/repo, then push.", true, false, null)]
    [InlineData("Push owner/repo", true, false, null)]
    [InlineData("Publish repository", true, false, null)]
    [InlineData("Create a branch and push the changes", true, false, null)]
    [InlineData("Open a PR from base branch develop", true, true, "develop")]
    [InlineData("Use release/next as the base and open a draft pull request", true, true, "release/next")]
    [InlineData("Don't push or open a PR", false, false, null)]
    [InlineData("Do not publish the changes", false, false, null)]
    public void DeliveryComesFromUserIntent(string text, bool push, bool pr, string? branch) =>
        Assert.Equal(new GitDeliveryIntent(branch, push, pr), GitRepositoryIntent.Parse(text));

    [Theory]
    [InlineData("Do not push, but open a PR")]
    [InlineData("Use base branch main and base branch develop")]
    public void ContradictionsNeedClarification(string text) =>
        Assert.Throws<ApplicationFailure>(() => GitRepositoryIntent.Parse(text));

    [Fact]
    public void ShortNamesKeepAllCandidatesIncludingDisabledGitRepositories()
    {
        var work = new WorkItem(1, "Fix goblin", DateTimeOffset.UtcNow);
        Assert.Equal(new[] { "owner/goblin" }, GitRepositoryReferences.Find(work.Snapshot(), ["owner/goblin"]));
        Assert.Equal(new[] { "other/goblin", "owner/goblin" }, GitRepositoryReferences.Find(work.Snapshot(), ["owner/goblin", "other/goblin"]));
    }

    [Fact]
    public void BaseBranchNamesAreNotMistakenForGitRepositories()
    {
        var work = new WorkItem(1, "Fix owner/repo and use release/next as the base", DateTimeOffset.UtcNow);
        Assert.Equal(new[] { "owner/repo" }, GitRepositoryReferences.Find(work.Snapshot(), ["owner/repo", "someone/next"]));
    }

    [Fact]
    public void LaterUserCorrectionOverridesEarlierDelivery()
    {
        var work = new WorkItem(1, "Open a PR using base branch main", DateTimeOffset.UtcNow);
        work.AddContext(2, "Actually don't push. Use develop as base", DateTimeOffset.UtcNow);
        Assert.Equal(new GitDeliveryIntent("develop"), GitRepositoryIntent.Delivery(work.Snapshot()));
    }
}
