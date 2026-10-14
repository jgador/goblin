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
        RotatingConnectorIdentity identity = await AuthenticateAsync(accessToken, token);
        return await ReadAsync(identity, workId, token);
    }

    public async Task<RotatingConnectorIdentity> AuthenticateAsync(string accessToken, CancellationToken token)
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
        CustomerSigningKeyLease lease = await _keys.GetLeaseAsync(kid, token);
        return await VerifyAsync(accessToken, lease, token);
    }

    // Internal phases make reject-only pre/post verification gates testable;
    // application callers can acquire identity only via AuthenticateAsync.
    internal async Task<RotatingConnectorIdentity> VerifyAsync(string accessToken, CustomerSigningKeyLease lease, CancellationToken token)
    {
        lease.RequireCurrent(_keys, token);
        var verifier = new ConnectorReadBoundary(_keys.Issuer, _resource, lease.PublicKey, _source);
        ConnectorIdentity identity = await verifier.AuthenticateAsync(accessToken, token);
        return CompleteAuthentication(identity, verifier, lease, token);
    }

    internal RotatingConnectorIdentity CompleteAuthentication(ConnectorIdentity identity, ConnectorReadBoundary verifier,
        CustomerSigningKeyLease lease, CancellationToken token)
    {
        lease.RequireCurrent(_keys, token);
        if (identity.Issuer != _keys.Issuer || identity.Resource != _resource ||
            verifier.Issuer != _keys.Issuer || verifier.Resource != _resource)
            throw new ConnectorAuthenticationFailure();
        return new(this, identity, verifier, lease);
    }

    public async Task<WorkStatusResult> ReadAsync(RotatingConnectorIdentity identity, long workId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (identity is null || !ReferenceEquals(identity.Owner, this)) throw new ConnectorAuthenticationFailure();
        identity.Lease.RequireCurrent(_keys, token);
        // No database/source await under the cache lock. Invalidation after this
        // check cannot retroactively stop an already-started authorized read.
        return await identity.Verifier.ReadAsync(identity.Verified, workId, token);
    }
}

public sealed class RotatingConnectorIdentity
{
    internal RotatingConnectorIdentity(RotatingConnectorReadBoundary owner, ConnectorIdentity verified,
        ConnectorReadBoundary verifier, CustomerSigningKeyLease lease)
    {
        Owner = owner;
        Verified = verified;
        Verifier = verifier;
        Lease = lease;
    }

    internal RotatingConnectorReadBoundary Owner { get; }
    internal ConnectorIdentity Verified { get; }
    internal ConnectorReadBoundary Verifier { get; }
    internal CustomerSigningKeyLease Lease { get; }
    public string Issuer => Verified.Issuer;
    public string Subject => Verified.Subject;
    public string Resource => Verified.Resource;
    public DateTimeOffset ExpiresAt => Verified.ExpiresAt;
}
