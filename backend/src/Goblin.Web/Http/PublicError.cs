using System;

namespace Goblin.Web;

public sealed class PublicError(string code, string message, int status = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;

    public static PublicError RuntimeUnavailable() => new("runtime_unavailable",
        "Codex is unavailable. Retry in a moment. If this continues, restart Goblin.", 503);
}
