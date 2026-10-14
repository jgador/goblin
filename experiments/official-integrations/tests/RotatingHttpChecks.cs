using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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

internal static class RotatingHttpChecks
{
    private const string Issuer = "https://identity.customer.example/realm";
    private const string Resource = "https://goblin.customer.example/mcp";

    public static async Task<int> RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken token = deadline.Token;
        using RSA key = RSA.Create(2048), attacker = RSA.Create(2048);
        var documents = new Documents(JsonSerializer.Serialize(new
        {
            keys = new[] { JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(key.ExportParameters(false)) { KeyId = "current" }) }
        }));
        var cache = new CustomerSigningKeyCache(new OAuthDiscoverySummary(Issuer, new Uri(Issuer + "/authorize"),
            new Uri(Issuer + "/token"), new Uri(Issuer + "/certs")), documents, TimeProvider.System);
        var source = new LocalStatus();
        var boundary = new RotatingConnectorReadBoundary(cache, Resource, source);
        var owner = new Owner();
        var administration = new CustomerSigningKeyAdministration(cache, owner);
        bool invalidateBeforeDispatch = false, revokeBeforeDispatch = false;
        int dispatches = 0, checks = 0;
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        PrivateMcpHttpAdapter.AddServices(builder.Services, boundary);
        await using WebApplication app = builder.Build();
        PrivateMcpHttpAdapter.Map(app, boundary);
        // Test-only deterministic seam after authentication, before SDK dispatch.
        app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/mcp")
            {
                dispatches++;
                if (invalidateBeforeDispatch)
                {
                    invalidateBeforeDispatch = false;
                    await administration.InvalidateAsync(context.RequestAborted);
                }
                if (revokeBeforeDispatch) { revokeBeforeDispatch = false; source.Denied = true; }
            }
            await next(context);
        });
        await app.StartAsync(token);
        using HttpClient client = app.GetTestClient();
        client.BaseAddress = new Uri("https://goblin.customer.example/");
        client.Timeout = TimeSpan.FromSeconds(5);
        string valid = Issue(key);

        async Task Check(string name, Func<Task> action)
        {
            await action();
            checks++;
            Console.WriteLine("PASS " + name);
        }

        async Task<HttpResponseMessage> Rpc(string method, object? parameters, string? bearer = null,
            string? origin = null, string? host = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "mcp")
            {
                Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method, @params = parameters })
            };
            request.Headers.Accept.ParseAdd("application/json, text/event-stream");
            request.Headers.Add("MCP-Protocol-Version", "2025-06-18");
            if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            if (origin is not null) request.Headers.Add("Origin", origin);
            if (host is not null) request.Headers.Host = host;
            return await client.SendAsync(request, token);
        }

        async Task<bool> Call(string id = "42")
        {
            using var response = await Rpc("tools/call", new { name = "goblin_get_work_status", arguments = new { workId = id } }, valid);
            Require(response.IsSuccessStatusCode && response.Headers.CacheControl?.NoStore == true);
            using JsonDocument payload = await Payload(response, token);
            Require(!payload.RootElement.ToString().Contains(valid, StringComparison.Ordinal));
            return payload.RootElement.GetProperty("result").TryGetProperty("isError", out var error) && error.GetBoolean();
        }

        await Check("rotating public metadata is fixed and does not fetch keys or grant Work", async () =>
        {
            using var response = await client.GetAsync(PrivateMcpHttpAdapter.MetadataPath, token);
            using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            Require(response.IsSuccessStatusCode && body.RootElement.GetProperty("resource").GetString() == Resource &&
                body.RootElement.GetProperty("authorization_servers")[0].GetString() == Issuer && documents.Calls == 0 && source.Calls == 0);
        });
        await Check("rotating anonymous request receives fixed resource and scope challenge", async () =>
        {
            using var response = await Rpc("tools/list", null);
            Require(response.StatusCode == HttpStatusCode.Unauthorized && response.Headers.WwwAuthenticate.ToString() ==
                $"Bearer resource_metadata=\"https://goblin.customer.example{PrivateMcpHttpAdapter.MetadataPath}\", scope=\"{ConnectorReadBoundary.ReadScope}\"" && dispatches == 0);
        });
        foreach (bool foreignHost in new[] { false, true })
            await Check("rotating foreign Host or Origin is denied before key retrieval", async () =>
            {
                using var response = await Rpc("tools/list", null, valid,
                    origin: foreignHost ? null : "https://evil.example", host: foreignHost ? "evil.example" : null);
                Require(response.StatusCode == HttpStatusCode.Forbidden && documents.Calls == 0 && dispatches == 0 &&
                    !response.Headers.Contains("WWW-Authenticate") && !(await response.Content.ReadAsStringAsync(token)).Contains("evil.example", StringComparison.Ordinal));
            });
        foreach (string invalid in new[] { "malformed", Issue(attacker), Issue(key, audience: Resource + "/"), Issue(key, expired: true) })
            await Check("rotating invalid HTTP bearer is sanitized before dispatch", async () =>
            {
                using var response = await Rpc("tools/list", null, invalid);
                Require(response.StatusCode == HttpStatusCode.Unauthorized && dispatches == 0 && source.Calls == 0 &&
                    !(await response.Content.ReadAsStringAsync(token)).Contains(invalid, StringComparison.Ordinal));
            });
        await Check("rotating stateless SDK initialization retains no session", async () =>
        {
            using var response = await Rpc("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "rotating-contract", version = "1" } }, valid);
            using JsonDocument payload = await Payload(response, token);
            Require(response.IsSuccessStatusCode && payload.RootElement.TryGetProperty("result", out _) && !response.Headers.Contains("Mcp-Session-Id"));
        });
        await Check("rotating SDK discovery exposes only the safe status tool", async () =>
        {
            using var response = await Rpc("tools/list", null, valid);
            using JsonDocument payload = await Payload(response, token);
            var tools = payload.RootElement.GetProperty("result").GetProperty("tools");
            Require(tools.GetArrayLength() == 1 && tools[0].GetProperty("name").GetString() == "goblin_get_work_status" &&
                tools[0].GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean() && source.Calls == 0);
        });
        await Check("rotating HTTP tool reads verified identity with repeated local grants", async () =>
        {
            Require(!await Call() && !await Call() && source.Calls == 2);
        });
        await Check("cache invalidation between HTTP authentication and tool denies before source", async () =>
        {
            invalidateBeforeDispatch = true;
            int before = source.Calls;
            Require(await Call() && source.Calls == before && owner.Calls == 1);
        });
        await Check("next HTTP request freshly verifies a still-published key after flush", async () =>
        {
            int before = documents.Calls;
            Require(!await Call() && documents.Calls == before + 1 && source.Calls == 3);
        });
        await Check("grant revocation after HTTP authentication is enforced inside tool", async () =>
        {
            revokeBeforeDispatch = true;
            Require(await Call() && source.Calls == 4);
            source.Denied = false;
        });
        await Check("invalid Work ID is denied before source and ungranted ID reaches local policy", async () =>
        {
            int before = source.Calls;
            Require(await Call("-1") && source.Calls == before && await Call("43") && source.Calls == before + 1);
        });
        foreach (string mode in new[] { "anonymous", "cookie", "query", "multiple" })
            await Check("rotating request context cannot leak authentication to " + mode, async () =>
            {
                int before = dispatches;
                using var request = new HttpRequestMessage(HttpMethod.Post, mode == "query" ? "mcp?access_token=" + Uri.EscapeDataString(valid) : "mcp");
                if (mode == "cookie") request.Headers.Add("Cookie", "goblin_auth_session=" + valid);
                if (mode == "multiple") request.Headers.TryAddWithoutValidation("Authorization", new[] { "Bearer " + valid, "Bearer " + valid });
                using var response = await client.SendAsync(request, token);
                Require(response.StatusCode == HttpStatusCode.Unauthorized && dispatches == before &&
                    !(await response.Content.ReadAsStringAsync(token)).Contains(valid, StringComparison.Ordinal));
            });
        await app.StopAsync(token);
        Console.WriteLine($"PASS {checks} rotating HTTP checks; actual TestServer/SDK/JWT, synthetic keys/grants/owner only.");
        return checks;
    }

    private static string Issue(RSA key, string audience = Resource, bool expired = false) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer, Audience = audience, TokenType = "at+jwt",
            Claims = new Dictionary<string, object> { ["sub"] = "linked", ["jti"] = "ephemeral", ["scope"] = ConnectorReadBoundary.ReadScope },
            NotBefore = DateTime.UtcNow.AddMinutes(-10), Expires = DateTime.UtcNow.AddMinutes(expired ? -1 : 10),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(key) { KeyId = "current" }, SecurityAlgorithms.RsaSha256)
        });

    private static async Task<JsonDocument> Payload(HttpResponseMessage response, CancellationToken token)
    {
        string body = await response.Content.ReadAsStringAsync(token);
        string? payload = body.Split('\n').FirstOrDefault(line => line.StartsWith("data: ", StringComparison.Ordinal));
        return JsonDocument.Parse(payload is null ? body : payload[6..]);
    }

    private static void Require(bool value)
    {
        if (!value) throw new Exception("Rotating HTTP assertion failed.");
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

    private sealed class Owner : ILocalOwnerGrantAuthority
    {
        public int Calls { get; private set; }

        public Task RequireOwnerAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Calls++; return Task.CompletedTask; }
    }

    private sealed class LocalStatus : ICustomerWorkStatusSource
    {
        public bool Denied { get; set; }
        public int Calls { get; private set; }

        public Task<WorkStatusResult> ReadAuthorizedAsync(ConnectorIdentity identity, long workId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            if (Denied || identity.Issuer != Issuer || identity.Subject != "linked" || identity.Resource != Resource || workId != 42)
                throw new WorkReadFailure("Connector Work read access is not authorized.");
            return Task.FromResult(new WorkStatusResult("42", "7", "Ready", DateTimeOffset.UtcNow));
        }
    }
}
