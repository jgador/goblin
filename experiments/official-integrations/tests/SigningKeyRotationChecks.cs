using System.Security.Cryptography;
using System.Text.Json;
using Goblin.Mcp.Prototype;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

internal static class SigningKeyRotationChecks
{
    private const string Issuer = "https://identity.customer.example/realm";
    private const string Resource = "https://goblin.customer.example/mcp";

    public static async Task<int> RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken token = deadline.Token;
        using RSA oldKey = RSA.Create(2048), newKey = RSA.Create(2048), attacker = RSA.Create(2048);
        JsonWebKey old = Public(oldKey, "old"), next = Public(newKey, "new");
        var clock = new Clock();
        var documents = new Documents { Body = Document(old) };
        var cache = new CustomerSigningKeyCache(Configuration(), documents, clock);
        var source = new StatusSource();
        var boundary = new RotatingConnectorReadBoundary(cache, Resource, source);
        int checks = 0;

        async Task Pass(string name, Func<Task> action)
        {
            await action();
            checks++;
            Console.WriteLine("PASS " + name);
        }

        async Task Deny(string name, Func<Task> action, bool localDenial = false)
        {
            int reads = source.Calls;
            try { await action(); throw new Exception("Expected rotation denial."); }
            catch (WorkReadFailure error)
            {
                Require(error.Message == "Connector Work read access is not authorized." && error.InnerException is null);
            }
            Require(source.Calls == reads + (localDenial ? 1 : 0));
            checks++;
            Console.WriteLine("PASS " + name);
        }

        string original = Issue(oldKey, "old");
        await Pass("cold JWKS validates signed token before local source", async () =>
        {
            await boundary.ReadAsync(original, 42, token);
            Require(documents.Calls == 1 && source.Calls == 1 && documents.Address == Configuration().JwksEndpoint);
        });
        await Pass("cached public key is defensively copied", async () =>
        {
            RsaSecurityKey copy = await cache.GetAsync("old", token);
            copy.Parameters.Modulus![0] = 0;
            await boundary.ReadAsync(original, 42, token);
            Require(documents.Calls == 1);
        });
        await Deny("unknown kid denied during minimum refresh interval", () => boundary.ReadAsync(Issue(newKey, "new"), 42, token));
        Require(documents.Calls == 1);
        clock.Advance(TimeSpan.FromSeconds(61));
        documents.Body = Document(old, next);
        await Pass("new active key fetched after bounded miss", async () =>
        {
            await boundary.ReadAsync(Issue(newKey, "new"), 42, token);
            Require(documents.Calls == 2);
        });
        await Pass("old passive key remains valid only while published", () => boundary.ReadAsync(original, 42, token));
        await Deny("known kid with attacker signature never refreshes", () => boundary.ReadAsync(Issue(attacker, "old"), 42, token));
        Require(documents.Calls == 2);
        foreach (string name in new[] { "issuer", "algorithm", "type", "missing kid", "malformed", "oversized" })
        {
            string value = name switch
            {
                "issuer" => Issue(oldKey, "old", issuer: Issuer + "/"),
                "algorithm" => Issue(oldKey, "old", algorithm: SecurityAlgorithms.RsaSha512),
                "type" => Issue(oldKey, "old", type: "JWT"),
                "missing kid" => Issue(oldKey, ""),
                "malformed" => "not-a-token",
                _ => new string('a', 8193)
            };
            await Deny("rotation reject-only selector: " + name, () => boundary.ReadAsync(value, 42, token));
            Require(documents.Calls == 2);
        }
        await Deny("resource remains verified by existing boundary", () => boundary.ReadAsync(Issue(oldKey, "old", audience: Resource + "/"), 42, token));
        await Deny("scope remains verified by existing boundary", () => boundary.ReadAsync(Issue(oldKey, "old", scope: "other"), 42, token));
        await Pass("token URL headers do not select JWKS endpoint", async () =>
        {
            await boundary.ReadAsync(Issue(oldKey, "old", hostileHeaders: true), 42, token);
            Require(documents.Calls == 2 && documents.Address == Configuration().JwksEndpoint);
        });
        source.Denied = true;
        await Deny("rotation never bypasses current local Work denial", () => boundary.ReadAsync(original, 42, token), localDenial: true);
        source.Denied = false;
        clock.Advance(TimeSpan.FromMinutes(10));
        documents.Body = Document(next);
        await Deny("atomic replacement removes old signing key", () => boundary.ReadAsync(original, 42, token));
        await Pass("replacement key still verifies", () => boundary.ReadAsync(Issue(newKey, "new"), 42, token));
        Require(documents.Calls == 3);
        await Pass("parallel unknown-kid storm has no extra fetch", async () =>
        {
            await Task.WhenAll(Enumerable.Range(0, 20).Select(async i =>
            {
                try { await cache.GetAsync("unknown-" + i, token); throw new Exception("Expected denial."); }
                catch (ConnectorAuthenticationFailure) { }
            }));
            Require(documents.Calls == 3);
        });
        clock.Advance(TimeSpan.FromMinutes(10));
        documents.Fail = true;
        await Deny("expired cache fails closed on retrieval error", () => boundary.ReadAsync(Issue(newKey, "new"), 42, token));
        int failedCalls = documents.Calls;
        await Deny("failure cooldown suppresses repeated fetch", () => boundary.ReadAsync(Issue(newKey, "new"), 42, token));
        Require(documents.Calls == failedCalls);
        documents.Fail = false;
        clock.Advance(TimeSpan.FromSeconds(61));
        await Pass("refresh recovers after cooldown", () => boundary.ReadAsync(Issue(newKey, "new"), 42, token));

        foreach (string body in new[]
        {
            "not-json PRIVATE", "{}", "{\"keys\":[]}", new string('a', 32769),
            Document(old, old), Document(Public(oldKey, "", use: "sig")),
            Document(Public(oldKey, "old", use: "enc")), Document(Public(oldKey, "old", algorithm: "RS512")),
            Document(JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(oldKey.ExportParameters(true)) { KeyId = "old" })),
            Document(old).Replace("\"keys\":", "\"keys\":[],\"keys\":", StringComparison.Ordinal)
        })
        {
            var bad = new Documents { Body = body };
            var rejected = new CustomerSigningKeyCache(Configuration(), bad, clock);
            await Deny("unsafe JWKS rejected before identity/source", () => rejected.GetAsync("old", token));
            Require(bad.Calls == 1);
        }
        await Pass("caller cancellation propagates without fetch", async () =>
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            int before = documents.Calls;
            try { await cache.GetAsync("new", cancelled.Token); throw new Exception("Expected cancellation."); }
            catch (OperationCanceledException) { }
            Require(documents.Calls == before);
        });
        await Pass("retrieval has a real five-second deadline and cancellation", async () =>
        {
            var blocked = new Documents { Block = true };
            var waiting = new CustomerSigningKeyCache(Configuration(), blocked);
            try { await waiting.GetAsync("old", token); throw new Exception("Expected timeout."); }
            catch (ConnectorAuthenticationFailure) { }
            await blocked.Completion.Task.WaitAsync(token);
            Require(blocked.Calls == 1 && blocked.Cancelled);
        });
        Console.WriteLine($"PASS {checks} rotation checks; real RSA/JWT, synthetic JWKS and local status source only.");
        return checks;
    }

    private static OAuthDiscoverySummary Configuration() => new CustomerOAuthDiscoveryPreflight(Issuer).Review(JsonSerializer.Serialize(new
    {
        issuer = Issuer, authorization_endpoint = Issuer + "/authorize", token_endpoint = Issuer + "/token", jwks_uri = Issuer + "/certs",
        code_challenge_methods_supported = new[] { "S256" }, grant_types_supported = new[] { "authorization_code" },
        response_types_supported = new[] { "code" }, token_endpoint_auth_methods_supported = new[] { "none" },
        scopes_supported = new[] { ConnectorReadBoundary.ReadScope }
    }));

    private static JsonWebKey Public(RSA key, string kid, string use = "sig", string algorithm = "RS256")
    {
        JsonWebKey value = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(key.ExportParameters(false)) { KeyId = kid });
        value.Use = use;
        value.Alg = algorithm;
        return value;
    }

    private static string Document(params JsonWebKey[] keys) => JsonSerializer.Serialize(new { keys });

    private static string Issue(RSA key, string kid, string issuer = Issuer, string audience = Resource,
        string scope = ConnectorReadBoundary.ReadScope, string type = "at+jwt", string algorithm = "RS256", bool hostileHeaders = false) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer, Audience = audience, TokenType = type,
            Claims = new Dictionary<string, object> { ["sub"] = "linked", ["jti"] = "ephemeral", ["scope"] = scope },
            NotBefore = DateTime.UtcNow.AddMinutes(-1), Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(key) { KeyId = kid }, algorithm),
            AdditionalHeaderClaims = hostileHeaders ? new Dictionary<string, object>
            { ["jku"] = "https://attacker.example/jwks", ["x5u"] = "http://127.0.0.1/private" } : null
        });

    private static void Require(bool value)
    {
        if (!value) throw new Exception("Rotation assertion failed.");
    }

    private sealed class Clock : TimeProvider
    {
        private long _now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _now;

        public void Advance(TimeSpan amount) => _now += amount.Ticks;
    }

    private sealed class Documents : ICustomerJwksDocumentSource
    {
        public string Body { get; set; } = "{}";
        public bool Fail { get; set; }
        public bool Block { get; set; }
        public bool Cancelled { get; private set; }
        public int Calls { get; private set; }
        public Uri? Address { get; private set; }
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> ReadAsync(Uri pinnedAddress, CancellationToken token)
        {
            try
            {
                Calls++;
                Address = pinnedAddress;
                if (Fail) throw new InvalidOperationException("PRIVATE provider failure");
                if (Block) await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Body;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { Cancelled = true; throw; }
            finally { Completion.TrySetResult(true); }
        }
    }

    private sealed class StatusSource : ICustomerWorkStatusSource
    {
        public bool Denied { get; set; }
        public int Calls { get; set; }

        public Task<WorkStatusResult> ReadAuthorizedAsync(ConnectorIdentity identity, long workId, CancellationToken token)
        {
            Calls++;
            if (Denied || identity.Issuer != Issuer || identity.Resource != Resource || identity.Subject != "linked" || workId != 42)
                throw new WorkReadFailure("Connector Work read access is not authorized.");
            return Task.FromResult(new WorkStatusResult("42", "7", "Ready", DateTimeOffset.UtcNow));
        }
    }
}
