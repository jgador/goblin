using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Env = Goblin.Contracts.Configuration.EnvironmentVariables;

namespace Goblin.Integrations.GitHub;

// Owns the isolated process boundary for gh and git. Connection state and
// repository workflows consume commands without owning process mechanics.
internal sealed class GitHubCommandRunner
{
    private readonly string _command;
    private readonly string _profile;

    internal GitHubCommandRunner(string command, string profile)
    {
        _command = command;
        _profile = profile;
    }

    internal Task<string> RunCliAsync(string[] arguments, CancellationToken token,
        string? profile = null, Action<string>? progress = null) =>
        RunAsync(_command, arguments, profile ?? _profile, token, progress);

    internal Task<string> RunGitAsync(string directory, string[] arguments, CancellationToken token) =>
        RunAsync("git", ["-c", "core.hooksPath=/dev/null", "-c", "credential.helper=", "-c", "credential.helper=!gh auth git-credential", .. arguments],
            _profile, token, workingDirectory: directory);

    private static async Task<string> RunAsync(string command, string[] arguments, string profile, CancellationToken token,
        Action<string>? progress = null, string? workingDirectory = null)
    {
        bool watchdog = OperatingSystem.IsLinux();
        var info = new ProcessStartInfo(watchdog ? "/usr/bin/timeout" : command)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? profile
        };
        if (watchdog)
            foreach (string prefix in new[] { "--kill-after=5s", progress is null ? "120s" : "900s", command })
                info.ArgumentList.Add(prefix);
        info.Environment.Clear();
        foreach ((string, string) pair in new[]
        {
            (Env.Path, Environment.GetEnvironmentVariable(Env.Path) ?? "/usr/bin:/bin"),
            (Env.Home, profile),
            (Env.GhConfigDir, profile),
            (Env.GhPromptDisabled, "1"),
            (Env.GhNoUpdateNotifier, "1"),
            (Env.NoColor, "1"),
            (Env.LcAll, "C"),
            (Env.GitTerminalPrompt, "0"),
            (Env.GitConfigNosystem, "1"),
            (Env.GitConfigGlobal, "/dev/null")
        }) info.Environment[pair.Item1] = pair.Item2;
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(progress is null ? TimeSpan.FromMinutes(2) : TimeSpan.FromMinutes(15));
        using Process process = Process.Start(info) ?? throw new GitHubFailure();
        process.StandardInput.Close();
        Task<string> output = ReadAsync(process.StandardOutput, null), errors = ReadAsync(process.StandardError, progress);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            throw new GitHubFailure();
        }
        string result = await output;
        await errors;
        if (process.ExitCode != 0) throw new GitHubFailure();
        return result;
    }

    private static async Task<string> ReadAsync(StreamReader reader, Action<string>? progress)
    {
        var text = new StringBuilder();
        while (await reader.ReadLineAsync() is { } line)
        {
            progress?.Invoke(line);
            if (text.Length < 4 * 1024 * 1024) text.AppendLine(line);
        }
        return text.ToString();
    }
}
