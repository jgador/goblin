using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Env = Goblin.Contracts.Configuration.EnvironmentVariables;

namespace Goblin.Execution;

// Owns the process boundary for Git commands executed inside a repository
// workspace. Callers provide the complete environment; ambient credentials and
// repository hooks are never inherited by accident.
public static class GitRepositoryProcess
{
    public static Dictionary<string, string> CreateEnvironment() =>
        CreateEnvironment(Environment.GetEnvironmentVariable(Env.Home) ?? "/runtime/home");

    public static Dictionary<string, string> CreateEnvironment(string home) => new()
    {
        [Env.Home] = home,
        [Env.Path] = Environment.GetEnvironmentVariable(Env.Path) ?? "/usr/bin:/bin",
        [Env.GitTerminalPrompt] = "0",
        [Env.GitConfigNosystem] = "1",
        [Env.GitConfigGlobal] = "/dev/null"
    };

    public static async Task<string> RunAsync(string directory, IReadOnlyDictionary<string, string> environment,
        CancellationToken token, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.Environment.Clear();
        foreach (KeyValuePair<string, string> entry in environment) start.Environment[entry.Key] = entry.Value;
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("core.hooksPath=/dev/null");
        foreach (string argument in arguments) start.ArgumentList.Add(argument);

        using Process process = Process.Start(start) ?? throw new IOException("Repository operation failed.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(token);
        Task<string> error = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(token);
            await error;
            if (process.ExitCode != 0) throw new IOException("Repository operation failed.");
            return await output;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}
