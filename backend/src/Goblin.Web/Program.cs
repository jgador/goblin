using System;
using Goblin.Web;
using Microsoft.AspNetCore.Builder;
using Env = Goblin.Contracts.Configuration.EnvironmentVariables;

if (args.Length > 0 && args[0] == "--execute")
{
    Environment.ExitCode = await Goblin.Execution.ExecutionWorker.RunAsync(args[1..]);
    return;
}
if (args.Length is 1 or 2 && args[0] == "--workspace-files")
{
    Environment.ExitCode = Goblin.Execution.WorkspaceFiles.Run(args.Length == 2 ? args[1] : null);
    return;
}
if (args.Length == 1 && args[0] == "--sandbox-execute")
{
    Environment.ExitCode = await Goblin.Execution.SandboxWorker.RunAsync();
    return;
}
if (args.Length > 0 && args[0] == "--repository")
{
    Environment.ExitCode = await Goblin.Execution.RepositoryClient.RunAsync(args[1..]);
    return;
}

string portValue = Environment.GetEnvironmentVariable(Env.GoblinPort) ?? "8787";
if (!int.TryParse(portValue, out int port) || port is < 1 or > 65535)
    throw new ArgumentException("GOBLIN_PORT must be a valid port.");
string origin = Environment.GetEnvironmentVariable(Env.GoblinPublicOrigin) ?? $"http://localhost:{port}";
string host = Environment.GetEnvironmentVariable(Env.GoblinHost) ?? "127.0.0.1";
await using WebApplication app = await GoblinApplication.CreateAsync(new()
{
    DataDirectory = Environment.GetEnvironmentVariable(Env.GoblinDataDir) ?? ".goblin-auth",
    PasswordHashFile = Environment.GetEnvironmentVariable(Env.GoblinPasswordHashFile),
    PublicOrigin = origin,
    AllowInsecureHttp = string.Equals(Environment.GetEnvironmentVariable(Env.GoblinAllowInsecureHttp), "true", StringComparison.OrdinalIgnoreCase),
    ListenUrl = $"http://{host}:{port}",
    EnableWork = !string.Equals(Environment.GetEnvironmentVariable(Env.GoblinWorkEnabled), "false", StringComparison.OrdinalIgnoreCase),
    HeadlampUrl = Environment.GetEnvironmentVariable(Env.GoblinHeadlampUrl),
    VictoriaLogsUrl = Environment.GetEnvironmentVariable(Env.GoblinVictorialogsUrl),
    ConfigureCodex = options => options with { Command = Environment.GetEnvironmentVariable(Env.GoblinCodexCommand) ?? options.Command }
});
Console.WriteLine($"Goblin: {origin}");
Console.WriteLine("Workspace access: use your Goblin password.");
await app.RunAsync();
