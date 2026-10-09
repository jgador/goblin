using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
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
    private const int MaximumCapturedLength = 4 * 1024 * 1024;

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
        string output = "";
        await RunProcessAsync(directory, environment, timeout, async (reader, cancellation) =>
        {
            var text = new StringBuilder();
            char[] buffer = new char[4096];
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellation)) != 0)
            {
                if (text.Length + count > MaximumCapturedLength)
                    throw new IOException("Repository operation output is too large.");
                text.Append(buffer, 0, count);
            }
            output = text.ToString();
        }, token, arguments);
        return output;
    }

    // Full patches stay on the workspace filesystem. Publish the file only
    // after Git succeeds, preserving an existing patch if the command fails.
    public static async Task WriteOutputAsync(string directory, IReadOnlyDictionary<string, string> environment,
        string destination, CancellationToken token, params string[] arguments)
    {
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous))
                await RunProcessAsync(directory, environment, DefaultTimeout,
                    (reader, cancellation) => reader.BaseStream.CopyToAsync(file, cancellation), token, arguments);
            File.Move(temporary, destination, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private static async Task RunProcessAsync(string directory, IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout, Func<StreamReader, CancellationToken, Task> readOutput, CancellationToken token, string[] arguments)
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
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(watchdog ? timeout + TimeSpan.FromSeconds(6) : timeout);
        Task output = readOutput(process.StandardOutput, deadline.Token);
        // Upstream stderr is neither returned nor retained. Drain it with a
        // fixed buffer so it cannot block Git or exhaust worker memory.
        Task error = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
        try
        {
            var pending = new List<Task> { output, error, process.WaitForExitAsync(deadline.Token) };
            while (pending.Count > 0)
            {
                Task completed = await Task.WhenAny(pending);
                await completed;
                pending.Remove(completed);
            }
            if (watchdog && process.ExitCode is 124 or 137)
                throw new TimeoutException("Repository operation timed out.");
            if (process.ExitCode != 0) throw new IOException("Repository operation failed.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Repository operation timed out.");
        }
        finally
        {
            await deadline.CancelAsync();
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* The process exited before the kill. */ }
                await process.WaitForExitAsync(CancellationToken.None);
            }
            try { await Task.WhenAll(output, error); }
            catch { /* Preserve the command, output-limit, or cancellation failure. */ }
        }
    }
}
