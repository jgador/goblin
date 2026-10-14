using System.Text;

namespace Goblin.Mcp.Prototype;

// A trusted request-scoped adapter must validate the existing local owner
// session and Origin. Never implement this using connector/profile claims.
public interface ILocalOwnerGrantAuthority
{
    Task RequireOwnerAsync(CancellationToken token);
}

// Implement approval/revocation and the local audit atomically, under Goblin's
// application transaction gate. Recheck Work existence/link policy there.
// No default or production implementation is registered by this experiment.
public interface ICustomerGrantMutationStore
{
    Task ApproveWithAuditAsync(ConnectorGrantApproval approval, CancellationToken token);

    Task RevokeWithAuditAsync(ConnectorGrantTarget target, DateTimeOffset revokedAt, CancellationToken token);
}

public sealed class ConnectorGrantTarget
{
    internal ConnectorGrantTarget(string issuer, string subject, string resource, long workId)
    {
        Issuer = issuer;
        Subject = subject;
        Resource = resource;
        WorkId = workId;
    }

    public string Issuer { get; }
    public string Subject { get; }
    public string Resource { get; }
    public long WorkId { get; }
}

public sealed class ConnectorGrantApproval
{
    internal ConnectorGrantApproval(ConnectorGrantTarget target, DateTimeOffset approvedAt, DateTimeOffset expiresAt)
    {
        Target = target;
        ApprovedAt = approvedAt;
        ExpiresAt = expiresAt;
    }

    public ConnectorGrantTarget Target { get; }
    public DateTimeOffset ApprovedAt { get; }
    public DateTimeOffset ExpiresAt { get; }
}

public sealed class GrantAdministrationFailure : Exception
{
    public GrantAdministrationFailure(string message) : base(message) { }
}

// Policy/composition prototype only, not an owner authentication mechanism,
// durable writer or MCP tool. One explicit subject and Work per operation.
public sealed class CustomerGrantAdministration
{
    private readonly string _issuer;
    private readonly string _resource;
    private readonly TimeSpan _maximumLifetime;
    private readonly ILocalOwnerGrantAuthority _owner;
    private readonly ICustomerGrantMutationStore _store;
    private readonly TimeProvider _clock;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public CustomerGrantAdministration(string issuer, string resource, TimeSpan maximumLifetime,
        ILocalOwnerGrantAuthority owner, ICustomerGrantMutationStore store, TimeProvider clock)
    {
        ValidateAddress(issuer);
        ValidateAddress(resource);
        if (maximumLifetime <= TimeSpan.Zero || maximumLifetime > TimeSpan.FromDays(7))
            throw new ArgumentException("Configure a positive grant lifetime of at most seven days.");
        _issuer = issuer;
        _resource = resource;
        _maximumLifetime = maximumLifetime;
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task ApproveAsync(string subject, long workId, DateTimeOffset expiresAt, CancellationToken token)
    {
        await RequireOwnerAsync(token);
        ConnectorGrantTarget target = Target(subject, workId);
        DateTimeOffset now = _clock.GetUtcNow();
        if (expiresAt <= now || expiresAt - now > _maximumLifetime)
            throw new GrantAdministrationFailure("Select a future expiry within the configured grant lifetime.");
        var approval = new ConnectorGrantApproval(target, now.ToUniversalTime(), expiresAt.ToUniversalTime());
        token.ThrowIfCancellationRequested();
        try { await _store.ApproveWithAuditAsync(approval, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { throw Unconfirmed(); }
    }

    public async Task RevokeAsync(string subject, long workId, CancellationToken token)
    {
        await RequireOwnerAsync(token);
        ConnectorGrantTarget target = Target(subject, workId);
        token.ThrowIfCancellationRequested();
        // Revocation never requires the connector to be logged in or its token
        // to remain valid. The store must keep already-revoked rows revoked.
        try { await _store.RevokeWithAuditAsync(target, _clock.GetUtcNow().ToUniversalTime(), token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { throw Unconfirmed(); }
    }

    private async Task RequireOwnerAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { await _owner.RequireOwnerAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { throw new GrantAdministrationFailure("Local owner authorization is required."); }
        token.ThrowIfCancellationRequested();
    }

    private ConnectorGrantTarget Target(string subject, long workId)
    {
        try
        {
            if (workId <= 0 || string.IsNullOrWhiteSpace(subject) ||
                subject.Any(char.IsControl) || StrictUtf8.GetByteCount(subject) > 256)
                throw new GrantAdministrationFailure("Select one valid subject and positive Work ID.");
            // Subject is never trimmed, case-folded, or treated as an email.
            return new(_issuer, subject, _resource, workId);
        }
        catch (EncoderFallbackException)
        {
            throw new GrantAdministrationFailure("Select one valid subject and positive Work ID.");
        }
    }

    private static void ValidateAddress(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? address) || address.Scheme != "https" ||
            address.UserInfo.Length != 0 || address.Query.Length != 0 || address.Fragment.Length != 0 ||
            StrictUtf8.GetByteCount(value) > 512)
            throw new ArgumentException("Configure a bounded exact HTTPS issuer and resource.");
    }

    private static GrantAdministrationFailure Unconfirmed() => new("Grant change could not be confirmed.");
}
