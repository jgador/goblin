using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Integrations.ChatGpt;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Goblin.Tests;

public sealed class ChatGptAuthenticationTests
{
    internal const string Issuer = "https://identity.example.test/realms/goblin";
    internal const string Origin = "https://goblin.example.test";

    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expiry")]
    [InlineData("future")]
    [InlineData("subject")]
    [InlineData("id")]
    [InlineData("scope")]
    [InlineData("type")]
    [InlineData("signature")]
    public async Task TokenMustProveIdentityResourceLifetimeAndScope(string invalid)
    {
        using var fixture = new Tokens();
        var auth = new ChatGptAuthentication(new(Issuer, Origin), fixture);
        await Assert.ThrowsAsync<ChatGptAuthenticationFailure>(() => auth.AuthenticateAsync(fixture.Token(invalid), default));
    }

    [Fact]
    public async Task ValidIdentityRemainsBoundToItsVerifierAndKeyGeneration()
    {
        using var fixture = new Tokens();
        var auth = new ChatGptAuthentication(new(Issuer, Origin), fixture);
        ChatGptIdentity identity = await auth.AuthenticateAsync(fixture.Token(), default);
        Assert.Equal("subject-one", identity.Subject);
        auth.RequireCurrent(identity, default);
        var other = new ChatGptAuthentication(new(Issuer, Origin), fixture);
        Assert.Throws<ChatGptAuthenticationFailure>(() => other.RequireCurrent(identity, default));
        auth.InvalidateKeys(default);
        Assert.Throws<ChatGptAuthenticationFailure>(() => auth.RequireCurrent(identity, default));
        _ = await auth.AuthenticateAsync(fixture.Token(), default);
        Assert.Equal(2, fixture.Reads);
    }

    [Fact]
    public async Task UnknownKeyRefreshIsBoundedAndCannotSelectAnEndpoint()
    {
        using var fixture = new Tokens();
        var auth = new ChatGptAuthentication(new(Issuer, Origin), fixture);
        for (int i = 0; i < 5; i++)
            await Assert.ThrowsAsync<ChatGptAuthenticationFailure>(() => auth.AuthenticateAsync(fixture.Token("signature"), default));
        Assert.Equal(1, fixture.Reads);
        Assert.Equal(new Uri(Issuer + "/protocol/openid-connect/certs"), fixture.Address);
    }

    [Theory]
    [InlineData("http://identity.example.test/realms/goblin", Origin)]
    [InlineData(Issuer, "http://goblin.example.test")]
    [InlineData(Issuer + "/", Origin)]
    [InlineData(Issuer + "?secret=wrong", Origin)]
    [InlineData("https://identity.example.test/other", Origin)]
    public void ConfigurationRequiresCanonicalHttpsEndpoints(string issuer, string origin) =>
        Assert.ThrowsAny<Exception>(() => new ChatGptConfiguration(issuer, origin));

    [Fact]
    public async Task ExpiredSigningKeysFailClosedDuringAnOutageAndRecoveredKeysRetireOldLeases()
    {
        using var fixture = new Tokens();
        var clock = new Clock();
        var keys = new SigningKeyCache(new ChatGptConfiguration(Issuer, Origin).Provider, fixture, clock);
        SigningKeyLease first = await keys.GetLeaseAsync("current", default);
        clock.Advance(TimeSpan.FromMinutes(10));
        fixture.Unavailable = true;
        await Assert.ThrowsAsync<ChatGptAuthenticationFailure>(() => keys.GetLeaseAsync("current", default));
        Assert.Throws<ChatGptAuthenticationFailure>(() => first.RequireCurrent(keys, default));
        fixture.Unavailable = false;
        await Assert.ThrowsAsync<ChatGptAuthenticationFailure>(() => keys.GetLeaseAsync("current", default));
        Assert.Equal(2, fixture.Reads);
        clock.Advance(TimeSpan.FromMinutes(1));
        SigningKeyLease recovered = await keys.GetLeaseAsync("current", default);
        recovered.RequireCurrent(keys, default);
        Assert.Throws<ChatGptAuthenticationFailure>(() => first.RequireCurrent(keys, default));
        Assert.Equal(3, fixture.Reads);
    }

    [Fact]
    public void ConfigurationBoundsTheDurableGrantIdentity()
    {
        Assert.Throws<ArgumentException>(() => new ChatGptConfiguration("https://identity.example.test/realms/" + new string('a', 500), Origin));
    }

    [Fact]
    public void ProviderTemplateRequiresExactChatGptCallbacksAndContainsNoSharedSecret()
    {
        var configuration = new ChatGptConfiguration(Issuer, Origin);
        Assert.Throws<ArgumentException>(() => KeycloakProfile.Create(configuration, "https://chatgpt.com/*"));
        Assert.Throws<ArgumentException>(() => KeycloakProfile.Create(configuration, "https://other.example.test/callback"));
        using JsonDocument template = JsonDocument.Parse(KeycloakProfile.Create(configuration, "https://chatgpt.com/connector/oauth/test-callback"));
        JsonElement client = template.RootElement.GetProperty("clients")[1];
        Assert.False(client.GetProperty("publicClient").GetBoolean());
        Assert.False(client.TryGetProperty("secret", out _));
        Assert.False(client.GetProperty("directAccessGrantsEnabled").GetBoolean());
        Assert.Equal("S256", client.GetProperty("attributes").GetProperty("pkce.code.challenge.method").GetString());
        Assert.Equal("true", client.GetProperty("attributes").GetProperty("access.token.header.type.rfc9068").GetString());
        Assert.Contains(client.GetProperty("protocolMappers").EnumerateArray(), mapper =>
            mapper.GetProperty("protocolMapper").GetString() == "oidc-sub-mapper" &&
            mapper.GetProperty("config").GetProperty("access.token.claim").GetString() == "true");
        Assert.Empty(JsonDocument.Parse(KeycloakProfile.Create(configuration, null)).RootElement.GetProperty("clients")[1].GetProperty("redirectUris").EnumerateArray());
    }

    [Theory]
    [InlineData("redirect")]
    [InlineData("oversize")]
    [InlineData("wrong-issuer")]
    [InlineData("foreign-jwks")]
    [InlineData("pkce")]
    [InlineData("duplicate")]
    public async Task DiscoveryFailsClosedForUnsafeOrIncompatibleMetadata(string invalid)
    {
        using var documents = new HttpsDocuments(new(Issuer, Origin), new Handler(invalid));
        await Assert.ThrowsAnyAsync<Exception>(() => documents.VerifyDiscoveryAsync(default));
    }

    [Fact]
    public async Task DiscoveryAndKeysUseOnlyConfiguredHttpsAddresses()
    {
        using var handler = new Handler("");
        using var documents = new HttpsDocuments(new(Issuer, Origin), handler);
        _ = await documents.VerifyDiscoveryAsync(default);
        Assert.Single(handler.Addresses);
        await Assert.ThrowsAsync<ChatGptAuthenticationFailure>(() => documents.ReadAsync(new Uri("https://other.example.test/keys"), default));
        Assert.Single(handler.Addresses);
    }

    internal sealed class Tokens : IJwksDocumentSource, IDisposable
    {
        private readonly RSA _rsa = RSA.Create(2048);
        private readonly RSA _other = RSA.Create(2048);
        public int Reads { get; private set; }
        public bool Unavailable { get; set; }
        public Uri? Address { get; private set; }

        public string Token(string invalid = "", string? resource = null)
        {
            var key = new RsaSecurityKey(invalid == "signature" ? _other : _rsa) { KeyId = invalid == "signature" ? "other" : "current" };
            var claims = new Dictionary<string, object>
            {
                ["sub"] = invalid == "subject" ? "" : "subject-one",
                ["jti"] = invalid == "id" ? "" : Guid.NewGuid().ToString("N"),
                ["scope"] = invalid == "scope" ? "other.read" : ChatGptConfiguration.ReadScope
            };
            return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = invalid == "issuer" ? Issuer + "-other" : Issuer,
                Audience = invalid == "audience" ? "https://other.example.test/mcp" : resource ?? Origin + "/mcp",
                Claims = claims,
                TokenType = invalid == "type" ? "JWT" : "at+jwt",
                NotBefore = DateTime.UtcNow.AddMinutes(invalid == "future" ? 1 : -10),
                Expires = DateTime.UtcNow.AddMinutes(invalid == "expiry" ? -1 : 5),
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256)
            });
        }

        public Task<string> ReadAsync(Uri pinnedAddress, CancellationToken token)
        {
            Reads++;
            if (Unavailable) throw new IOException("Disposable provider is unavailable.");
            Address = pinnedAddress;
            JsonWebKey key = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(_rsa) { KeyId = "current" });
            return Task.FromResult(JsonSerializer.Serialize(new { keys = new[] { new { kty = key.Kty, kid = "current", n = key.N, e = key.E, alg = "RS256", use = "sig" } } }));
        }

        public void Dispose() { _rsa.Dispose(); _other.Dispose(); }
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan duration) => _ticks += duration.Ticks;
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly string _invalid;
        public List<Uri> Addresses { get; } = [];

        public Handler(string invalid) => _invalid = invalid;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Addresses.Add(request.RequestUri!);
            Assert.Null(request.Headers.Authorization);
            string json = JsonSerializer.Serialize(new
            {
                issuer = _invalid == "wrong-issuer" ? Issuer + "-other" : Issuer,
                authorization_endpoint = Issuer + "/protocol/openid-connect/auth",
                token_endpoint = Issuer + "/protocol/openid-connect/token",
                jwks_uri = _invalid == "foreign-jwks" ? "https://other.example.test/keys" : Issuer + "/protocol/openid-connect/certs",
                code_challenge_methods_supported = new[] { _invalid == "pkce" ? "plain" : "S256" },
                grant_types_supported = new[] { "authorization_code" },
                response_types_supported = new[] { "code" },
                token_endpoint_auth_methods_supported = new[] { "client_secret_basic" },
                scopes_supported = new[] { ChatGptConfiguration.ReadScope }
            });
            if (_invalid == "oversize") json = new string(' ', 16385) + json;
            if (_invalid == "duplicate") json = json.Replace("\"issuer\":", "\"issuer\":\"wrong\",\"issuer\":", StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(_invalid == "redirect" ? HttpStatusCode.Redirect : HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
