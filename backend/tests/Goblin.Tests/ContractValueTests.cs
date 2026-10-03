using System;
using System.Text.Json;
using Goblin.Application.Work;
using Goblin.Contracts;
using Goblin.Core.Work;
using Xunit;

namespace Goblin.Tests;

public sealed class ContractValueTests
{
    [Fact]
    public void ClosedContractsRetainTheirExistingWireValues()
    {
        Assert.Equal("\"error\"", JsonSerializer.Serialize(NoticeKind.Error));
        Assert.Equal("\"info\"", JsonSerializer.Serialize(NoticeKind.Info));
        Assert.Equal("\"accepted\"", JsonSerializer.Serialize(VerificationState.Accepted));
        Assert.Equal("\"unverified\"", JsonSerializer.Serialize(VerificationState.Unverified));
        Assert.Equal("\"apiKey\"", JsonSerializer.Serialize(AuthenticationMethod.ApiKey));
        Assert.Equal("\"chatgpt\"", JsonSerializer.Serialize(AuthenticationMethod.ChatGPT));
        Assert.Equal("\"Connected\"", JsonSerializer.Serialize(GitHubConnectionStatus.Connected));
        Assert.Equal("\"Changing\"", JsonSerializer.Serialize(ConnectionAvailability.Changing));
        Assert.Equal("\"Succeeded\"", JsonSerializer.Serialize(GitRepositoryOperationState.Succeeded));
    }

    [Theory]
    [InlineData("\"unsupported\"")]
    [InlineData("1")]
    [InlineData("99")]
    [InlineData("\"1\"")]
    public void ClosedJsonContractsRejectUnknownAndNumericValues(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<VerificationState>(json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<GitHubConnectionStatus>(json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<WorkAction>(json, WorkStore.Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<WorkStatus>(json, WorkStore.Json));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("available")]
    [InlineData("0")]
    [InlineData(" Available")]
    public void StoredStatesMustUseAnExactNamedValue(string value) =>
        Assert.Throws<InvalidOperationException>(() => ContractValue.Parse<ConnectionAvailability>(value));

    [Fact]
    public void StoredStatesRoundTripAndUnrecognizedOperationsCannotGainAuthority()
    {
        Assert.Equal(InspectionState.Available, ContractValue.Parse<InspectionState>("Available"));
        Assert.Equal(GitRepositoryOperationState.Uncertain, ContractValue.Parse<GitRepositoryOperationState>("Uncertain"));
        Assert.Equal(GitRepositoryOperationKind.PullRequest, GitRepositoryOperationNames.Parse("pull-request"));
        Assert.Equal("checkpoint", GitRepositoryOperationKind.Checkpoint.WireValue());
        foreach (string value in new[] { "merge", "Publish", "1", "pull_request", " publish" })
            Assert.False(GitRepositoryOperationNames.TryParse(value, out _));
        var grant = new GitRepositoryGrant()
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
        Assert.Throws<WorkRuleException>(() => grant.Authorize(10, 20, "owner/repo", "owner/repo", grant.Branch, (GitRepositoryOperationKind)99));
    }
}
