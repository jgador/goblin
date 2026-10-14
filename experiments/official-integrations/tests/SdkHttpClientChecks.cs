using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
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
                new Uri(Issuer + "/token"), new Uri(Issuer + "/certs"), false), documents, TimeProvider.System);
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

            HttpClientTransport Transport() => new(new HttpClientTransportOptions
            {
                Endpoint = new Uri(Resource), Name = "Goblin synthetic HTTP contract",
                TransportMode = HttpTransportMode.StreamableHttp, ConnectionTimeout = TimeSpan.FromSeconds(5),
                OAuth = null, MaxReconnectionAttempts = 0, KnownSessionId = null, OwnsSession = false
            }, http, ownsHttpClient: false);

            async Task Unauthorized(Func<Task> action, string? credential)
            {
                int requests = observed.Requests, failures = observed.UnauthorizedResponses, calls = source.Calls;
                try { await action(); throw new Exception("Expected HTTP authentication failure."); }
                catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Unauthorized)
                {
                    Require((credential is null || !error.ToString().Contains(credential, StringComparison.Ordinal)) &&
                        !error.ToString().Contains(valid, StringComparison.Ordinal));
                }
                Require(observed.Requests == requests + 1 && observed.UnauthorizedResponses == failures + 1 &&
                    observed.LastChallenge == $"Bearer resource_metadata=\"https://goblin.customer.example{PrivateMcpHttpAdapter.MetadataPath}\", scope=\"{ConnectorReadBoundary.ReadScope}\"" &&
                    observed.SafeUnauthorizedBody && observed.UnauthorizedNoStore && !observed.NonPostUnauthorized && source.Calls == calls);
            }

            string?[] invalidCredentials = [null, "malformed", Issue(key, "linked", expired: true)];
            for (int index = 0; index < invalidCredentials.Length; index++)
            {
                string? credential = invalidCredentials[index];
                string label = new[] { "missing", "malformed", "expired" }[index];
                await Check(path + " SDK initialization rejects " + label + " bearer once with safe HTTP 401", async () =>
                {
                    http.DefaultRequestHeaders.Authorization = credential is null ? null : new AuthenticationHeaderValue("Bearer", credential);
                    await using var failedTransport = Transport();
                    await Unauthorized(async () =>
                    {
                        await using McpClient unexpected = await McpClient.CreateAsync(failedTransport, cancellationToken: token);
                    }, credential);
                    await failedTransport.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                });
            }
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", valid);
            await using var transport = Transport();
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
            for (int index = 0; index < invalidCredentials.Length; index++)
            {
                string? credential = invalidCredentials[index];
                string label = new[] { "missing", "malformed", "expired" }[index];
                await Check(path + " initialized SDK tool call rejects " + label + " bearer without Work access or retry", async () =>
                {
                    http.DefaultRequestHeaders.Authorization = credential is null ? null : new AuthenticationHeaderValue("Bearer", credential);
                    try { await Unauthorized(async () => { await Call(); }, credential); }
                    finally { http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", valid); }
                });
            }
            await Check(path + " SDK client accepts explicit credential restoration on a later call", async () =>
            {
                int before = source.Reads;
                Require((await Call()).IsError != true && source.Reads == before + 1);
            });
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
        await RunHttpsCompositionAsync(key, token, Check);
        await RunHttpsKeyFailuresAsync(key, token, Check);
        Console.WriteLine($"PASS {checks} SDK HTTP client checks; real client/server/TestServer/JWT, synthetic sources only.");
        return checks;
    }

    private static async Task RunHttpsCompositionAsync(RSA key, CancellationToken token, Func<string, Func<Task>, Task> check)
    {
        Uri address = new(Issuer + "/certs");
        string document = JsonSerializer.Serialize(new
        {
            keys = new[] { JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(key.ExportParameters(false)) { KeyId = "current" }) }
        });
        // The source owns its mocked transport. The outer guard also covers
        // construction failure; the ownership assertion precedes guard disposal.
        using var keyHandler = new PinnedKeyHandler(address, document);
        var configuration = new OAuthDiscoverySummary(Issuer, new Uri(Issuer + "/authorize"), new Uri(Issuer + "/token"), address, false);
        using var documents = new CustomerHttpsJwksDocumentSource(configuration, keyHandler);
        var source = new LocalStatus();
        var cache = new CustomerSigningKeyCache(configuration, documents, TimeProvider.System);
        var boundary = new RotatingConnectorReadBoundary(cache, Resource, source);
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        PrivateMcpHttpAdapter.AddServices(builder.Services, boundary);
        await using WebApplication app = builder.Build();
        PrivateMcpHttpAdapter.Map(app, boundary);
        await check("HTTPS-key composition startup performs no key fetch or Work access", () =>
        {
            Require(keyHandler.Calls == 0 && source.Calls == 0);
            return Task.CompletedTask;
        });
        await app.StartAsync(token);
        using var observed = new ObservedHandler(app.GetTestServer().CreateHandler());
        using var http = new HttpClient(observed) { Timeout = TimeSpan.FromSeconds(5) };
        string credential = Issue(key, "linked");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(Resource), Name = "Goblin synthetic HTTPS-key composition",
            TransportMode = HttpTransportMode.StreamableHttp, ConnectionTimeout = TimeSpan.FromSeconds(5),
            OAuth = null, MaxReconnectionAttempts = 0, KnownSessionId = null, OwnsSession = false
        }, http, ownsHttpClient: false);
        await using McpClient client = await McpClient.CreateAsync(transport, cancellationToken: token);
        await check("SDK cold initialization retrieves only one credential-free pinned public-key GET", () =>
        {
            Require(keyHandler.Calls == 1 && keyHandler.SafeRequests && keyHandler.ContentDisposed &&
                source.Calls == 0 && !observed.SawSession);
            return Task.CompletedTask;
        });

        Task<CallToolResult> Call() => client.CallToolAsync("goblin_get_work_status",
            new Dictionary<string, object?> { ["workId"] = "42" }, cancellationToken: token).AsTask();

        await check("SDK status read composes the HTTPS source/cache/verifier with local Work permission", async () =>
        {
            CallToolResult result = await Call();
            Require(result.IsError != true && result.Content.Count == 1 && result.Content[0] is TextContentBlock);
            string body = ((TextContentBlock)result.Content[0]).Text;
            using JsonDocument json = JsonDocument.Parse(body);
            WorkStatusResult? status = JsonSerializer.Deserialize<WorkStatusResult>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Require(json.RootElement.EnumerateObject().Count() == 4 && status?.WorkId == "42" &&
                status.Version == "9007199254740993" && status.Status == "Ready" && status.UpdatedAt == source.UpdatedAt &&
                !body.Contains(credential, StringComparison.Ordinal) && source.Calls == 1 && source.Reads == 1 && keyHandler.Calls == 1);
        });
        await check("cached public keys cannot bypass revoked local Work permission", async () =>
        {
            source.Denied = true;
            CallToolResult result = await Call();
            string body = JsonSerializer.Serialize(result);
            Require(result.IsError == true && !body.Contains(credential, StringComparison.Ordinal) &&
                !body.Contains("mcp/www_authenticate", StringComparison.Ordinal) && !body.Contains("9007199254740993", StringComparison.Ordinal) &&
                source.Calls == 2 && source.Reads == 1 && keyHandler.Calls == 1);
        });
        await check("explicit local permission restoration permits a later independent SDK read", async () =>
        {
            source.Denied = false;
            Require((await Call()).IsError != true && source.Calls == 3 && source.Reads == 2 && keyHandler.Calls == 1);
        });
        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await transport.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await app.StopAsync(token);
        documents.Dispose();
        await check("composed HTTPS key source disposes its owned mocked handler and response content", () =>
        {
            Require(keyHandler.WasDisposed && keyHandler.ContentDisposed && keyHandler.SafeRequests && keyHandler.Calls == 1);
            return Task.CompletedTask;
        });
    }

    private static async Task RunHttpsKeyFailuresAsync(RSA key, CancellationToken token, Func<string, Func<Task>, Task> check)
    {
        const string detail = "SYNTHETIC_KEY_SOURCE_DETAIL";
        Uri address = new(Issuer + "/certs"), redirect = new("https://untrusted.example/" + detail);
        foreach (var failure in new[]
        {
            (Name: "redirect", Status: HttpStatusCode.Found, Body: "{\"error\":\"" + detail + "\"}", Location: (Uri?)redirect),
            (Name: "non-200", Status: HttpStatusCode.ServiceUnavailable, Body: "{\"error\":\"" + detail + "\"}", Location: (Uri?)null),
            (Name: "malformed document", Status: HttpStatusCode.OK, Body: "{ " + detail, Location: (Uri?)null)
        })
        {
            using var keyHandler = new PinnedKeyHandler(address, failure.Body, failure.Status, failure.Location);
            var configuration = new OAuthDiscoverySummary(Issuer, new Uri(Issuer + "/authorize"), new Uri(Issuer + "/token"), address, false);
            using var documents = new CustomerHttpsJwksDocumentSource(configuration, keyHandler);
            var source = new LocalStatus();
            // Freeze only cache age/cooldown; JWT validity and request deadlines
            // retain real time. No sleep is needed to stay inside the cooldown.
            var boundary = new RotatingConnectorReadBoundary(new CustomerSigningKeyCache(configuration, documents, new FrozenCacheClock()), Resource, source);
            WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.UseTestServer();
            PrivateMcpHttpAdapter.AddServices(builder.Services, boundary);
            await using WebApplication app = builder.Build();
            PrivateMcpHttpAdapter.Map(app, boundary);
            await app.StartAsync(token);
            using var observed = new ObservedHandler(app.GetTestServer().CreateHandler());
            using var http = new HttpClient(observed) { Timeout = TimeSpan.FromSeconds(5) };
            string credential = Issue(key, "linked");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);

            HttpClientTransport Transport() => new(new HttpClientTransportOptions
            {
                Endpoint = new Uri(Resource), Name = "Goblin synthetic HTTPS-key failure",
                TransportMode = HttpTransportMode.StreamableHttp, ConnectionTimeout = TimeSpan.FromSeconds(5),
                OAuth = null, MaxReconnectionAttempts = 0, KnownSessionId = null, OwnsSession = false
            }, http, ownsHttpClient: false);

            await using var transport = Transport();

            async Task DenyCreationAsync(HttpClientTransport candidate, int expectedRequests)
            {
                try
                {
                    await using McpClient unexpected = await McpClient.CreateAsync(candidate, cancellationToken: token);
                    throw new Exception("Expected key-source authentication failure.");
                }
                catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Unauthorized)
                {
                    Require(!error.ToString().Contains(credential, StringComparison.Ordinal) &&
                        !error.ToString().Contains(detail, StringComparison.Ordinal) && !error.ToString().Contains(redirect.AbsoluteUri, StringComparison.Ordinal));
                }
                Require(observed.Requests == expectedRequests && observed.UnauthorizedResponses == expectedRequests && observed.SafeUnauthorizedBody && observed.UnauthorizedNoStore &&
                    !observed.SawSession && !observed.NonPostUnauthorized &&
                    observed.LastChallenge == $"Bearer resource_metadata=\"https://goblin.customer.example{PrivateMcpHttpAdapter.MetadataPath}\", scope=\"{ConnectorReadBoundary.ReadScope}\"" &&
                    keyHandler.Calls == 1 && keyHandler.SafeRequests && keyHandler.ContentDisposed && source.Calls == 0 && source.Reads == 0);
            }

            await check("cold SDK key " + failure.Name + " produces one safe HTTP 401 without Work access or retry",
                () => DenyCreationAsync(transport, 1));
            await transport.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await check("explicit second SDK creation after key " + failure.Name + " stays denied without cooldown refetch", async () =>
            {
                await using var secondTransport = Transport();
                await DenyCreationAsync(secondTransport, 2);
                await secondTransport.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                Require(observed.Requests == 2 && observed.UnauthorizedResponses == 2 && keyHandler.Calls == 1 && source.Calls == 0);
            });
            await app.StopAsync(token);
            documents.Dispose();
            await check("cold SDK key " + failure.Name + " disposes failed response and owned handler", () =>
            {
                Require(keyHandler.WasDisposed && keyHandler.ContentDisposed && keyHandler.Calls == 1 && source.Calls == 0);
                return Task.CompletedTask;
            });
        }
    }

    private static string Issue(RSA key, string subject, bool expired = false) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = Issuer, Audience = Resource, TokenType = "at+jwt",
        Claims = new Dictionary<string, object> { ["sub"] = subject, ["jti"] = "ephemeral", ["scope"] = ConnectorReadBoundary.ReadScope },
        NotBefore = DateTime.UtcNow.AddMinutes(-10), Expires = DateTime.UtcNow.AddMinutes(expired ? -5 : 10),
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
        public int UnauthorizedResponses { get; private set; }
        public string? LastChallenge { get; private set; }
        public bool SafeUnauthorizedBody { get; private set; }
        public bool UnauthorizedNoStore { get; private set; }
        public bool NonPostUnauthorized { get; private set; }

        public ObservedHandler(HttpMessageHandler inner) { InnerHandler = inner; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Require(request.RequestUri?.AbsoluteUri == Resource);
            Requests++;
            SawSession |= request.Headers.Contains("Mcp-Session-Id");
            HttpResponseMessage response = await base.SendAsync(request, token);
            SawSession |= response.Headers.Contains("Mcp-Session-Id");
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                UnauthorizedResponses++;
                LastChallenge = response.Headers.WwwAuthenticate.ToString();
                UnauthorizedNoStore = response.Headers.CacheControl?.NoStore == true;
                NonPostUnauthorized |= request.Method != HttpMethod.Post;
                string body = await response.Content.ReadAsStringAsync(token);
                using JsonDocument json = JsonDocument.Parse(body);
                SafeUnauthorizedBody = body.Length <= 512 && json.RootElement.EnumerateObject().Count() == 1 &&
                    json.RootElement.GetProperty("error").GetString() == "Connector authentication is required.";
            }
            return response;
        }
    }

    private sealed class FrozenCacheClock : TimeProvider
    {
        public override long GetTimestamp() => 0;
    }

    private sealed class PinnedKeyHandler : HttpMessageHandler
    {
        private readonly Uri _address;
        private readonly string _document;
        private readonly HttpStatusCode _status;
        private readonly Uri? _location;
        private KeyContent? _content;
        public int Calls { get; private set; }
        public bool SafeRequests { get; private set; } = true;
        public bool WasDisposed { get; private set; }
        public bool ContentDisposed => _content?.WasDisposed == true;

        public PinnedKeyHandler(Uri address, string document) : this(address, document, HttpStatusCode.OK, null) { }

        public PinnedKeyHandler(Uri address, string document, HttpStatusCode status, Uri? location)
        {
            _address = address;
            _document = document;
            _status = status;
            _location = location;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            SafeRequests &= request.Method == HttpMethod.Get && request.RequestUri?.OriginalString == _address.OriginalString &&
                request.Content is null && request.Headers.All(header => header.Key.Equals("Accept", StringComparison.OrdinalIgnoreCase)) &&
                request.Headers.Accept.Select(value => value.MediaType).SequenceEqual(new[] { "application/jwk-set+json", "application/json" });
            Require(SafeRequests && !WasDisposed);
            _content = new KeyContent(_document);
            var response = new HttpResponseMessage(_status) { RequestMessage = request, Content = _content };
            response.Headers.Location = _location;
            return Task.FromResult(response);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class KeyContent : StringContent
    {
        public bool WasDisposed { get; private set; }

        public KeyContent(string document) : base(document, Encoding.UTF8, "application/jwk-set+json") { }

        protected override void Dispose(bool disposing)
        {
            if (disposing) WasDisposed = true;
            base.Dispose(disposing);
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
