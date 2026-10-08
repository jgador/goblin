using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Goblin.Application.Work;
using Goblin.Contracts;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;
using Goblin.Execution;
using Goblin.Integrations.Codex;
using Goblin.Integrations.GitHub;
using Goblin.Protocol;
using Goblin.Web;
using Microsoft.AspNetCore.Http;
using Xunit;
using Api = Goblin.Web.Http.Contracts;

namespace Goblin.Tests;

public sealed class TypedJsonBoundaryTests
{
    private static readonly JsonSerializerOptions DifferentPolicy = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseUpper
    };

    [Fact]
    public async Task BufferedRequestsBindDirectlyToTheExistingWorkContract()
    {
        DefaultHttpContext context = Request("""
            {"commandId":"9007199254740993","workId":"9007199254740995","action":"Create",
             "text":"Keep the objective","ignored":{"nested":true}}
            """);
        context.Items[ApiRequest.BodyKey] = await ApiRequest.ReadBodyAsync(context.Request);
        Api.WorkCommand command = ApiRequest.Body<Api.WorkCommand>(context, ContractJson.Options);
        Assert.Equal(9007199254740993, command.CommandId);
        Assert.Equal(9007199254740995, command.WorkId);
        Assert.Equal("Keep the objective", command.Text);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"x\":}")]
    [InlineData("{} {}")]
    [InlineData("{\"x\":1,}")]
    public async Task EveryPostStillRequiresOneValidJsonObject(string json)
    {
        PublicError error = await Assert.ThrowsAsync<PublicError>(() => ApiRequest.ReadBodyAsync(Request(json).Request));
        Assert.Equal("invalid_json", error.Code);
    }

    [Fact]
    public async Task BodyLimitAndContentTypeArePreserved()
    {
        Assert.Equal(8192, (await ApiRequest.ReadBodyAsync(Request("{}" + new string(' ', 8190)).Request)).Length);
        PublicError large = await Assert.ThrowsAsync<PublicError>(() => ApiRequest.ReadBodyAsync(Request("{}" + new string(' ', 8191)).Request));
        Assert.Equal(413, large.Status);
        DefaultHttpContext context = Request("{}");
        context.Request.ContentType = "text/plain";
        Assert.Equal(415, (await Assert.ThrowsAsync<PublicError>(() => ApiRequest.ReadBodyAsync(context.Request))).Status);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"nested\":\"value\"}")]
    public async Task NonStringFormFieldsStillBehaveAsMissing(string value)
    {
        DefaultHttpContext context = Request("{\"password\":" + value + "}");
        context.Items[ApiRequest.BodyKey] = await ApiRequest.ReadBodyAsync(context.Request);
        Assert.Null(ApiRequest.Body<UnlockRequest>(context).Password);
    }

    [Fact]
    public async Task SimpleFieldsKeepExactNamesAndTypedCommandsRejectWrongShapes()
    {
        DefaultHttpContext context = Request("{\"password\":\"secret\",\"Password\":\"ignored\"}");
        context.Items[ApiRequest.BodyKey] = await ApiRequest.ReadBodyAsync(context.Request);
        Assert.Equal("secret", ApiRequest.Body<UnlockRequest>(context, DifferentPolicy).Password);
        context = Request("{\"commandId\":[]}");
        context.Items[ApiRequest.BodyKey] = await ApiRequest.ReadBodyAsync(context.Request);
        Assert.Throws<JsonException>(() => ApiRequest.Body<Api.WorkCommand>(context, ContractJson.Options));
    }

    [Fact]
    public void GitHubProjectionsKeepIdentifiersAndIgnoreUnconsumedApiFields()
    {
        GitHubRepositoryResponse gitRepository = JsonSerializer.Deserialize<GitHubRepositoryResponse>("""
            {"id":9007199254740993,"full_name":"owner/repo","default_branch":"master",
             "permissions":{"push":true,"admin":false},"owner":{"anything":[1,2,3]}}
            """, DifferentPolicy)!;
        Assert.Equal(9007199254740993, gitRepository.Id);
        Assert.Equal("owner/repo", gitRepository.FullName);
        Assert.Equal("master", gitRepository.DefaultBranch);
        Assert.True(gitRepository.Permissions!.Push);
        Assert.Null(JsonSerializer.Deserialize<GitHubRepositoryResponse>("""
            {"id":1,"full_name":"owner/read-only","default_branch":"main"}
            """)!.Permissions);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<GitHubRepositoryResponse>("{\"id\":1}"));
        Assert.Equal("octocat", JsonSerializer.Deserialize<GitHubUserResponse>("{\"id\":1,\"login\":\"octocat\"}", DifferentPolicy)!.Login);
        Assert.Equal("https://github.com/owner/repo/pull/1", JsonSerializer.Deserialize<GitHubPullRequestResponse>(
            "{\"html_url\":\"https://github.com/owner/repo/pull/1\",\"number\":1}", DifferentPolicy)!.HtmlUrl);
    }

    [Fact]
    public void GitRepositoryPollingKeepsExactStateParsingAndNullableSuccessUrls()
    {
        GitRepositoryOperationResponse response = JsonSerializer.Deserialize<GitRepositoryOperationResponse>(
            "{\"state\":\"Succeeded\",\"url\":null}", DifferentPolicy)!;
        Assert.Equal(GitRepositoryOperationState.Succeeded, response.Status);
        Assert.Null(response.Url);
        foreach (string state in new[] { "succeeded", "Unknown", "1" })
        {
            response = JsonSerializer.Deserialize<GitRepositoryOperationResponse>("{\"state\":\"" + state + "\",\"url\":null}")!;
            Assert.Throws<InvalidOperationException>(() => response.Status);
        }
    }

    [Fact]
    public void InspectionContractsDoNotAddFieldsOrChangeNames()
    {
        var listing = new WorkspaceFilesResponse { Files = [new("repository/a.txt", 4)], Truncated = false };
        Assert.Equal("{\"files\":[{\"path\":\"repository/a.txt\",\"size\":4}],\"truncated\":false}", JsonSerializer.Serialize(listing, DifferentPolicy));
        var file = new WorkspaceFilesResponse { Path = "repository/a.txt", Text = "text" };
        Assert.Equal("{\"path\":\"repository/a.txt\",\"text\":\"text\"}", JsonSerializer.Serialize(file, DifferentPolicy));
        Assert.Equal("text", JsonSerializer.Deserialize<WorkspaceFilesResponse>(JsonSerializer.Serialize(file), DifferentPolicy)!.Text);
        Assert.Equal("{\"turnNumber\":2,\"commitSha\":\"commit\"}", JsonSerializer.Serialize(new WorkspaceCheckpointWrite(2, "commit"), DifferentPolicy));
    }

    [Fact]
    public void CodexResultsPreserveSetupPresenceAndComputeReleaseDefaults()
    {
        CodexGitRepositoryWorkResult result = Assert.IsType<CodexGitRepositoryWorkResult>(CodexWorkResults.Read(
            "{\"kind\":\"input\",\"text\":\"Choose a direction\"}", true));
        Assert.True(result.ReleaseWorkspace);
        Assert.Null(CodexWorkResults.Setup(result));
        result = Assert.IsType<CodexGitRepositoryWorkResult>(CodexWorkResults.Read(
            "{\"kind\":\"input\",\"text\":\"Continue here\",\"releaseWorkspace\":false,\"setup\":[]}", true));
        Assert.False(result.ReleaseWorkspace);
        Assert.Empty(CodexWorkResults.Setup(result)!);
        result = Assert.IsType<CodexGitRepositoryWorkResult>(CodexWorkResults.Read(
            "{\"kind\":\"result\",\"text\":\"Review this\",\"setup\":null}", true));
        Assert.Equal("invalid_work_result", Assert.Throws<IntegrationFailure>(() => CodexWorkResults.Setup(result)).Code);
        Assert.Equal("workspace", CodexWorkResults.Read("{\"kind\":\"workspace\",\"text\":\"Inspect repo\",\"setup\":123}", false).Kind);
        Assert.Throws<IntegrationFailure>(() => CodexWorkResults.Read("{\"kind\":\"workspace\",\"text\":\"Inspect repo\"}", true));
        Assert.Throws<IntegrationFailure>(() => CodexWorkResults.Read("{\"kind\":\"result\",\"text\":\" \"}", false));
    }

    [Fact]
    public void CodexSetupOutputMapsToCoreAndStillRejectsUnsafeObservations()
    {
        const string json = """
            {"kind":"result","text":"Ready for review","setup":[{"topic":"dotnet","reason":"Build the project",
             "tools":["dotnet 10.0.100"],"commands":[],"files":["global.json"],
             "checks":[{"command":"dotnet --version","expectedOutput":"10.0.100"}]}]}
            """;
        Goblin.Core.GitRepositories.GitRepositorySetup setup = Assert.Single(CodexWorkResults.Setup(CodexWorkResults.Read(json, true))!);
        Assert.Equal("dotnet", setup.Topic);
        Assert.Equal("10.0.100", Assert.Single(setup.Checks).ExpectedOutput);
        Assert.Throws<IntegrationFailure>(() => CodexWorkResults.Setup(CodexWorkResults.Read(json.Replace("global.json", "../outside"), true)));
        Assert.Throws<IntegrationFailure>(() => CodexWorkResults.Setup(CodexWorkResults.Read("{\"kind\":\"result\",\"text\":\"Done\",\"setup\":[null]}", true)));
    }

    [Fact]
    public void ProtocolEnumWireNamesComeFromExplicitSchemaAttributes()
    {
        foreach (PlanType plan in Enum.GetValues<PlanType>())
            Assert.Equal(JsonSerializer.SerializeToElement(plan, ProtocolJson.Options).GetString(), ProtocolStringEnumConverter<PlanType>.WireName(plan));
        Assert.Throws<JsonException>(() => ProtocolStringEnumConverter<PlanType>.WireName((PlanType)(-1)));
    }

    private static DefaultHttpContext Request(string json)
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json; charset=utf-8";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return context;
    }
}
