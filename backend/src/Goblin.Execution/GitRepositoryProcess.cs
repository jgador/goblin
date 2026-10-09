using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

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
        CancellationToken token, params string[] arguments) =>
        await RunAsync(directory, environment, DefaultTimeout, token, arguments);

    public static async Task<string> RunAsync(string directory, IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout, CancellationToken token, params string[] arguments)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        bool watchdog = OperatingSystem.IsLinux() && File.Exists("/usr/bin/timeout");
        var start = new ProcessStartInfo(watchdog ? "/usr/bin/timeout" : "git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.Environment.Clear();
        foreach (KeyValuePair<string, string> entry in environment) start.Environment[entry.Key] = entry.Value;
        if (watchdog)
        {
            start.ArgumentList.Add("--kill-after=5s");
            start.ArgumentList.Add(timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "s");
            start.ArgumentList.Add("git");
        }
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("core.hooksPath=/dev/null");
        foreach (string argument in arguments) start.ArgumentList.Add(argument);

        using Process process = Process.Start(start) ?? throw new IOException("Repository operation failed.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(watchdog ? timeout + TimeSpan.FromSeconds(6) : timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            await error;
            if (watchdog && process.ExitCode is 124 or 137)
                throw new TimeoutException("Repository operation timed out.");
            if (process.ExitCode != 0) throw new IOException("Repository operation failed.");
            return await output;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Repository operation timed out.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            await Task.WhenAll(output, error);
        }
    }
}
