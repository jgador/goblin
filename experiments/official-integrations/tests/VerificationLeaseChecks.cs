using System.Security.Cryptography;
using System.Text.Json;
using Goblin.Mcp.Prototype;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

internal static class VerificationLeaseChecks
{
    private const string Issuer = "https://identity.customer.example/realm";
    private const string Resource = "https://goblin.customer.example/mcp";

    public static async Task<int> RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        CancellationToken token = deadline.Token;
        using RSA rsa = RSA.Create(2048), next = RSA.Create(2048), attacker = RSA.Create(2048);
        var clock = new Clock();
        var documents = new Documents { Body = Document((rsa, "old")) };
        var cache = new CustomerSigningKeyCache(Configuration(), documents, clock);
        var source = new LocalStatus();
        var boundary = new RotatingConnectorReadBoundary(cache, Resource, source);
        var admin = new CustomerSigningKeyAdministration(cache, new Owner());
        string valid = Issue(rsa, "old");
        int checks = 0;

        async Task Check(string name, Func<Task> action)
        {
            await action();
            checks++;
            Console.WriteLine("PASS " + name);
        }

        async Task Deny(Func<Task> action, bool sourceReached = false)
        {
            int before = source.Calls;
            try { await action(); throw new Exception("Expected lease denial."); }
            catch (WorkReadFailure error)
            {
                Require(error.Message == "Connector Work read access is not authorized." && error.InnerException is null);
            }
            Require(source.Calls == before + (sourceReached ? 1 : 0));
        }

        RotatingConnectorIdentity identity = await boundary.AuthenticateAsync(valid, token);
        await Check("rotating authentication verifies identity without Work access", () =>
        {
            Require(identity.Issuer == Issuer && identity.Resource == Resource && identity.Subject == "linked" && source.Calls == 0);
            return Task.CompletedTask;
        });
        await Check("leased identity rechecks local grants on every read", async () =>
        {
            await boundary.ReadAsync(identity, 42, token);
            await boundary.ReadAsync(identity, 42, token);
            source.Denied = true;
            await Deny(() => boundary.ReadAsync(identity, 42, token), sourceReached: true);
            source.Denied = false;
            Require(source.Calls == 3);
        });
        await Check("owner invalidation after authentication denies a new Work read", async () =>
        {
            await admin.InvalidateAsync(token);
            await Deny(() => boundary.ReadAsync(identity, 42, token));
        });
        await Check("fresh verification can trust still-published provider key after flush", async () =>
        {
            identity = await boundary.AuthenticateAsync(valid, token);
            await boundary.ReadAsync(identity, 42, token);
            Require(documents.Calls == 2);
        });
        await Check("invalidation between key selection and signature validation is denied", async () =>
        {
            CustomerSigningKeyLease lease = await cache.GetLeaseAsync("old", token);
            await admin.InvalidateAsync(token);
            await Deny(() => boundary.VerifyAsync(valid, lease, token));
        });
        await Check("invalidation after real signature validation prevents identity binding", async () =>
        {
            CustomerSigningKeyLease lease = await cache.GetLeaseAsync("old", token);
            var verifier = new ConnectorReadBoundary(Issuer, Resource, lease.PublicKey, source);
            ConnectorIdentity verified = await verifier.AuthenticateAsync(valid, token);
            await admin.InvalidateAsync(token);
            await Deny(() => Task.FromResult(boundary.CompleteAuthentication(verified, verifier, lease, token)));
        });
        identity = await boundary.AuthenticateAsync(valid, token);
        await Check("identity cannot move to a different boundary sharing the cache", async () =>
        {
            var other = new RotatingConnectorReadBoundary(cache, Resource, source);
            await Deny(() => other.ReadAsync(identity, 42, token));
        });
        await Check("lease cannot move to another cache with matching issuer and keys", async () =>
        {
            var otherCache = new CustomerSigningKeyCache(Configuration(), new Documents { Body = documents.Body });
            CustomerSigningKeyLease otherLease = await otherCache.GetLeaseAsync("old", token);
            await Deny(() => boundary.VerifyAsync(valid, otherLease, token));
        });
        await Check("ten-minute cache age invalidates a reused verified identity", async () =>
        {
            clock.Advance(TimeSpan.FromMinutes(10));
            await Deny(() => boundary.ReadAsync(identity, 42, token));
            identity = await boundary.AuthenticateAsync(valid, token);
            await boundary.ReadAsync(identity, 42, token);
        });
        await Check("successful key replacement retires older leases even with overlapping keys", async () =>
        {
            clock.Advance(TimeSpan.FromSeconds(61));
            documents.Body = Document((rsa, "old"), (next, "new"));
            await cache.GetAsync("new", token);
            await Deny(() => boundary.ReadAsync(identity, 42, token));
            identity = await boundary.AuthenticateAsync(valid, token);
            await boundary.ReadAsync(identity, 42, token);
        });
        foreach (string invalid in new[] { Issue(attacker, "old"), Issue(rsa, "old", scope: "other"),
            Issue(rsa, "old", audience: Resource + "/"), Issue(rsa, "old", type: "JWT"),
            Issue(rsa, "old", expired: true) })
            await Check("leases never replace signature audience scope type or expiry checks", () => Deny(() => boundary.AuthenticateAsync(invalid, token)));
        await Check("caller cancellation precedes leased Work source access", async () =>
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            int before = source.Calls;
            try { await boundary.ReadAsync(identity, 42, cancelled.Token); throw new Exception("Expected cancellation."); }
            catch (OperationCanceledException) { }
            Require(source.Calls == before);
        });
        await Check("invalidation does not retroactively stop an already-authorized source read", async () =>
        {
            source.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<WorkStatusResult> inFlight = boundary.ReadAsync(identity, 42, token);
            await source.Started.Task.WaitAsync(token);
            await admin.InvalidateAsync(token);
            source.Release.SetResult(true);
            Require((await inFlight).WorkId == "42");
            await Deny(() => boundary.ReadAsync(identity, 42, token));
        });
        Console.WriteLine($"PASS {checks} verification-lease checks; real RSA/JWT, test grants/cache/owner only.");
        return checks;
    }

    private static OAuthDiscoverySummary Configuration() => new(Issuer, new Uri(Issuer + "/authorize"),
        new Uri(Issuer + "/token"), new Uri(Issuer + "/certs"));

    private static string Document(params (RSA Key, string Kid)[] keys) => JsonSerializer.Serialize(new
    {
        keys = keys.Select(k => JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(k.Key.ExportParameters(false)) { KeyId = k.Kid }))
    });

    private static string Issue(RSA key, string kid, string scope = ConnectorReadBoundary.ReadScope,
        string audience = Resource, string type = "at+jwt", bool expired = false) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer, Audience = audience, TokenType = type,
            Claims = new Dictionary<string, object> { ["sub"] = "linked", ["jti"] = "ephemeral", ["scope"] = scope },
            NotBefore = DateTime.UtcNow.AddMinutes(-10), Expires = DateTime.UtcNow.AddMinutes(expired ? -1 : 10),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(key) { KeyId = kid }, SecurityAlgorithms.RsaSha256)
        });

    private static void Require(bool value)
    {
        if (!value) throw new Exception("Verification-lease assertion failed.");
    }

    private sealed class Clock : TimeProvider
    {
        private long _now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _now;

        public void Advance(TimeSpan amount) => _now += amount.Ticks;
    }

    private sealed class Owner : ILocalOwnerGrantAuthority
    {
        public Task RequireOwnerAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    }

    private sealed class Documents : ICustomerJwksDocumentSource
    {
        public string Body { get; set; } = "{}";
        public int Calls { get; private set; }

        public Task<string> ReadAsync(Uri pinnedAddress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(Body);
        }
    }

    private sealed class LocalStatus : ICustomerWorkStatusSource
    {
        public bool Denied { get; set; }
        public int Calls { get; private set; }
        public TaskCompletionSource<bool>? Release { get; set; }
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<WorkStatusResult> ReadAuthorizedAsync(ConnectorIdentity identity, long workId, CancellationToken token)
        {
            Calls++;
            if (Denied || identity.Issuer != Issuer || identity.Subject != "linked" || identity.Resource != Resource || workId != 42)
                throw new WorkReadFailure("Connector Work read access is not authorized.");
            if (Release is not null) { Started.TrySetResult(true); await Release.Task.WaitAsync(token); }
            return new WorkStatusResult("42", "7", "Ready", DateTimeOffset.UtcNow);
        }
    }
}
