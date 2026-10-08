using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Env = Goblin.Contracts.Configuration.EnvironmentVariables;

namespace Goblin.Integrations.Slack;

internal sealed class SlackSetupCommandRunner
{
    private const int MaximumOutputLength = 1024 * 1024;
    private readonly string _active;
    private readonly string _profile;
    private readonly string _project;
    private readonly string _executable;

    internal SlackSetupCommandRunner(string active)
    {
        _active = active;
        _profile = Path.Combine(active, "profile");
        _project = Path.Combine(active, "project");
        _executable = Path.Combine(active, "slack");
    }

    internal async Task<string> RunAsync(IEnumerable<string> arguments, CancellationToken token)
    {
        SuppressSensitiveLogs();
        var start = new ProcessStartInfo(_executable)
        {
            WorkingDirectory = _project,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false
        };
        start.Environment.Clear();
        start.Environment[Env.Home] = _active;
        start.Environment[Env.TmpDir] = _active;
        start.Environment[Env.SlackDisableTelemetry] = "true";
        start.Environment[Env.Path] = "/usr/local/bin:/usr/bin:/bin";
        foreach (string argument in arguments.Concat(["--config-dir", _profile, "--no-color", "--skip-update"]))
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new SlackFailure();
        process.StandardInput.Close();
        Task<string> output = ReadBoundedAsync(process.StandardOutput, token);
        Task<string> errors = ReadBoundedAsync(process.StandardError, token);
        try
        {
            Task exited = process.WaitForExitAsync(token);
            Task reads = Task.WhenAll(output, errors);
            await Task.WhenAny(exited, reads);
            if (reads.IsFaulted) await reads;
            await exited;
            string text = await output + "\n" + await errors;
            if (process.ExitCode != 0) throw new SlackFailure();
            return text;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            try { await Task.WhenAll(output, errors); }
            catch (OperationCanceledException) { }
        }
    }

    private void SuppressSensitiveLogs()
    {
        // The pinned CLI logs command arguments, including ticket/challenge
        // values. Cover a UTC midnight boundary for this short-lived setup.
        string logs = Path.Combine(_profile, "logs");
        SlackCredentialStore.PrivateDirectory(logs);
        foreach (int offset in new[] { -1, 0, 1 })
        {
            string date = DateTime.UtcNow.AddDays(offset).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            string path = Path.Combine(logs, "slack-debug-" + date + ".log");
            if (new FileInfo(path).LinkTarget == "/dev/null") continue;
            if (File.Exists(path)) File.Delete(path);
            File.CreateSymbolicLink(path, "/dev/null");
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        char[] buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer, token)) > 0)
        {
            if (text.Length + count > MaximumOutputLength) throw new SlackFailure();
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }
}
