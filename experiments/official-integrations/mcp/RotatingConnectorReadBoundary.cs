using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Goblin.Mcp.Prototype;

// Unregistered rotation composition. Parsing below is only a reject-only
// selector; identity is constructed by the existing cryptographic verifier.
public sealed class RotatingConnectorReadBoundary
{
    private readonly CustomerSigningKeyCache _keys;
    private readonly string _resource;
    private readonly ICustomerWorkStatusSource _source;
    private readonly JsonWebTokenHandler _handler = new() { MaximumTokenSizeInBytes = 8192, MapInboundClaims = false };

    public RotatingConnectorReadBoundary(CustomerSigningKeyCache keys, string resource, ICustomerWorkStatusSource source)
    {
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        if (!Uri.TryCreate(resource, UriKind.Absolute, out Uri? address) || address.Scheme != "https" ||
            address.UserInfo.Length != 0 || address.Query.Length != 0 || address.Fragment.Length != 0)
            throw new ArgumentException("Configure an exact HTTPS resource identifier.");
        _resource = resource;
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public async Task<WorkStatusResult> ReadAsync(string accessToken, long workId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string kid;
        try
        {
            if (string.IsNullOrWhiteSpace(accessToken) || accessToken.Length > 8192) throw new ConnectorAuthenticationFailure();
            JsonWebToken untrusted = _handler.ReadJsonWebToken(accessToken);
            if (untrusted.Alg != SecurityAlgorithms.RsaSha256 || untrusted.Typ != "at+jwt" || untrusted.Issuer != _keys.Issuer)
                throw new ConnectorAuthenticationFailure();
            kid = untrusted.Kid;
        }
        catch (Exception) { throw new ConnectorAuthenticationFailure(); }
        RsaSecurityKey key = await _keys.GetAsync(kid, token);
        var verified = new ConnectorReadBoundary(_keys.Issuer, _resource, key, _source);
        return await verified.ReadAsync(accessToken, workId, token);
    }
}
