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
using Goblin.Application.Connections;
using Goblin.Contracts;
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
    [InlineData("/api/integrations/chatgpt/grants/revoke")]
    [InlineData("/api/integrations/chatgpt/package")]
    [InlineData("/api/integrations/chatgpt/creator/check")]
    [InlineData("/api/integrations/chatgpt/creator/prepare")]
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

    [Fact]
    public async Task OwnerPreparesARequestUsingServerOwnedMetadataWithoutRegisteringOrSharingWork()
    {
        var authentication = new PluginAuthentication();
        await using Host host = await Host.CreateAsync(pluginAuthentication: authentication);
        using HttpClient http = host.Client();
        http.DefaultRequestHeaders.Add("Cookie", host.OwnerCookie);
        http.DefaultRequestHeaders.Add("Origin", ChatGptAuthenticationTests.Origin);
        using HttpResponseMessage response = await http.PostAsync("/api/integrations/chatgpt/creator/prepare", Json("{\"registeredAppId\":\"plugin_asdk_app_1234\"}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, authentication.Preparations);
        Assert.Equal(ChatGptAuthenticationTests.Origin + "/mcp", authentication.Context!.ServerUrl);
        Assert.Equal(ChatGptConfiguration.ClientId, authentication.Context.ClientId);
        Assert.Equal("plugin_asdk_app_1234", authentication.Context.RegisteredAppId);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement result = document.RootElement;
        Assert.False(result.GetProperty("pluginRegistered").GetBoolean());
        Assert.False(result.GetProperty("connectionVerified").GetBoolean());
        Assert.False(result.GetProperty("creatorAccess").GetProperty("automaticCreationAvailable").GetBoolean());
        Assert.Equal("Fixture creator prompt", result.GetProperty("creatorPrompt").GetString());
        string payload = result.GetRawText();
        Assert.False(result.TryGetProperty("archiveBase64", out _));
        Assert.DoesNotContain("goblin-plugin-setup", payload);
        Assert.DoesNotContain("clientSecret", payload);
        Assert.DoesNotContain("ownerSubject", payload);
        Assert.DoesNotContain("workId", payload);
        Assert.Equal(0, host.Tokens.Reads);
    }

    [Theory]
    [InlineData("invalid-id", true, HttpStatusCode.BadRequest)]
    [InlineData("plugin_asdk_app_1234", false, HttpStatusCode.Conflict)]
    public async Task InvalidConnectionIdOrApiKeyCannotStartPreparation(string appId, bool chatGpt, HttpStatusCode expected)
    {
        var authentication = new PluginAuthentication(chatGpt);
        await using Host host = await Host.CreateAsync(pluginAuthentication: authentication);
        using HttpClient http = host.Client();
        http.DefaultRequestHeaders.Add("Cookie", host.OwnerCookie);
        http.DefaultRequestHeaders.Add("Origin", ChatGptAuthenticationTests.Origin);
        using HttpResponseMessage response = await http.PostAsync("/api/integrations/chatgpt/creator/prepare", Json(JsonSerializer.Serialize(new { registeredAppId = appId })));
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(0, authentication.Preparations);
    }

    private sealed class PluginAuthentication : ICodexAuthentication, ICodexPluginSetup
    {
        private readonly bool _chatGpt;

        public PluginAuthentication(bool chatGpt = true) => _chatGpt = chatGpt;

        public int Preparations { get; private set; }
        public ChatGptPluginContext? Context { get; private set; }
        public Task<AuthenticationState> StatusAsync() => Task.FromResult(new AuthenticationState(
            _chatGpt ? new ChatGPTAccountView("owner@example.test", "plus") : new ApiKeyAccountView(), null, null, null, true));
        public Task<PluginCreatorAccess> CheckPluginCreatorAsync(CancellationToken token) => Task.FromResult(new PluginCreatorAccess(
            true, false, null, false, false, false, [], DateTimeOffset.UtcNow));
        public async Task<ChatGptPluginDraft> PreparePluginAsync(ChatGptPluginContext context, CancellationToken token)
        {
            Preparations++;
            Context = context;
            return new("Fixture private plugin description", "Fixture creator prompt", "fixture-model", 1,
                await CheckPluginCreatorAsync(token));
        }
        public Task<AuthenticationState> LoginChatGPTAsync() => throw new NotSupportedException();
        public Task<AuthenticationState> LoginApiKeyAsync(string? value) => throw new NotSupportedException();
        public Task<AuthenticationState> CancelLoginAsync() => throw new NotSupportedException();
        public Task<AuthenticationState> LogoutAsync() => throw new NotSupportedException();
        public Task<PromptResult> SendPromptAsync(string? value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task RealSdkDiscoversReadOnlyToolsAndDoesNotReuseAuthenticationBetweenRequests()
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
        Assert.Equal(new[] { "goblin_get_work_attention", "goblin_get_work_result", "goblin_get_work_status", "goblin_list_work", "goblin_ping" }, tools.Select(t => t.Name).Order());
        foreach (McpClientTool tool in tools)
        {
            Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint);
            Assert.False(tool.ProtocolTool.Annotations?.DestructiveHint);
            Assert.NotNull(tool.ProtocolTool.OutputSchema);
            Assert.NotNull(tool.ProtocolTool.Meta?["securitySchemes"]);
        }
        // This host has an unreachable database and no GitHub connection. Ping
        // must confirm the transport without reading Work or its grants.
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
        CallToolResult invalid = await client.CallToolAsync("goblin_get_work_status", new Dictionary<string, object?> { ["workId"] = "1e3" }, cancellationToken: deadline.Token);
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

        public static async Task<Host> CreateAsync(bool internalPort = false, bool configured = true,
            ICodexAuthentication? pluginAuthentication = null)
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
            builder.Services.AddSingleton(new ConnectionService(pluginAuthentication ?? new PluginAuthentication()));
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
