using System;
using Goblin.Application.Work;
using Goblin.Core.Work;
using Xunit;

namespace Goblin.Application.Tests;

public sealed class RepositoryReferencesTests
{
    [Theory]
    [InlineData("https://github.com/owner/repo convert Python to Rust", "owner/repo")]
    [InlineData("Inspect https://github.com/owner/repo.git.", "owner/repo")]
    [InlineData("Use owner/repo to fix it", "owner/repo")]
    [InlineData("Inspect https://github.com/owner/repo/issues/9", "owner/repo")]
    [InlineData("Explain compaction", null)]
    [InlineData("Use https://evil.github.com/owner/repo", null)]
    [InlineData("Use https://example.com/owner/repo", null)]
    [InlineData("Use https://github.com.evil/owner/repo", null)]
    public void OnlyExplicitReferencesSuggestRepositories(string objective, string? expected)
    {
        var work = new WorkItem(1, objective, DateTimeOffset.UtcNow);
        string[] matches = RepositoryReferences.Find(work.Snapshot(), ["owner/repo"]);
        Assert.Equal(expected is null ? [] : [expected], matches);
    }

    [Fact]
    public void AmbiguousReferencesAndFollowupAnswersAreRetainedForSelection()
    {
        var work = new WorkItem(1, "Compare owner/repo and https://github.com/other/repo", DateTimeOffset.UtcNow);
        Assert.Equal(new[] { "other/repo", "owner/repo" }, RepositoryReferences.Find(work.Snapshot(), ["owner/repo"], "Clone OWNER/REPO"));
    }
}
