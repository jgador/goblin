using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;
using Goblin.Integrations.Codex;

namespace Goblin.Execution;

public static class SandboxWorker
{
    public static async Task<int> RunAsync()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Agent sandboxes require Linux.");
        const string root = "/workspace";
        string resultFile = Path.Combine(root, "goblin-result.json");
        ExecutionObservation? saved = await ExecutionFiles.ReadAsync<ExecutionObservation>(resultFile);
        if (saved is not null) { Emit(saved); return 0; }
        try { using var reservation = new FileStream(Path.Combine(root, "goblin-attempt"), FileMode.CreateNew, FileAccess.Write, FileShare.None); }
        catch (IOException)
        {
            // A replacement pod must never re-execute an uncertain attempt.
            Emit(new(ObservationKind.Uncertain, Failure: FailureKind.HostUnavailable));
            return 1;
        }
        ExecutionObservation outcome;
        WorkerInput input = (await ExecutionFiles.ReadAsync<WorkerInput>("/run/input/input.json"))!;
        RepositoryChange repository = input.Work.Attempts[^1].Target.Repository!;
        string home = "/runtime/home", codexHome = "/runtime/codex", checkout = Path.Combine(root, "repository");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(codexHome);
        File.Copy("/run/credentials/auth.json", Path.Combine(codexHome, "auth.json"), overwrite: true);
        File.SetUnixFileMode(Path.Combine(codexHome, "auth.json"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        // No token appears in remote URLs, git config, argv, or Work history.
        string askpass = "/runtime/git-askpass";
        await File.WriteAllTextAsync(askpass, "#!/bin/sh\ncase \"$1\" in *Username*) printf '%s' x-access-token ;; *) cat /run/credentials/github-token ;; esac\n");
        File.SetUnixFileMode(askpass, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var gitEnvironment = new Dictionary<string, string>
        {
            ["HOME"] = home, ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
            ["GIT_ASKPASS"] = askpass, ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_GLOBAL"] = "/dev/null"
        };
        string branch = "goblin/" + input.Work.Id.ToString("N") + "/" + input.Work.Attempts[^1].Id.ToString("N");
        ExecutionObservation? runtimeOutcome = null;
        ExecutionSession? runtimeSession = null;
        try
        {
            await GitAsync(root, gitEnvironment, "clone", "--", "https://github.com/" + repository.Repository + ".git", checkout);
            // A fresh sandbox can continue a reviewed checkpoint without sharing
            // a mutable checkout with another Work item or trusting native sessions.
            var previous = input.Work.Attempts.SkipLast(1).LastOrDefault(a =>
                a.Target.Repository?.Repository == repository.Repository && input.Work.Artifacts.Any(x => x.AttemptId == a.Id));
            if (previous is null) await GitAsync(checkout, gitEnvironment, "checkout", "-b", branch);
            else await GitAsync(checkout, gitEnvironment, "checkout", "-b", branch,
                "origin/goblin/" + input.Work.Id.ToString("N") + "/" + previous.Id.ToString("N"));
            await GitAsync(checkout, gitEnvironment, "config", "user.name", repository.GitAuthorName);
            await GitAsync(checkout, gitEnvironment, "config", "user.email", repository.GitAuthorEmail);
            await using (var codex = new CodexClient(new()
            {
                Home = home, CodexHome = codexHome, Workspace = checkout, Command = "codex", RepositoryExecution = true
            }))
            {
                outcome = await new CodexWorkRunner(codex).RunAsync(input.Work, true,
                    value => { runtimeSession = value.Session ?? runtimeSession; Console.WriteLine("GOBLIN_PROGRESS " + JsonSerializer.Serialize(value, ExecutionFiles.Json)); return Task.CompletedTask; }, CancellationToken.None);
            }
            runtimeOutcome = outcome;
            if (outcome.Kind is ObservationKind.Result or ObservationKind.InputRequired)
            {
                await GitAsync(checkout, gitEnvironment, "add", "--all");
                int changes = await GitAsync(checkout, gitEnvironment, ["diff", "--cached", "--quiet"], allowDifference: true);
                if (changes == 1)
                    await GitAsync(checkout, gitEnvironment, "commit", "-m", "Goblin Work " + input.Work.Id.ToString("N"));
                await GitAsync(checkout, gitEnvironment, "push", "origin", "HEAD:refs/heads/" + branch);
                outcome = outcome with { ArtifactReference = "https://github.com/" + repository.Repository + "/tree/" + branch };
            }
        }
        catch (TimeoutException) { outcome = new(ObservationKind.Failed, runtimeSession, Failure: FailureKind.TimedOut); }
        catch { outcome = new(ObservationKind.Failed, runtimeOutcome?.Session ?? runtimeSession, Failure: FailureKind.ExecutionFailed); }
        await ExecutionFiles.WriteAsync(resultFile, outcome);
        Emit(outcome);
        return 0;
    }

    private static Task<int> GitAsync(string directory, Dictionary<string, string> environment, params string[] args) =>
        GitAsync(directory, environment, args, false);
    private static async Task<int> GitAsync(string directory, Dictionary<string, string> environment, string[] args, bool allowDifference)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true };
        info.Environment.Clear();
        foreach (var (key, value) in environment) info.Environment[key] = value;
        foreach (string arg in args) info.ArgumentList.Add(arg);
        using Process process = Process.Start(info)!;
        Task stdout = DrainAsync(process.StandardOutput), stderr = DrainAsync(process.StandardError);
        await process.WaitForExitAsync();
        await Task.WhenAll(stdout, stderr);
        if (process.ExitCode != 0 && !(allowDifference && process.ExitCode == 1)) throw new IOException("Repository operation failed.");
        return process.ExitCode;
    }
    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer) > 0) { }
    }
    private static void Emit(ExecutionObservation observation) => Console.WriteLine("GOBLIN_RESULT " + JsonSerializer.Serialize(observation, ExecutionFiles.Json));
}
