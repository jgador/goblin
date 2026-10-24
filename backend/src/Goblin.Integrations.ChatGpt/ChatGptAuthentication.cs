using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Goblin.Integrations.ChatGpt;

public sealed class ChatGptAuthenticationFailure : Exception
{
    public ChatGptAuthenticationFailure() : base("ChatGPT connection authentication is required.") { }
}

public sealed class ChatGptAuthentication
{
    private readonly ChatGptConfiguration _configuration;
    private readonly SigningKeyCache _keys;
    private readonly JsonWebTokenHandler _handler = new() { MaximumTokenSizeInBytes = 8192, MapInboundClaims = false };

    public ChatGptAuthentication(ChatGptConfiguration configuration, IJwksDocumentSource source)
    {
        _configuration = configuration;
        _keys = new SigningKeyCache(configuration.Provider, source);
    }

    public async Task<ChatGptIdentity> AuthenticateAsync(string accessToken, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (string.IsNullOrWhiteSpace(accessToken) || accessToken.Length > 8192) throw new ChatGptAuthenticationFailure();
            // Reject-only parsing cannot grant access or select a remote URL.
            JsonWebToken untrusted = _handler.ReadJsonWebToken(accessToken);
            if (untrusted.Alg != SecurityAlgorithms.RsaSha256 || untrusted.Typ != "at+jwt" || untrusted.Issuer != _configuration.Issuer)
                throw new ChatGptAuthenticationFailure();
            SigningKeyLease lease = await _keys.GetLeaseAsync(untrusted.Kid, token);
            lease.RequireCurrent(_keys, token);
            TokenValidationResult result = await _handler.ValidateTokenAsync(accessToken, new TokenValidationParameters
            {
                ValidIssuer = _configuration.Issuer,
                ValidateIssuer = true,
                ValidAudience = _configuration.Resource,
                ValidateAudience = true,
                RequireAudience = true,
                IgnoreTrailingSlashWhenValidatingAudience = false,
                IssuerSigningKey = lease.PublicKey,
                ValidateIssuerSigningKey = true,
                RequireSignedTokens = true,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ValidTypes = ["at+jwt"],
                RequireExpirationTime = true,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero,
                SaveSigninToken = false,
                IncludeTokenOnFailedValidation = false,
                LogTokenId = false
            }).WaitAsync(token);
            if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt ||
                !jwt.TryGetPayloadValue("sub", out string? subject) || !ValidClaim(subject) ||
                !jwt.TryGetPayloadValue("jti", out string? id) || !ValidClaim(id) ||
                !jwt.TryGetPayloadValue("scope", out string? scope) ||
                scope?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(ChatGptConfiguration.ReadScope, StringComparer.Ordinal) != true)
                throw new ChatGptAuthenticationFailure();
            lease.RequireCurrent(_keys, token);
            return new ChatGptIdentity(this, lease, _configuration.Issuer, subject!, _configuration.Resource,
                new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { throw new ChatGptAuthenticationFailure(); }
    }

    public void RequireCurrent(ChatGptIdentity identity, CancellationToken token)
    {
        if (!ReferenceEquals(identity.Owner, this) || identity.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new ChatGptAuthenticationFailure();
        identity.Lease.RequireCurrent(_keys, token);
    }

    // Called only by the existing owner-authenticated administration endpoint.
    public void InvalidateKeys(CancellationToken token) => _keys.Invalidate(token);

    private static bool ValidClaim(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(char.IsControl);
}

public sealed class ChatGptIdentity
{
    internal ChatGptIdentity(ChatGptAuthentication owner, SigningKeyLease lease, string issuer, string subject,
        string resource, DateTimeOffset expiresAt)
    {
        Owner = owner;
        Lease = lease;
        Issuer = issuer;
        Subject = subject;
        Resource = resource;
        ExpiresAt = expiresAt;
    }

    internal ChatGptAuthentication Owner { get; }
    internal SigningKeyLease Lease { get; }
    public string Issuer { get; }
    public string Subject { get; }
    public string Resource { get; }
    public DateTimeOffset ExpiresAt { get; }
}
