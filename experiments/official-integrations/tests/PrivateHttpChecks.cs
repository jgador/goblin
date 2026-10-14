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

internal static class PrivateHttpChecks
{
    public static async Task<int> RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using RSA key = RSA.Create(2048);
        var grants = new Grants();
        int reads = 0, checks = 0;
        var boundary = new ConnectorReadBoundary("https://identity.customer.example/", "https://goblin.customer.example/mcp",
            new RsaSecurityKey(key.ExportParameters(false)), new TestGrantStatusSource(grants,
                (id, _) => { reads++; return Task.FromResult(new WorkStatusResult(id.ToString(), "5", "Ready", DateTimeOffset.UtcNow)); }));
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        PrivateMcpHttpAdapter.AddServices(builder.Services, boundary);
        await using WebApplication app = builder.Build();
        PrivateMcpHttpAdapter.Map(app, boundary);
        await app.StartAsync(deadline.Token);
        using HttpClient client = app.GetTestClient();
        client.BaseAddress = new Uri("https://goblin.customer.example/");
        client.Timeout = TimeSpan.FromSeconds(5);
        string valid = Issue(key);

        async Task Check(string name, Func<Task> check)
        {
            await check();
            Console.WriteLine("PASS " + name);
            checks++;
        }

        async Task<HttpResponseMessage> Rpc(string method, object? parameters, string? credential = null,
            string? origin = null, string? host = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "mcp")
            {
                Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method, @params = parameters })
            };
            request.Headers.Accept.ParseAdd("application/json, text/event-stream");
            request.Headers.Add("MCP-Protocol-Version", "2025-06-18");
            if (credential is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            if (origin is not null) request.Headers.Add("Origin", origin);
            if (host is not null) request.Headers.Host = host;
            return await client.SendAsync(request, deadline.Token);
        }

        await Check("fixed public resource metadata requires no bearer", async () =>
        {
            using var response = await client.GetAsync(PrivateMcpHttpAdapter.MetadataPath, deadline.Token);
            using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
            Require(response.IsSuccessStatusCode && body.RootElement.GetProperty("resource").GetString() == boundary.Resource &&
                body.RootElement.GetProperty("authorization_servers")[0].GetString() == boundary.Issuer);
        });
        await Check("missing bearer returns 401 and fixed metadata challenge", async () =>
        {
            using var response = await Rpc("tools/list", null);
            Require(response.StatusCode == HttpStatusCode.Unauthorized &&
                response.Headers.WwwAuthenticate.ToString().Contains("https://goblin.customer.example" + PrivateMcpHttpAdapter.MetadataPath, StringComparison.Ordinal));
            Require(reads == 0 && grants.Calls == 0);
        });
        foreach (string credential in new[] { "malformed", Issue(key, audience: "https://other.customer.example/mcp"), Issue(key, expired: true) })
            await Check("invalid HTTP credential is sanitized before dispatch", async () =>
            {
                using var response = await Rpc("tools/list", null, credential);
                string body = await response.Content.ReadAsStringAsync(deadline.Token);
                Require(response.StatusCode == HttpStatusCode.Unauthorized && !body.Contains(credential, StringComparison.Ordinal));
                Require(reads == 0 && grants.Calls == 0);
            });
        await Check("foreign Origin returns 403 before token/grant use", async () =>
        {
            using var response = await Rpc("tools/list", null, valid, "https://evil.example");
            Require(response.StatusCode == HttpStatusCode.Forbidden && reads == 0 && grants.Calls == 0);
        });
        await Check("untrusted Host returns 403 without reflected URL", async () =>
        {
            using var response = await Rpc("tools/list", null, valid, host: "evil.example");
            Require(response.StatusCode == HttpStatusCode.Forbidden &&
                !response.Headers.Contains("WWW-Authenticate") && !(await response.Content.ReadAsStringAsync(deadline.Token)).Contains("evil.example", StringComparison.Ordinal));
        });
        await Check("authenticated stateless MCP initialization", async () =>
        {
            using var response = await Rpc("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "local-contract", version = "1" } }, valid);
            using JsonDocument result = await Payload(response, deadline.Token);
            Require(response.IsSuccessStatusCode && result.RootElement.TryGetProperty("result", out _) && !response.Headers.Contains("Mcp-Session-Id"));
        });
        await Check("only the read-only Work status tool is discoverable", async () =>
        {
            using var response = await Rpc("tools/list", null, valid);
            using JsonDocument result = await Payload(response, deadline.Token);
            var available = result.RootElement.GetProperty("result").GetProperty("tools");
            Require(available.GetArrayLength() == 1 && available[0].GetProperty("name").GetString() == "goblin_get_work_status" &&
                available[0].GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        });
        await Check("authorized HTTP tool reads only the granted Work", async () =>
        {
            using var response = await Rpc("tools/call", new { name = "goblin_get_work_status", arguments = new { workId = "42" } }, valid);
            using JsonDocument result = await Payload(response, deadline.Token);
            Require(response.IsSuccessStatusCode && !IsError(result) && reads == 1 && grants.Calls == 1);
            Require(!result.RootElement.ToString().Contains(valid, StringComparison.Ordinal));
        });
        await Check("ungranted Work is an MCP tool error without data access", async () =>
        {
            using var response = await Rpc("tools/call", new { name = "goblin_get_work_status", arguments = new { workId = "43" } }, valid);
            using JsonDocument result = await Payload(response, deadline.Token);
            Require(IsError(result) && reads == 1);
        });
        grants.Revoked = true;
        await Check("revocation rechecked on a fresh HTTP call using the same bearer", async () =>
        {
            using var response = await Rpc("tools/call", new { name = "goblin_get_work_status", arguments = new { workId = "42" } }, valid);
            using JsonDocument result = await Payload(response, deadline.Token);
            Require(IsError(result) && reads == 1);
        });
        await Check("anonymous metadata cannot acquire a Work session", async () =>
        {
            using var response = await Rpc("tools/list", null);
            Require(response.StatusCode == HttpStatusCode.Unauthorized && reads == 1);
        });
        await Check("legacy SSE endpoint is not mapped", async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "mcp/sse");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", valid);
            using var response = await client.SendAsync(request, deadline.Token);
            Require(response.StatusCode == HttpStatusCode.NotFound);
        });
        await Check("owner cookie is not connector authentication", async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "mcp");
            request.Headers.Add("Cookie", "goblin_auth_session=" + valid);
            using var response = await client.SendAsync(request, deadline.Token);
            Require(response.StatusCode == HttpStatusCode.Unauthorized && reads == 1);
        });
        await Check("query token is not connector authentication", async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "mcp?access_token=" + Uri.EscapeDataString(valid));
            using var response = await client.SendAsync(request, deadline.Token);
            Require(response.StatusCode == HttpStatusCode.Unauthorized && reads == 1 &&
                !(await response.Content.ReadAsStringAsync(deadline.Token)).Contains(valid, StringComparison.Ordinal));
        });
        await Check("multiple Authorization fields are rejected", async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "mcp");
            request.Headers.TryAddWithoutValidation("Authorization", new[] { "Bearer " + valid, "Bearer " + valid });
            using var response = await client.SendAsync(request, deadline.Token);
            Require(response.StatusCode == HttpStatusCode.Unauthorized && reads == 1);
        });
        await app.StopAsync(deadline.Token);
        Console.WriteLine($"PASS {checks} in-process HTTP/MCP checks; real ASP.NET pipeline/SDK/JWT, test grants/source only.");
        return checks;
    }

    private static string Issue(RSA key, string audience = "https://goblin.customer.example/mcp", bool expired = false) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "https://identity.customer.example/", Audience = audience, TokenType = "at+jwt",
            Claims = new Dictionary<string, object> { ["sub"] = "linked", ["jti"] = "local-id", ["scope"] = ConnectorReadBoundary.ReadScope },
            IssuedAt = DateTime.UtcNow.AddMinutes(-10), NotBefore = DateTime.UtcNow.AddMinutes(-10),
            Expires = expired ? DateTime.UtcNow.AddMinutes(-5) : DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256)
        });

    private static async Task<JsonDocument> Payload(HttpResponseMessage response, CancellationToken token)
    {
        string body = await response.Content.ReadAsStringAsync(token);
        string? payload = body.Split('\n').FirstOrDefault(line => line.StartsWith("data: ", StringComparison.Ordinal));
        return JsonDocument.Parse(payload is null ? body : payload[6..]);
    }

    private static bool IsError(JsonDocument result) => result.RootElement.TryGetProperty("error", out _) ||
        result.RootElement.GetProperty("result").TryGetProperty("isError", out var error) && error.GetBoolean();

    private static void Require(bool value)
    {
        if (!value) throw new Exception("Private HTTP contract assertion failed.");
    }

    private sealed class Grants : ITestWorkReadGrants
    {
        public bool Revoked { get; set; }
        public int Calls { get; private set; }

        public Task<bool> CanReadAsync(ConnectorIdentity identity, long workId, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(!Revoked && identity.Subject == "linked" && workId == 42);
        }
    }
}
