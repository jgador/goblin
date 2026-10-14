using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Goblin.Mcp.Http.Prototype;
using Goblin.Mcp.Prototype;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

internal static class SdkHttpClientChecks
{
    private const string Issuer = "https://identity.customer.example/realm";
    private const string Resource = "https://goblin.customer.example/mcp";

    public static async Task<int> RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken token = deadline.Token;
        using RSA key = RSA.Create(2048);
        int checks = 0;

        async Task Check(string name, Func<Task> action)
        {
            await action();
            checks++;
            Console.WriteLine("PASS " + name);
        }

        foreach (bool rotating in new[] { false, true })
        {
            string path = rotating ? "rotating" : "fixed";
            var source = new LocalStatus();
            var documents = new Documents(JsonSerializer.Serialize(new
            {
                keys = new[] { JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(key.ExportParameters(false)) { KeyId = "current" }) }
            }));
            var cache = new CustomerSigningKeyCache(new OAuthDiscoverySummary(Issuer, new Uri(Issuer + "/authorize"),
                new Uri(Issuer + "/token"), new Uri(Issuer + "/certs")), documents, TimeProvider.System);
            var rotation = new RotatingConnectorReadBoundary(cache, Resource, source);
            var fixedBoundary = new ConnectorReadBoundary(Issuer, Resource,
                new RsaSecurityKey(key.ExportParameters(false)) { KeyId = "current" }, source);
            var administration = new CustomerSigningKeyAdministration(cache, new Owner());
            bool invalidateBeforeDispatch = false;
            WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.UseTestServer();
            if (rotating) PrivateMcpHttpAdapter.AddServices(builder.Services, rotation);
            else PrivateMcpHttpAdapter.AddServices(builder.Services, fixedBoundary);
            await using WebApplication app = builder.Build();
            if (rotating) PrivateMcpHttpAdapter.Map(app, rotation);
            else PrivateMcpHttpAdapter.Map(app, fixedBoundary);
            app.Use(async (context, next) =>
            {
                if (invalidateBeforeDispatch && context.Request.Path == "/mcp")
                {
                    invalidateBeforeDispatch = false;
                    await administration.InvalidateAsync(context.RequestAborted);
                }
                await next(context);
            });
            await app.StartAsync(token);
            using var observed = new ObservedHandler(app.GetTestServer().CreateHandler());
            using var http = new HttpClient(observed) { Timeout = TimeSpan.FromSeconds(5) };
            string valid = Issue(key, "linked");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", valid);
            await using var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(Resource), Name = "Goblin synthetic HTTP contract",
                TransportMode = HttpTransportMode.StreamableHttp, ConnectionTimeout = TimeSpan.FromSeconds(5),
                OAuth = null, MaxReconnectionAttempts = 0, KnownSessionId = null, OwnsSession = false
            }, http, ownsHttpClient: false);
            await using McpClient client = await McpClient.CreateAsync(transport, cancellationToken: token);

            Task<CallToolResult> Call(string id = "42", CancellationToken? cancellation = null) =>
                client.CallToolAsync("goblin_get_work_status", new Dictionary<string, object?> { ["workId"] = id },
                    cancellationToken: cancellation ?? token).AsTask();

            void Denied(CallToolResult result)
            {
                string json = JsonSerializer.Serialize(result);
                Require(result.IsError == true && !json.Contains(valid, StringComparison.Ordinal) &&
                    !json.Contains("mcp/www_authenticate", StringComparison.Ordinal) && !json.Contains("9007199254740993", StringComparison.Ordinal));
            }

            await Check(path + " SDK HTTP client initializes without Work access or a session ID", () =>
            {
                Require(source.Calls == 0 && observed.Requests > 0 && !observed.SawSession &&
                    documents.Calls == (rotating ? 1 : 0));
                return Task.CompletedTask;
            });
            await Check(path + " SDK client discovers only the safe tool and exact OAuth mirror", async () =>
            {
                var tools = await client.ListToolsAsync(cancellationToken: token);
                Require(tools.Count == 1 && tools[0].Name == "goblin_get_work_status");
                var descriptor = tools[0].ProtocolTool;
                using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(descriptor));
                var schemes = json.RootElement.GetProperty("_meta").GetProperty("securitySchemes");
                Require(descriptor.Annotations?.ReadOnlyHint == true && descriptor.Annotations.DestructiveHint == false &&
                    schemes.GetArrayLength() == 1 && schemes[0].GetProperty("type").GetString() == "oauth2" &&
                    schemes[0].GetProperty("scopes").GetArrayLength() == 1 &&
                    schemes[0].GetProperty("scopes")[0].GetString() == ConnectorReadBoundary.ReadScope && source.Calls == 0);
            });
            await Check(path + " SDK client receives only safe status fields with exact bigint", async () =>
            {
                CallToolResult result = await Call();
                Require(result.IsError != true && result.Content.Count == 1 && result.Content[0] is TextContentBlock);
                string text = ((TextContentBlock)result.Content[0]).Text;
                using JsonDocument json = JsonDocument.Parse(text);
                WorkStatusResult? status = JsonSerializer.Deserialize<WorkStatusResult>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                Require(json.RootElement.EnumerateObject().Count() == 4 && status?.WorkId == "42" &&
                    status.Version == "9007199254740993" && status.Status == "Ready" && status.UpdatedAt == source.UpdatedAt &&
                    !text.Contains(valid, StringComparison.Ordinal) && source.Reads == 1);
            });
            await Check(path + " SDK client rechecks revoked local permission without OAuth challenge", async () =>
            {
                source.Denied = true;
                int before = source.Calls;
                Denied(await Call());
                Require(source.Calls == before + 1 && source.Reads == 1);
                source.Denied = false;
            });
            await Check(path + " SDK client ungranted Work fails without a status projection", async () =>
            {
                int before = source.Reads;
                Denied(await Call("43"));
                Require(source.Reads == before);
            });
            await Check(path + " SDK client invalid Work ID fails before local policy", async () =>
            {
                int before = source.Calls;
                Denied(await Call("-1"));
                Require(source.Calls == before);
            });
            await Check(path + " same SDK client changing bearer subject cannot reuse a prior identity", async () =>
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Issue(key, "unlinked"));
                Denied(await Call());
                Require(source.LastSubject == "unlinked" && source.Reads == 1);
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", valid);
                Require((await Call()).IsError != true && source.LastSubject == "linked" && source.Reads == 2);
            });
            await Check(path + " SDK client preserves pre-cancelled tool calls without Work access", async () =>
            {
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                int before = source.Calls;
                try { await Call(cancellation: cancelled.Token); throw new Exception("Expected caller cancellation."); }
                catch (OperationCanceledException) { Require(source.Calls == before); }
            });
            if (rotating)
            {
                await Check("rotating SDK call retains its lease across authentication and dispatch", async () =>
                {
                    invalidateBeforeDispatch = true;
                    int before = source.Calls;
                    Denied(await Call());
                    Require(source.Calls == before && documents.Calls == 1);
                });
                await Check("rotating SDK next call freshly verifies still-published keys after flush", async () =>
                {
                    Require((await Call()).IsError != true && documents.Calls == 2 && source.Reads == 3);
                });
            }
            await Check(path + " SDK transport stays stateless on one pinned TestServer target", () =>
            {
                Require(!observed.SawSession && observed.Requests > 5);
                return Task.CompletedTask;
            });
            // Dispose sessions/transport before stopping the host; no listener or OAuth flow.
            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await transport.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await app.StopAsync(token);
        }
        Console.WriteLine($"PASS {checks} SDK HTTP client checks; real client/server/TestServer/JWT, synthetic sources only.");
        return checks;
    }

    private static string Issue(RSA key, string subject) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = Issuer, Audience = Resource, TokenType = "at+jwt",
        Claims = new Dictionary<string, object> { ["sub"] = subject, ["jti"] = "ephemeral", ["scope"] = ConnectorReadBoundary.ReadScope },
        NotBefore = DateTime.UtcNow.AddMinutes(-1), Expires = DateTime.UtcNow.AddMinutes(10),
        SigningCredentials = new SigningCredentials(new RsaSecurityKey(key) { KeyId = "current" }, SecurityAlgorithms.RsaSha256)
    });

    private static void Require(bool value)
    {
        if (!value) throw new Exception("SDK HTTP client assertion failed.");
    }

    private sealed class ObservedHandler : DelegatingHandler
    {
        public int Requests { get; private set; }
        public bool SawSession { get; private set; }

        public ObservedHandler(HttpMessageHandler inner) { InnerHandler = inner; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Require(request.RequestUri?.AbsoluteUri == Resource);
            Requests++;
            SawSession |= request.Headers.Contains("Mcp-Session-Id");
            HttpResponseMessage response = await base.SendAsync(request, token);
            SawSession |= response.Headers.Contains("Mcp-Session-Id");
            return response;
        }
    }

    private sealed class Documents : ICustomerJwksDocumentSource
    {
        private readonly string _body;
        public int Calls { get; private set; }

        public Documents(string body) { _body = body; }

        public Task<string> ReadAsync(Uri pinnedAddress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(_body);
        }
    }

    private sealed class LocalStatus : ICustomerWorkStatusSource
    {
        public bool Denied { get; set; }
        public int Calls { get; private set; }
        public int Reads { get; private set; }
        public string? LastSubject { get; private set; }
        public DateTimeOffset UpdatedAt { get; } = new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);

        public Task<WorkStatusResult> ReadAuthorizedAsync(ConnectorIdentity identity, long workId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            LastSubject = identity.Subject;
            if (Denied || identity.Issuer != Issuer || identity.Subject != "linked" || identity.Resource != Resource || workId != 42)
                throw new WorkReadFailure("Connector Work read access is not authorized.");
            Reads++;
            return Task.FromResult(new WorkStatusResult("42", "9007199254740993", "Ready", UpdatedAt));
        }
    }

    private sealed class Owner : ILocalOwnerGrantAuthority
    {
        public Task RequireOwnerAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    }
}
