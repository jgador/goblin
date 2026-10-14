using System.Security.Cryptography;
using Goblin.Mcp.Prototype;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

internal static class ConnectorBoundaryChecks
{
    public static async Task<int> RunAsync()
    {
        using RSA issuerKey = RSA.Create(2048);
        using RSA attackerKey = RSA.Create(2048);
        var signing = new SigningCredentials(new RsaSecurityKey(issuerKey), SecurityAlgorithms.RsaSha256);
        var grants = new LocalGrants();
        int reads = 0, checks = 0;
        var boundary = new ConnectorReadBoundary("https://identity.customer.example/", "https://goblin.customer.example/mcp",
            new RsaSecurityKey(issuerKey.ExportParameters(false)), new TestGrantStatusSource(grants,
                (id, _) => { reads++; return Task.FromResult(new WorkStatusResult(id.ToString(), "7", "Ready", DateTimeOffset.UtcNow)); }));
        string valid = Issue(signing);
        await boundary.ReadAsync(valid, 42, default);
        Require(reads == 1 && grants.Calls == 1);
        checks++;

        async Task Deny(string name, string credential, long workId = 42, bool authenticationFailure = true)
        {
            int before = reads, previousGrants = grants.Calls;
            try { await boundary.ReadAsync(credential, workId, default); throw new Exception("Expected authorization denial."); }
            catch (WorkReadFailure error)
            {
                Require(error.Message == "Connector Work read access is not authorized.");
                Require(!error.ToString().Contains(credential, StringComparison.Ordinal) || credential.Length == 0);
            }
            Require(reads == before && (!authenticationFailure || grants.Calls == previousGrants));
            Console.WriteLine("PASS " + name);
            checks++;
        }

        await Deny("wrong issuer", Issue(signing, issuer: "https://another.customer.example/"));
        await Deny("wrong resource audience", Issue(signing, audience: "https://another.customer.example/mcp"));
        await Deny("audience trailing slash is not normalized", Issue(signing, audience: "https://goblin.customer.example/mcp/"));
        await Deny("invalid signature", Issue(new SigningCredentials(new RsaSecurityKey(attackerKey), SecurityAlgorithms.RsaSha256)));
        await Deny("unsupported signing algorithm", Issue(new SigningCredentials(new RsaSecurityKey(issuerKey), SecurityAlgorithms.RsaSha512)));
        await Deny("unsigned token rejected", Issue(null));
        await Deny("missing expiration rejected", Issue(signing, omitExpiration: true));
        await Deny("identity token type rejected", Issue(signing, type: "JWT"));
        await Deny("expired token", Issue(signing, expires: DateTime.UtcNow.AddMinutes(-5), notBefore: DateTime.UtcNow.AddMinutes(-10)));
        await Deny("future token", Issue(signing, notBefore: DateTime.UtcNow.AddMinutes(5)));
        await Deny("missing exact read scope", Issue(signing, scope: "goblin.work.status.read.extra"));
        await Deny("missing subject", Issue(signing, subject: ""));
        await Deny("missing token identity", Issue(signing, tokenId: ""));
        await Deny("malformed bearer rejected", "not-a-token");
        await Deny("oversized bearer rejected", new string('a', 8193));
        await Deny("unauthorized Work rejected before source read", valid, 43, false);
        await Deny("unlinked subject rejected", Issue(signing, subject: "unlinked"), authenticationFailure: false);
        grants.Revoked = true;
        await Deny("revoked local grant rechecked on same token", valid, authenticationFailure: false);
        grants.Revoked = false;
        grants.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await Deny("expired local grant rejected", valid, authenticationFailure: false);
        grants.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        grants.Unavailable = true;
        await Deny("local grant storage failure is closed and sanitized", valid, authenticationFailure: false);
        Console.WriteLine($"PASS {checks} connector checks; real RSA/JWT validation, in-memory local grants and Work source.");
        return checks;
    }

    private static string Issue(SigningCredentials? signing, string issuer = "https://identity.customer.example/",
        string audience = "https://goblin.customer.example/mcp", string scope = ConnectorReadBoundary.ReadScope,
        string subject = "linked-subject", string tokenId = "local-token-id", string type = "at+jwt",
        DateTime? expires = null, DateTime? notBefore = null, bool omitExpiration = false)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = new Dictionary<string, object> { ["sub"] = subject, ["jti"] = tokenId, ["scope"] = scope },
            IssuedAt = DateTime.UtcNow.AddMinutes(-1),
            NotBefore = notBefore ?? DateTime.UtcNow.AddMinutes(-1),
            Expires = omitExpiration ? null : expires ?? DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = signing,
            TokenType = type
        };
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(descriptor);
    }

    private static void Require(bool value)
    {
        if (!value) throw new Exception("Connector security assertion failed.");
    }

    private sealed class LocalGrants : ITestWorkReadGrants
    {
        public bool Revoked { get; set; }
        public bool Unavailable { get; set; }
        public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddMinutes(10);
        public int Calls { get; private set; }

        public Task<bool> CanReadAsync(ConnectorIdentity identity, long workId, CancellationToken token)
        {
            Calls++;
            if (Unavailable) throw new InvalidOperationException("PRIVATE upstream configuration details");
            return Task.FromResult(!Revoked && ExpiresAt > DateTimeOffset.UtcNow && workId == 42 &&
                identity.Issuer == "https://identity.customer.example/" && identity.Subject == "linked-subject" &&
                identity.Resource == "https://goblin.customer.example/mcp" && identity.TokenId == "local-token-id");
        }
    }
}
