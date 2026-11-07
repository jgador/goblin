using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.Work;
using Goblin.Contracts;
using Goblin.Core.Work;
using Goblin.Integrations.ChatGpt;
using Goblin.Persistence;
using Goblin.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Goblin.Tests;

public sealed class ChatGptHttpTests
{
    [Fact]
    public void WorkSectionsAndEveryResultRemainReadableBeyondOneMcpPage()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string longText = new string('r', 5999) + "😀" + new string('q', 6100);
        var view = new WorkView(4, now, now, new WorkSnapshot
        {
            Id = 11,
            Objective = "Full objective",
            Attempts = [],
            History = [],
            Decisions = [],
            Messages = [],
            Artifacts = [],
            Results = [new WorkResult(21, longText, now), new WorkResult(22, "Approved text", now, now)]
        });
        var section = new StringBuilder();
        int offset = 0;
        do
        {
            ChatGptWorkSectionPage page = ChatGptWorkProjection.Section(view, ChatGptWorkSection.Results, offset);
            section.Append(page.ContentJson);
            if (page.NextOffset is not int next) break;
            offset = next;
        } while (true);
        using JsonDocument results = JsonDocument.Parse(section.ToString());
        Assert.Equal(2, results.RootElement.GetArrayLength());
        Assert.Equal("21", results.RootElement[0].GetProperty("attemptId").GetString());
        Assert.Equal(longText, results.RootElement[0].GetProperty("text").GetString());

        foreach (long attemptId in new long[] { 21, 22 })
        {
            var text = new StringBuilder();
            offset = 0;
            do
            {
                ChatGptWorkResultPage page = ChatGptWorkProjection.Result(view, attemptId, offset);
                text.Append(page.Text);
                if (page.NextOffset is not int next) break;
                offset = next;
            } while (true);
            Assert.Equal(attemptId == 21 ? longText : "Approved text", text.ToString());
        }
    }

    [Fact]
    public async Task DisabledSetupSuppliesConfigurationNamesWithoutADocumentDownload()
    {
        await using Host host = await Host.CreateAsync(configured: false);
        using HttpClient http = host.Client();
        http.DefaultRequestHeaders.Add("Cookie", host.OwnerCookie);
        using HttpResponseMessage response = await http.GetAsync("/api/integrations/chatgpt");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(document.RootElement.GetProperty("configured").GetBoolean());
        JsonElement names = document.RootElement.GetProperty("environmentVariables");
        Assert.Equal("GOBLIN_PUBLIC_ORIGIN", names.GetProperty("publicOrigin").GetString());
        Assert.Equal("GOBLIN_CHATGPT_ENABLED", names.GetProperty("enabled").GetString());
        using HttpResponseMessage guide = await http.GetAsync("/api/integrations/chatgpt/guide");
        Assert.Equal(HttpStatusCode.NotFound, guide.StatusCode);
    }

    [Fact]
    public async Task MetadataIsPublicButMcpRequiresBearerAndOwnerCookieCannotSubstitute()
    {
        await using Host host = await Host.CreateAsync();
        using HttpClient http = host.Client();
        using HttpResponseMessage metadata = await http.GetAsync(ChatGptConfiguration.MetadataPath);
        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await metadata.Content.ReadAsStringAsync());
        Assert.Equal(ChatGptAuthenticationTests.Origin + "/mcp", document.RootElement.GetProperty("resource").GetString());
        Assert.Equal("no-store", metadata.Headers.CacheControl?.ToString());
        http.DefaultRequestHeaders.Add("Cookie", host.OwnerCookie);
        using HttpResponseMessage response = await http.PostAsync("/mcp", Json("{}"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("resource_metadata=", response.Headers.WwwAuthenticate.ToString());
        Assert.Equal(0, host.Tokens.Reads);
    }

    [Theory]
    [InlineData("foreign-host")]
    [InlineData("foreign-origin")]
    [InlineData("duplicate-authorization")]
    [InlineData("internal-port")]
    public async Task InvalidTransportBoundaryCannotReachTools(string invalid)
    {
        await using Host host = await Host.CreateAsync(internalPort: invalid == "internal-port");
        using HttpClient http = host.Client();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = Json("{}") };
        if (invalid == "foreign-host") request.Headers.Host = "other.example.test";
        if (invalid == "foreign-origin") request.Headers.Add("Origin", "https://other.example.test");
        request.Headers.Authorization = new("Bearer", host.Tokens.Token());
        if (invalid == "duplicate-authorization") request.Headers.TryAddWithoutValidation("Authorization", "Bearer second");
        using HttpResponseMessage response = await http.SendAsync(request);
        Assert.Equal(invalid == "duplicate-authorization" ? HttpStatusCode.Unauthorized : invalid == "internal-port" ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, host.Tokens.Reads);
    }

    [Theory]
    [InlineData("/api/integrations/chatgpt/client/revoke")]
    public async Task BearerTokenCannotAdministerSetupAndOwnerPostStillRequiresOrigin(string path)
    {
        await using Host host = await Host.CreateAsync();
        using HttpClient http = host.Client();
        http.DefaultRequestHeaders.Authorization = new("Bearer", host.Tokens.Token());
        http.DefaultRequestHeaders.Add("Origin", ChatGptAuthenticationTests.Origin);
        using HttpResponseMessage bearer = await http.PostAsync(path, Json("{\"id\":\"1\"}"));
        Assert.Equal(HttpStatusCode.Unauthorized, bearer.StatusCode);
        http.DefaultRequestHeaders.Authorization = null;
        http.DefaultRequestHeaders.Add("Cookie", host.OwnerCookie);
        http.DefaultRequestHeaders.Remove("Origin");
        using HttpResponseMessage cookie = await http.PostAsync(path, Json("{\"id\":\"1\"}"));
        Assert.Equal(HttpStatusCode.Forbidden, cookie.StatusCode);
    }

    [Theory]
    [InlineData("/api/integrations/chatgpt/creator/check")]
    [InlineData("/api/integrations/chatgpt/creator/prepare")]
    [InlineData("/api/integrations/chatgpt/package")]
    public async Task RemovedSetupEndpointsAreUnavailableToTheOwner(string path)
    {
        await using Host host = await Host.CreateAsync();
        using HttpClient http = host.Client();
        http.DefaultRequestHeaders.Add("Cookie", host.OwnerCookie);
        http.DefaultRequestHeaders.Add("Origin", ChatGptAuthenticationTests.Origin);
        using HttpResponseMessage response = await http.PostAsync(path, Json("{}"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, host.Tokens.Reads);
    }

    [Fact]
    public async Task RealSdkDiscoversFullWorkToolsAndDoesNotReuseAuthenticationBetweenRequests()
    {
        await using Host host = await Host.CreateAsync();
        using HttpClient http = host.Client();
        http.DefaultRequestHeaders.Authorization = new("Bearer", host.Tokens.Token());
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(ChatGptAuthenticationTests.Origin + "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            OAuth = null,
            MaxReconnectionAttempts = 0,
            OwnsSession = false
        }, http, ownsHttpClient: false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using McpClient client = await McpClient.CreateAsync(transport, cancellationToken: deadline.Token);
        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: deadline.Token);
        var actions = new Dictionary<WorkAction, string>
        {
            [WorkAction.Create] = "goblin_create_work",
            [WorkAction.Assign] = "goblin_assign_work",
            [WorkAction.AddContext] = "goblin_add_work_context",
            [WorkAction.Execute] = "goblin_execute_work",
            [WorkAction.Answer] = "goblin_answer_work_question",
            [WorkAction.RequestChanges] = "goblin_request_work_changes",
            [WorkAction.Approve] = "goblin_approve_work_result",
            [WorkAction.Cancel] = "goblin_cancel_work",
            [WorkAction.Reconcile] = "goblin_reconcile_work",
            [WorkAction.Retry] = "goblin_retry_work",
            [WorkAction.PrepareGitRepository] = "goblin_prepare_work_repository",
            [WorkAction.AuthorizeGitRepository] = "goblin_authorize_work_repository",
            [WorkAction.DenyGitRepository] = "goblin_deny_work_repository"
        };
        Assert.Equal(Enum.GetValues<WorkAction>().Order(), actions.Keys.Order());
        Assert.Equal(actions.Values.Concat(new[] { "goblin_get_work", "goblin_get_work_result", "goblin_get_work_section",
            "goblin_list_agents", "goblin_list_repositories", "goblin_list_available_repositories", "goblin_list_work", "goblin_ping", "goblin_reserve_work_ids",
            "goblin_suggest_work_repositories" }).Order(), tools.Select(t => t.Name).Order());
        foreach (McpClientTool tool in tools)
        {
            bool writes = actions.ContainsValue(tool.Name) || tool.Name == "goblin_reserve_work_ids";
            Assert.Equal(!writes, tool.ProtocolTool.Annotations?.ReadOnlyHint);
            bool destructive = tool.Name is "goblin_add_work_context" or "goblin_execute_work" or "goblin_answer_work_question"
                or "goblin_cancel_work" or "goblin_reconcile_work" or "goblin_retry_work" or "goblin_prepare_work_repository"
                or "goblin_authorize_work_repository" or "goblin_deny_work_repository";
            Assert.Equal(destructive, tool.ProtocolTool.Annotations?.DestructiveHint);
            Assert.Equal(destructive && tool.Name != "goblin_deny_work_repository" ||
                tool.Name == "goblin_list_available_repositories", tool.ProtocolTool.Annotations?.OpenWorldHint);
            Assert.Equal(tool.Name != "goblin_ping" && tool.Name != "goblin_reserve_work_ids", tool.ProtocolTool.Annotations?.IdempotentHint);
            Assert.NotNull(tool.ProtocolTool.OutputSchema);
            Assert.NotNull(tool.ProtocolTool.Meta?["securitySchemes"]);
            Assert.Contains(ChatGptConfiguration.WriteScope, tool.ProtocolTool.Meta!["securitySchemes"]!.ToJsonString());
        }
        foreach (string name in actions.Values)
        {
            using JsonDocument schema = JsonDocument.Parse(JsonSerializer.Serialize(tools.Single(t => t.Name == name).ProtocolTool.InputSchema));
            JsonElement properties = schema.RootElement.GetProperty("properties");
            Assert.Equal("string", properties.GetProperty("commandId").GetProperty("type").GetString());
            Assert.Equal("string", properties.GetProperty("workId").GetProperty("type").GetString());
            if (name == "goblin_create_work")
                Assert.Equal("string", properties.GetProperty("objective").GetProperty("type").GetString());
            else
                Assert.Equal("integer", properties.GetProperty("expectedVersion").GetProperty("type").GetString());
        }
        using (JsonDocument sectionSchema = JsonDocument.Parse(JsonSerializer.Serialize(
            tools.Single(t => t.Name == "goblin_get_work_section").ProtocolTool.InputSchema)))
            Assert.Equal("string", sectionSchema.RootElement.GetProperty("properties").GetProperty("section").GetProperty("type").GetString());
        // This host has an unreachable database and no GitHub connection. Ping
        // must confirm the transport without reading Work.
        DateTimeOffset beforePing = DateTimeOffset.UtcNow;
        CallToolResult ping = await client.CallToolAsync("goblin_ping", cancellationToken: deadline.Token);
        Assert.False(ping.IsError ?? false);
        JsonElement result = ping.StructuredContent!.Value;
        using JsonDocument textResult = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(ping.Content)).Text);
        Assert.True(JsonElement.DeepEquals(result, textResult.RootElement));
        Assert.Equal(new[] { "message", "operatingSystem", "processArchitecture", "respondedAtUtc", "responseId", "runtime" }, result.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("Goblin MCP connection is working.", result.GetProperty("message").GetString());
        string responseId = result.GetProperty("responseId").GetString()!;
        Assert.True(Guid.TryParseExact(responseId, "N", out _));
        Assert.InRange(result.GetProperty("respondedAtUtc").GetDateTimeOffset(), beforePing, DateTimeOffset.UtcNow);
        Assert.Equal(TimeSpan.Zero, result.GetProperty("respondedAtUtc").GetDateTimeOffset().Offset);
        Assert.Contains(result.GetProperty("operatingSystem").GetString(), new[] { "Linux", "Windows", "macOS", "Other" });
        Assert.Equal(RuntimeInformation.FrameworkDescription, result.GetProperty("runtime").GetString());
        Assert.Equal(RuntimeInformation.ProcessArchitecture.ToString(), result.GetProperty("processArchitecture").GetString());
        Assert.Contains(responseId, Assert.Single(host.PingLog.Messages));
        CallToolResult nextPing = await client.CallToolAsync("goblin_ping", cancellationToken: deadline.Token);
        JsonElement nextResult = nextPing.StructuredContent!.Value;
        Assert.NotEqual(responseId, nextResult.GetProperty("responseId").GetString());
        Assert.True(nextResult.GetProperty("respondedAtUtc").GetDateTimeOffset() >= result.GetProperty("respondedAtUtc").GetDateTimeOffset());
        Assert.Equal(result.GetProperty("operatingSystem").GetString(), nextResult.GetProperty("operatingSystem").GetString());
        Assert.Equal(2, host.PingLog.Messages.Count);
        Assert.Contains(host.PingLog.Messages, message => message.Contains(nextResult.GetProperty("responseId").GetString()!, StringComparison.Ordinal));
        CallToolResult invalid = await client.CallToolAsync("goblin_get_work", new Dictionary<string, object?> { ["workId"] = "1e3" }, cancellationToken: deadline.Token);
        Assert.True(invalid.IsError);
        Assert.DoesNotContain("mcp/www_authenticate", JsonSerializer.Serialize(invalid));
        http.DefaultRequestHeaders.Authorization = new("Bearer", host.Tokens.Token("expiry"));
        HttpRequestException failure = await Assert.ThrowsAsync<HttpRequestException>(async () => await client.CallToolAsync("goblin_ping", cancellationToken: deadline.Token));
        Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
        Assert.Equal(2, host.PingLog.Messages.Count);
    }

    [Fact]
    public async Task ChunkedOversizedMcpBodyIsRejectedBeforeSdkDispatch()
    {
        await using Host host = await Host.CreateAsync();
        using HttpClient http = host.Client();
        http.DefaultRequestHeaders.Authorization = new("Bearer", host.Tokens.Token());
        using StringContent content = Json("{\"padding\":\"" + new string('a', 8192) + "\"}");
        using HttpResponseMessage response = await http.PostAsync("/mcp", content);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");

    private sealed class FixtureAuthentication : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity("fixture")), scheme!)));
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
    }

    private sealed class PingLogger : ILogger<ChatGptTools>
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Enqueue(formatter(state, exception));
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly string _directory;
        public ChatGptAuthenticationTests.Tokens Tokens { get; }
        public string OwnerCookie { get; }
        public PingLogger PingLog { get; }

        private Host(WebApplication app, string directory, ChatGptAuthenticationTests.Tokens tokens, string cookie, PingLogger pingLog)
        { _app = app; _directory = directory; Tokens = tokens; OwnerCookie = cookie; PingLog = pingLog; }

        public static async Task<Host> CreateAsync(bool internalPort = false, bool configured = true)
        {
            string? root = Environment.CurrentDirectory;
            while (root is not null && !File.Exists(Path.Combine(root, "AGENTS.md"))) root = Path.GetDirectoryName(root);
            string directory = Path.Combine(root!, ".artifacts", "chatgpt-plugin", "http-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            byte[] digest = Rfc2898DeriveBytes.Pbkdf2("fixture-password", salt, 600000, HashAlgorithmName.SHA256, 32);
            string passwordFile = Path.Combine(directory, "owner-password");
            await File.WriteAllTextAsync(passwordFile, "pbkdf2-sha256$600000$" + Convert.ToBase64String(salt) + "$" + Convert.ToBase64String(digest));
            Workspace workspace = await Workspace.OpenAsync(directory, ChatGptAuthenticationTests.Origin, passwordFile);
            var session = new DefaultHttpContext();
            workspace.Unlock("fixture-password", session.Response);
            string cookie = session.Response.Headers.SetCookie.ToString().Split(';')[0];
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            var pingLog = new PingLogger();
            builder.Services.AddSingleton<ILogger<ChatGptTools>>(pingLog);
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(workspace);
            builder.Services.AddGoblinPersistence("Host=127.0.0.1;Port=1;Database=unavailable;Username=unused;Timeout=1");
            ChatGptMcp.Configure(builder.Services, new ApplicationOptions
            {
                EnableWork = true,
                EnableChatGpt = configured,
                DataDirectory = directory,
                PublicOrigin = ChatGptAuthenticationTests.Origin
            });
            var tokens = new ChatGptAuthenticationTests.Tokens();
            builder.Services.AddSingleton<IJwksDocumentSource>(tokens);
            // Transport tests isolate persisted-token validation; the OAuth integration
            // journey below exercises the actual OpenIddict validator with PostgreSQL.
            builder.Services.AddSingleton<IAuthenticationService, FixtureAuthentication>();
            WebApplication app = builder.Build();
            if (internalPort) app.Use((context, next) => { context.Connection.LocalPort = 8788; return next(context); });
            StaticAssets assets = await StaticAssets.LoadAsync(Path.Combine(root!, "frontend", "dist"));
            app.UseMiddleware<WorkspaceMiddleware>(assets, true);
            ChatGptMcp.Map(app);
            await app.StartAsync();
            return new Host(app, directory, tokens, cookie, pingLog);
        }

        public HttpClient Client()
        {
            HttpClient http = _app.GetTestClient();
            http.BaseAddress = new Uri(ChatGptAuthenticationTests.Origin);
            return http;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();
            Tokens.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
