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
    internal const string Issuer = "https://goblin.example.test/";
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
        var auth = new ChatGptAuthentication(new(Origin), fixture);
        await Assert.ThrowsAsync<ChatGptAuthenticationFailure>(() => auth.AuthenticateAsync(fixture.Token(invalid), default));
    }

    [Fact]
    public async Task ValidIdentityRemainsBoundToItsVerifierAndKeyGeneration()
    {
        using var fixture = new Tokens();
        var auth = new ChatGptAuthentication(new(Origin), fixture);
        ChatGptIdentity identity = await auth.AuthenticateAsync(fixture.Token(), default);
        Assert.Equal("subject-one", identity.Subject);
        auth.RequireCurrent(identity, default);
        var other = new ChatGptAuthentication(new(Origin), fixture);
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
        var auth = new ChatGptAuthentication(new(Origin), fixture);
        for (int i = 0; i < 5; i++)
            await Assert.ThrowsAsync<ChatGptAuthenticationFailure>(() => auth.AuthenticateAsync(fixture.Token("signature"), default));
        Assert.Equal(1, fixture.Reads);
        Assert.Equal(new Uri(Origin + ChatGptConfiguration.KeysPath), fixture.Address);
    }

    [Theory]
    [InlineData("http://goblin.example.test")]
    [InlineData(Origin + "/path")]
    [InlineData(Origin + "?value=wrong")]
    [InlineData("https://user:password@goblin.example.test")]
    public void ConfigurationRequiresCanonicalHttpsEndpoints(string origin) =>
        Assert.ThrowsAny<Exception>(() => new ChatGptConfiguration(origin));

    [Fact]
    public async Task ExpiredSigningKeysFailClosedDuringAnOutageAndRecoveredKeysRetireOldLeases()
    {
        using var fixture = new Tokens();
        var clock = new Clock();
        var keys = new SigningKeyCache(new ChatGptConfiguration(Origin), fixture, clock);
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

    [Theory]
    [InlineData("https://chatgpt.com/*")]
    [InlineData("https://other.example.test/callback")]
    [InlineData("https://chatgpt.com/connector/oauth/callback\n")]
    public void CallbackRejectsWildcardsForeignHostsAndTrailingCharacters(string value) =>
        Assert.Throws<ArgumentException>(() => ChatGptCallback.Require(value));

    [Fact]
    public void CallbackAllowsExactDisplayedChatGptRedirect() =>
        ChatGptCallback.Require("https://chatgpt.com/connector/oauth/test-callback");

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

}
