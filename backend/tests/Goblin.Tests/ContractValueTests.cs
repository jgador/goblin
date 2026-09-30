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
        Assert.Equal("\"chatgpt\"", JsonSerializer.Serialize(AuthenticationMethod.Chatgpt));
        Assert.Equal("\"Connected\"", JsonSerializer.Serialize(GitHubConnectionStatus.Connected));
        Assert.Equal("\"Changing\"", JsonSerializer.Serialize(ConnectionAvailability.Changing));
        Assert.Equal("\"Succeeded\"", JsonSerializer.Serialize(RepositoryOperationState.Succeeded));
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
        Assert.Equal(RepositoryOperationState.Uncertain, ContractValue.Parse<RepositoryOperationState>("Uncertain"));
        Assert.Equal(RepositoryOperationKind.PullRequest, RepositoryOperationNames.Parse("pull-request"));
        Assert.Equal("checkpoint", RepositoryOperationKind.Checkpoint.WireValue());
        foreach (string value in new[] { "merge", "Publish", "1", "pull_request", " publish" })
            Assert.False(RepositoryOperationNames.TryParse(value, out _));
        var grant = new RepositoryGrant(1, "generation", "42", "owner", 22, "main", "goblin/10/20", AllowPush: true, AllowPullRequest: true);
        Assert.Throws<WorkRuleException>(() => grant.Authorize(10, 20, "owner/repo", "owner/repo", grant.Branch, (RepositoryOperationKind)99));
    }
}
