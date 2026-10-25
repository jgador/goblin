using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
    public async Task RealSdkDiscoversFourReadOnlyToolsAndDoesNotReuseAuthenticationBetweenRequests()
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
        Assert.Equal(new[] { "goblin_get_work_attention", "goblin_get_work_result", "goblin_get_work_status", "goblin_list_work" }, tools.Select(t => t.Name).Order());
        foreach (McpClientTool tool in tools)
        {
            Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint);
            Assert.False(tool.ProtocolTool.Annotations?.DestructiveHint);
            Assert.NotNull(tool.ProtocolTool.OutputSchema);
            Assert.NotNull(tool.ProtocolTool.Meta?["securitySchemes"]);
        }
        CallToolResult invalid = await client.CallToolAsync("goblin_get_work_status", new Dictionary<string, object?> { ["workId"] = "1e3" }, cancellationToken: deadline.Token);
        Assert.True(invalid.IsError);
        Assert.DoesNotContain("mcp/www_authenticate", JsonSerializer.Serialize(invalid));
        http.DefaultRequestHeaders.Authorization = new("Bearer", host.Tokens.Token("expiry"));
        HttpRequestException failure = await Assert.ThrowsAsync<HttpRequestException>(async () => await client.ListToolsAsync(cancellationToken: deadline.Token));
        Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
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

    private sealed class Host : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly string _directory;
        public ChatGptAuthenticationTests.Tokens Tokens { get; }
        public string OwnerCookie { get; }

        private Host(WebApplication app, string directory, ChatGptAuthenticationTests.Tokens tokens, string cookie)
        { _app = app; _directory = directory; Tokens = tokens; OwnerCookie = cookie; }

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
            return new Host(app, directory, tokens, cookie);
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
