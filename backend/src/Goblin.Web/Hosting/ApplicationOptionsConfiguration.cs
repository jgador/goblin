using System;
using Env = Goblin.Contracts.Configuration.EnvironmentVariables;

namespace Goblin.Web;

// Keep process-environment access at the executable boundary while making the
// mapping to application behavior explicit and independently testable.
internal static class ApplicationOptionsConfiguration
{
    public static ApplicationOptions Read(Func<string, string?> value)
    {
        ArgumentNullException.ThrowIfNull(value);

        string portValue = value(Env.GoblinPort) ?? "8787";
        if (!int.TryParse(portValue, out int port) || port is < 1 or > 65535)
            throw new ArgumentException("GOBLIN_PORT must be a valid port.");

        string origin = value(Env.GoblinPublicOrigin) ?? $"http://localhost:{port}";
        string host = value(Env.GoblinHost) ?? "127.0.0.1";
        return new()
        {
            DataDirectory = value(Env.GoblinDataDir) ?? ".goblin-auth",
            PasswordHashFile = value(Env.GoblinPasswordHashFile),
            PublicOrigin = origin,
            AllowInsecureHttp = string.Equals(value(Env.GoblinAllowInsecureHttp), "true", StringComparison.OrdinalIgnoreCase),
            ListenUrl = $"http://{host}:{port}",
            EnableWork = !string.Equals(value(Env.GoblinWorkEnabled), "false", StringComparison.OrdinalIgnoreCase),
            HeadlampUrl = value(Env.GoblinHeadlampUrl),
            VictoriaLogsUrl = value(Env.GoblinVictorialogsUrl),
            ConfigureCodex = options => options with { Command = value(Env.GoblinCodexCommand) ?? options.Command }
        };
    }
}
