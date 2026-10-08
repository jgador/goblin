using System;
using System.Collections.Generic;
using System.Text.Json;
using Goblin.Application.Work;
using Goblin.Contracts;
using Goblin.Core.Work;
using Goblin.Execution;
using Xunit;

namespace Goblin.Tests;

public sealed class GitRepositoryNamingTests
{
    public static IEnumerable<object[]> NamedValues() =>
    [
        [AttentionReason.GitRepositoryRequired, "RepositoryRequired"],
        [WorkEventKind.GitRepositoryRequested, "RepositoryRequested"],
        [WorkEventKind.GitRepositoryAuthorized, "RepositoryAuthorized"],
        [WorkEventKind.GitRepositoryDenied, "RepositoryDenied"],
        [WorkEventKind.GitRepositoryAuthorizationInvalidated, "RepositoryAuthorizationInvalidated"],
        [WorkAction.PrepareGitRepository, "PrepareRepository"],
        [WorkAction.AuthorizeGitRepository, "AuthorizeRepository"],
        [WorkAction.DenyGitRepository, "DenyRepository"],
        [IdentityKind.GitRepositoryOperation, "RepositoryOperation"]
    ];

    [Theory]
    [MemberData(nameof(NamedValues))]
    public void RenamedMembersKeepExistingWireValues(object value, string wire)
    {
        foreach (JsonSerializerOptions options in new[] { ContractJson.Options, ExecutionFiles.Json, GitRepositoryJson.CreateOptions() })
        {
            Type type = value.GetType();
            string json = JsonSerializer.Serialize(wire);
            Assert.Equal(json, JsonSerializer.Serialize(value, type, options));
            Assert.Equal(value, JsonSerializer.Deserialize(json, type, options));
            Assert.Equal(value, JsonSerializer.Deserialize(JsonSerializer.Serialize(wire.ToLowerInvariant()), type, options));
            Assert.Equal(value, JsonSerializer.Deserialize(JsonSerializer.Serialize(wire + ", " + wire), type, options));
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("999", type, options));
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("\"unknown\"", type, options));
        }
    }

    [Fact]
    public void WorkerSnapshotKeepsNestedGitRepositoryAuthorityAndSetupNames()
    {
        var work = new WorkItem(1, "Prepare a change", DateTimeOffset.UnixEpoch);
        work.Assign(2, DateTimeOffset.UnixEpoch);
        var change = new GitRepositoryChange("owner/repo", "Author", "author@example.test", new GitRepositoryGrant
        {
            ConnectionId = 3,
            Generation = "generation",
            AccountId = "account",
            Login = "owner",
            GitRepositoryId = 4,
            BaseBranch = "main",
            Branch = "goblin/1/5"
        });
        work.PrepareGitRepositoryAuthorization(5, new ExecutionTarget("codex", 6, gitRepository: change),
            true, false, DateTimeOffset.UnixEpoch);

        string json = JsonSerializer.Serialize(work.Snapshot(), ContractJson.Options);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement snapshot = document.RootElement;
        Assert.Equal("RepositoryRequired", snapshot.GetProperty("attention").GetProperty("reason").GetString());
        Assert.Equal("owner/repo", snapshot.GetProperty("repositoryRequest").GetProperty("repositories")[0].GetString());
        JsonElement approval = snapshot.GetProperty("repositoryAuthorization");
        Assert.True(approval.GetProperty("enableRepository").GetBoolean());
        JsonElement target = approval.GetProperty("target").GetProperty("repository");
        Assert.Equal("owner/repo", target.GetProperty("repository").GetString());
        Assert.Equal(4, target.GetProperty("grant").GetProperty("repositoryId").GetInt64());
        Assert.DoesNotContain("gitRepository", json, StringComparison.OrdinalIgnoreCase);

        WorkSnapshot restored = JsonSerializer.Deserialize<WorkSnapshot>(json, ExecutionFiles.Json)!;
        Assert.Equal(change, restored.GitRepositoryAuthorization!.Target.GitRepository);
        Assert.Equal(json, JsonSerializer.Serialize(restored, ExecutionFiles.Json));
    }
}
