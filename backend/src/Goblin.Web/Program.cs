using System;
using Goblin.Web;
using Microsoft.AspNetCore.Builder;

if (args.Length > 0 && args[0] == "--slack-hook")
{
    // The CLI supplies --source to the manifest hook. The embedded manifest
    // does not read that directory, but the hook must accept the CLI contract.
    bool valid = args.Length == 2 ||
        args.Length == 3 && args[1] == "manifest" && args[2].StartsWith("--source=", StringComparison.Ordinal) ||
        args.Length == 4 && args[1] == "manifest" && args[2] == "--source";
    Environment.ExitCode = valid ? await Goblin.Integrations.Slack.SlackSetup.HookAsync(args[1]) : 1;
    return;
}

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
    Environment.ExitCode = await Goblin.Execution.GitRepositoryClient.RunAsync(args[1..]);
    return;
}

ApplicationOptions options = ApplicationOptionsConfiguration.Read(Environment.GetEnvironmentVariable);
await using WebApplication app = await GoblinApplication.CreateAsync(options);
Console.WriteLine($"Goblin: {options.PublicOrigin}");
Console.WriteLine("Workspace access: use your Goblin password.");
await app.RunAsync();
