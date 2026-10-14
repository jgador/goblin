using System.Text.Json;
using Goblin.Mcp.Prototype;

internal static class OAuthDiscoveryChecks
{
    private const string Issuer = "https://identity.customer.example/realms/goblin";

    public static int Run()
    {
        var policy = new CustomerOAuthDiscoveryPreflight(Issuer);
        string valid = Sample();
        OAuthDiscoverySummary result = policy.Review(valid);
        Require(result.Issuer == Issuer && result.TokenEndpoint.AbsoluteUri == Issuer + "/protocol/openid-connect/token" &&
            result.JwksEndpoint.AbsoluteUri == Issuer + "/protocol/openid-connect/certs" &&
            !result.AdvertisesAuthorizationResponseIssuerIdentification);
        int checks = 1;
        Console.WriteLine("PASS synthetic customer OAuth discovery passes narrow offline policy");

        void Deny(string name, string value)
        {
            try { policy.Review(value); }
            catch (InvalidOperationException error)
            {
                Require(error.Message == "Customer OAuth discovery does not satisfy the configured preflight policy." && error.InnerException is null);
                checks++;
                Console.WriteLine("PASS " + name);
                return;
            }
            throw new Exception("Expected OAuth preflight denial.");
        }

        void Advisory(string name, string document, bool expected)
        {
            OAuthDiscoverySummary summary = policy.Review(document);
            Require(summary.AdvertisesAuthorizationResponseIssuerIdentification == expected && summary.Issuer == result.Issuer &&
                summary.AuthorizationEndpoint == result.AuthorizationEndpoint && summary.TokenEndpoint == result.TokenEndpoint &&
                summary.JwksEndpoint == result.JwksEndpoint);
            checks++;
            Console.WriteLine("PASS " + name);
        }

        Advisory("explicit false issuer advertisement stays conservative", IssAdvertisement("false"), false);
        Advisory("true issuer advertisement is reported without changing pinned endpoints", IssAdvertisement("true"), true);
        Advisory("a later missing advertisement does not inherit prior true metadata", valid, false);
        Advisory("issuer capability name is case sensitive, without optimistic fallback",
            IssAdvertisement("true").Replace("authorization_response_iss_parameter_supported", "AUTHORIZATION_RESPONSE_ISS_PARAMETER_SUPPORTED",
                StringComparison.Ordinal), false);
        foreach (string value in new[] { "null", "\"true\"", "\"false\"", "1", "0", "[]", "{}", "tru" })
            Deny("non-Boolean or malformed issuer advertisement is sanitized", IssAdvertisement(value));
        Deny("duplicate true issuer advertisements are rejected", IssAdvertisement("true", IssAdvertisement("true")));
        Deny("conflicting issuer advertisements are rejected", IssAdvertisement("false", IssAdvertisement("true")));
        Deny("issuer advertisement cannot relax exact trusted issuer", IssAdvertisement("true", Sample(issuer: Issuer + "/")));
        Deny("issuer advertisement cannot relax S256", IssAdvertisement("true", Sample(challenge: ["plain"])));
        Deny("issuer advertisement cannot allow foreign keys", IssAdvertisement("true", Sample(keys: "https://other.customer.example/keys")));

        Deny("discovery issuer is exact, without trailing slash normalization", Sample(issuer: Issuer + "/"));
        Deny("plain-only PKCE is rejected", Sample(challenge: ["plain"]));
        Deny("PKCE algorithm is case sensitive", Sample(challenge: ["s256"]));
        Deny("client-credentials-only metadata is rejected", Sample(grants: ["client_credentials"]));
        Deny("implicit-only response is rejected", Sample(responses: ["token"]));
        Deny("public-client token exchange must be advertised", Sample(authMethods: ["client_secret_basic"]));
        Deny("similar but inexact Work scope is rejected", Sample(scopes: [ConnectorReadBoundary.ReadScope + ".extra"]));
        foreach (string endpoint in new[] { "http://identity.customer.example/token", "https://other.customer.example/token",
            Issuer + "/token?secret=PRIVATE", "https://user:PRIVATE@identity.customer.example/token", Issuer + "/token#PRIVATE" })
            Deny("unsafe or foreign token endpoint is rejected without fetch", Sample(token: endpoint));
        Deny("foreign JWKS endpoint is rejected without fetch", Sample(keys: "https://other.customer.example/keys"));
        Deny("missing signing-key endpoint is rejected", Sample(keys: ""));
        Deny("duplicate metadata properties are rejected", valid.Replace("{", "{\"issuer\":\"https://evil.example/\",", StringComparison.Ordinal));
        Deny("missing required advertised PKCE is rejected", "{\"issuer\":\"" + Issuer + "\"}");
        Deny("wrong-shaped capability array is sanitized", valid.Replace("[\"S256\"]", "\"PRIVATE\"", StringComparison.Ordinal));
        Deny("malformed metadata is sanitized", "{ PRIVATE");
        Deny("oversized metadata is rejected", new string('x', 16385));
        Deny("null discovery object is rejected", "null");
        foreach (string issuer in new[] { "http://identity.customer.example/", "https://user:PRIVATE@identity.customer.example/",
            Issuer + "?PRIVATE", Issuer + "\n", new string('x', 2049) })
        {
            try { _ = new CustomerOAuthDiscoveryPreflight(issuer); }
            catch (InvalidOperationException)
            {
                checks++;
                Console.WriteLine("PASS unsafe trusted-issuer configuration is rejected");
                continue;
            }
            throw new Exception("Expected trusted-issuer rejection.");
        }
        Console.WriteLine($"PASS {checks} OAuth metadata preflight checks; synthetic JSON only, no provider, key fetching or OAuth flow.");
        return checks;
    }

    private static string Sample(string issuer = Issuer, string? token = null, string? keys = null,
        string[]? challenge = null, string[]? grants = null, string[]? responses = null, string[]? authMethods = null, string[]? scopes = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["issuer"] = issuer,
            ["authorization_endpoint"] = Issuer + "/protocol/openid-connect/auth",
            ["token_endpoint"] = token ?? Issuer + "/protocol/openid-connect/token",
            ["jwks_uri"] = keys ?? Issuer + "/protocol/openid-connect/certs",
            ["code_challenge_methods_supported"] = challenge ?? ["S256"],
            ["grant_types_supported"] = grants ?? ["authorization_code"],
            ["response_types_supported"] = responses ?? ["code"],
            ["token_endpoint_auth_methods_supported"] = authMethods ?? ["none"],
            ["scopes_supported"] = scopes ?? [ConnectorReadBoundary.ReadScope]
        });

    private static string IssAdvertisement(string jsonValue, string? document = null) =>
        (document ?? Sample()).Insert(1, "\"authorization_response_iss_parameter_supported\":" + jsonValue + ",");

    private static void Require(bool value)
    {
        if (!value) throw new Exception("OAuth discovery preflight assertion failed.");
    }
}
