using System.Globalization;
using System.Text.RegularExpressions;

namespace Goblin.Mcp.Prototype;

// Experimental process configuration only. Never forwarded to agent execution.
public sealed class PrototypeOptions
{
    public const string OriginVariable = "GOBLIN_MCP_ORIGIN";
    public const string SessionVariable = "GOBLIN_MCP_SESSION";
    public const string WorkIdsVariable = "GOBLIN_MCP_READ_WORK_IDS";
    private readonly HashSet<long> _workIds;
    internal string Session { get; }
    public Uri Origin { get; }

    public PrototypeOptions(string origin, string session, IEnumerable<long> workIds)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/" ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.Host is "localhost" or "127.0.0.1" or "[::1]")))
            throw new ArgumentException("Configure a fixed HTTPS Goblin origin or loopback HTTP origin.");
        if (!Regex.IsMatch(session, "^[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Configure a valid local Goblin session.");
        _workIds = new HashSet<long>(workIds);
        if (_workIds.Count == 0 || _workIds.Any(id => id <= 0))
            throw new ArgumentException("Explicit positive Work read grants are required.");
        Origin = uri;
        Session = session;
    }

    public bool CanRead(long workId) => workId > 0 && _workIds.Contains(workId);

    public static PrototypeOptions Read(Func<string, string?> read)
    {
        string[] ids = (read(WorkIdsVariable) ?? "").Split(',', StringSplitOptions.TrimEntries);
        if (ids.Any(id => !long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out long value) || value <= 0))
            throw new ArgumentException("Configure explicit Work read grants.");
        return new(read(OriginVariable) ?? "", read(SessionVariable) ?? "",
            ids.Select(id => long.Parse(id, CultureInfo.InvariantCulture)));
    }
}
