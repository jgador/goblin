namespace Goblin.Mcp.Prototype;

// Volatile cache administration, not authentication, durable key revocation,
// an MCP mutation tool, a public route or a production owner-session adapter.
public sealed class CustomerSigningKeyAdministration
{
    private readonly CustomerSigningKeyCache _cache;
    private readonly ILocalOwnerGrantAuthority _owner;

    public CustomerSigningKeyAdministration(CustomerSigningKeyCache cache, ILocalOwnerGrantAuthority owner)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    public async Task InvalidateAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await _owner.RequireOwnerAsync(deadline.Token).WaitAsync(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { throw new GrantAdministrationFailure("Local owner authorization is required."); }
        // Recheck after owner authorization; mutation is synchronous/atomic and
        // has no post-mutation cancellation that could obscure confirmation.
        _cache.Invalidate(token);
    }
}
