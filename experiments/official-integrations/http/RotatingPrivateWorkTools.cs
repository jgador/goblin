using System.ComponentModel;
using System.Globalization;
using Goblin.Mcp.Prototype;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Goblin.Mcp.Http.Prototype;

[McpServerToolType]
public sealed class RotatingPrivateWorkTools
{
    private readonly IHttpContextAccessor _context;
    private readonly RotatingConnectorReadBoundary _boundary;

    public RotatingPrivateWorkTools(IHttpContextAccessor context, RotatingConnectorReadBoundary boundary)
    {
        _context = context;
        _boundary = boundary;
    }

    [McpServerTool(Name = "goblin_get_work_status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Read status for a Goblin Work ID authorized by this installation. Returns no history or credentials.")]
    public async Task<WorkStatusResult> GetStatusAsync(string workId, CancellationToken cancellationToken)
    {
        if (!long.TryParse(workId, NumberStyles.None, CultureInfo.InvariantCulture, out long id) || id <= 0 ||
            _context.HttpContext?.Items[PrivateMcpHttpAdapter.IdentityKey] is not RotatingConnectorIdentity identity)
            throw new McpException("Connector Work read access is not authorized.");
        // Retain the verified lease, not a bearer token or reconstructed claims.
        // The boundary rechecks its cache lease and local grants on every call.
        try { return await _boundary.ReadAsync(identity, id, cancellationToken); }
        catch (WorkReadFailure) { throw new McpException("Connector Work read access is not authorized."); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { throw new McpException("Work status is unavailable."); }
    }
}
