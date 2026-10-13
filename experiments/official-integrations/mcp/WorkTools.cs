using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Goblin.Mcp.Prototype;

[McpServerToolType]
public sealed class WorkTools
{
    private readonly WorkStatusReader _reader;

    public WorkTools(WorkStatusReader reader)
    {
        _reader = reader;
    }

    [McpServerTool(Name = "goblin_get_work_status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Read status and version for an explicitly authorized Goblin Work ID. Returns no history or credentials.")]
    public async Task<WorkStatusResult> GetStatusAsync([Description("Positive decimal Goblin Work ID.")] string workId,
        CancellationToken cancellationToken)
    {
        if (!long.TryParse(workId, NumberStyles.None, CultureInfo.InvariantCulture, out long id) || id <= 0)
            throw new McpException("A positive decimal Work ID is required.");
        try { return await _reader.ReadAsync(id, cancellationToken); }
        catch (WorkReadFailure error) { throw new McpException(error.Message); }
    }
}
