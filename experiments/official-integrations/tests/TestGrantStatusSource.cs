using Goblin.Mcp.Prototype;

internal interface ITestWorkReadGrants
{
    Task<bool> CanReadAsync(ConnectorIdentity identity, long workId, CancellationToken token);
}

// Test-only split store. Never compose this mock in a customer host: the real
// source must authorize and project within one database statement/snapshot.
internal sealed class TestGrantStatusSource : ICustomerWorkStatusSource
{
    private readonly ITestWorkReadGrants _grants;
    private readonly Func<long, CancellationToken, Task<WorkStatusResult>> _read;

    public TestGrantStatusSource(ITestWorkReadGrants grants, Func<long, CancellationToken, Task<WorkStatusResult>> read)
    {
        _grants = grants;
        _read = read;
    }

    public async Task<WorkStatusResult> ReadAuthorizedAsync(ConnectorIdentity identity, long workId, CancellationToken token)
    {
        if (!await _grants.CanReadAsync(identity, workId, token))
            throw new WorkReadFailure("Connector Work read access is not authorized.");
        token.ThrowIfCancellationRequested();
        return await _read(workId, token);
    }
}
