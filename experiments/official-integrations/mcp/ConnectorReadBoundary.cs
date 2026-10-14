using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Goblin.Mcp.Prototype;

// Verified transport identity is evidence, not a local Work permission.
public sealed class ConnectorIdentity
{
    internal ConnectorIdentity(string issuer, string subject, string resource, string tokenId, DateTimeOffset expiresAt)
    {
        Issuer = issuer;
        Subject = subject;
        Resource = resource;
        TokenId = tokenId;
        ExpiresAt = expiresAt;
    }

    public string Issuer { get; }
    public string Subject { get; }
    public string Resource { get; }
    public string TokenId { get; }
    public DateTimeOffset ExpiresAt { get; }
}

// Implement inside the customer's persistence boundary. Local authorization
// and the minimal Work projection must share one database statement/snapshot.
public interface ICustomerWorkStatusSource
{
    Task<WorkStatusResult> ReadAuthorizedAsync(ConnectorIdentity identity, long workId, CancellationToken token);
}

public sealed class ConnectorAuthenticationFailure : WorkReadFailure
{
    public ConnectorAuthenticationFailure() : base("Connector Work read access is not authorized.") { }
}

// Isolated resource-server experiment. No token issuance, public endpoint,
// discovery or claim of OpenAI identity support is provided by this component.
public sealed class ConnectorReadBoundary
{
    public const string ReadScope = "goblin.work.status.read";
    private readonly string _issuer;
    private readonly string _resource;
    private readonly TokenValidationParameters _validation;
    private readonly JsonWebTokenHandler _handler = new() { MaximumTokenSizeInBytes = 8192, MapInboundClaims = false };
    private readonly ICustomerWorkStatusSource _source;

    public string Issuer => _issuer;
    public string Resource => _resource;

    public ConnectorReadBoundary(string issuer, string resource, RsaSecurityKey publicKey,
        ICustomerWorkStatusSource source)
    {
        ValidateAddress(issuer);
        ValidateAddress(resource);
        // Copy public material only. Never select signing keys from a token's
        // jku/x5u/jwk headers or retain a caller's mutable key object.
        RSAParameters parameters = publicKey.Rsa?.ExportParameters(false) ?? publicKey.Parameters;
        if (parameters.D is not null)
            throw new ArgumentException("Configure a public RSA signing key.");
        var key = new RsaSecurityKey(new RSAParameters
        {
            Modulus = parameters.Modulus?.ToArray(),
            Exponent = parameters.Exponent?.ToArray()
        })
        {
            KeyId = publicKey.KeyId
        };
        if (key.KeySize < 2048)
            throw new ArgumentException("Configure a public RSA signing key of at least 2048 bits.");
        _issuer = issuer;
        _resource = resource;
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _validation = new TokenValidationParameters
        {
            ValidIssuer = issuer,
            ValidateIssuer = true,
            ValidAudience = resource,
            ValidateAudience = true,
            RequireAudience = true,
            IgnoreTrailingSlashWhenValidatingAudience = false,
            IssuerSigningKey = key,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ValidTypes = ["at+jwt"],
            RequireExpirationTime = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
            // Do not retain raw bearer tokens in identity objects or exceptions.
            SaveSigninToken = false,
            IncludeTokenOnFailedValidation = false,
            LogTokenId = false
        };
    }

    public async Task<WorkStatusResult> ReadAsync(string accessToken, long workId, CancellationToken token)
    {
        ConnectorIdentity identity = await AuthenticateAsync(accessToken, token);
        return await ReadAsync(identity, workId, token);
    }

    public async Task<ConnectorIdentity> AuthenticateAsync(string accessToken, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(accessToken) || accessToken.Length > 8192)
            throw new ConnectorAuthenticationFailure();
        TokenValidationResult verified;
        try { verified = await _handler.ValidateTokenAsync(accessToken, _validation).WaitAsync(token); }
        catch (Exception error) when (error is SecurityTokenException or ArgumentException)
        {
            throw new ConnectorAuthenticationFailure();
        }
        if (!verified.IsValid || verified.SecurityToken is not JsonWebToken jwt ||
            !jwt.TryGetPayloadValue("sub", out string? subject) || string.IsNullOrWhiteSpace(subject) ||
            !jwt.TryGetPayloadValue("jti", out string? tokenId) || string.IsNullOrWhiteSpace(tokenId) ||
            !jwt.TryGetPayloadValue("scope", out string? scope) || string.IsNullOrWhiteSpace(scope) ||
            !scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(ReadScope, StringComparer.Ordinal))
            throw new ConnectorAuthenticationFailure();
        return new ConnectorIdentity(_issuer, subject, _resource, tokenId, new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero));
    }

    public async Task<WorkStatusResult> ReadAsync(ConnectorIdentity identity, long workId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (identity.Issuer != _issuer || identity.Resource != _resource || identity.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new ConnectorAuthenticationFailure();
        if (workId <= 0) throw Denied();
        try { return await _source.ReadAuthorizedAsync(identity, workId, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { throw Denied(); }
    }

    private static WorkReadFailure Denied() => new("Connector Work read access is not authorized.");

    private static void ValidateAddress(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Configure an exact HTTPS issuer and resource identifier.");
    }
}
