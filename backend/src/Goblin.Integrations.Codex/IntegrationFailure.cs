using System;

namespace Goblin.Integrations.Codex;

// Only fixed, sanitized messages leave this integration. HTTP status selection
// belongs to the web adapter; runtime code knows nothing about HTTP responses.
public sealed class IntegrationFailure(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
    public static IntegrationFailure RuntimeUnavailable() => new("runtime_unavailable",
        "Codex is unavailable. Check the connection and runtime installation.");
}
