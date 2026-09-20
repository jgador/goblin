using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
        var gitEnvironment = new Dictionary<string, string>
        {
            ["HOME"] = home,
            ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = "/dev/null"
        };
        string branch = repository.Grant!.Branch;
        ExecutionObservation? runtimeOutcome = null;
        ExecutionSession? runtimeSession = null;
        try
        {
            string bundle = Path.Combine(root, "input.bundle");
            await RepositoryClient.DownloadAsync(input.Work.Attempts[^1].Id, bundle);
            await GitAsync(root, gitEnvironment, "clone", "--branch", branch, "--", bundle, checkout);
            await GitAsync(checkout, gitEnvironment, "remote", "set-url", "origin", "https://github.com/" + repository.Repository + ".git");
            await GitAsync(checkout, gitEnvironment, "config", "user.name", repository.GitAuthorName);
            await GitAsync(checkout, gitEnvironment, "config", "user.email", repository.GitAuthorEmail);
            await using (var codex = new CodexClient(new()
            {
                Home = home,
                CodexHome = codexHome,
                Workspace = checkout,
                Command = "codex",
                RepositoryExecution = true
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
                    await GitAsync(checkout, gitEnvironment, "commit", "-m", "Goblin Work " + input.Work.Id.ToString(CultureInfo.InvariantCulture));
                await RepositoryClient.SubmitAsync(input.Work.Attempts[^1].Id, branch, checkout, "publish");
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
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.Environment.Clear();
        foreach ((string? key, string? value) in environment) info.Environment[key] = value;
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
        char[] buffer = new char[4096];
        while (await reader.ReadAsync(buffer) > 0) { }
    }
    private static void Emit(ExecutionObservation observation) => Console.WriteLine("GOBLIN_RESULT " + JsonSerializer.Serialize(observation, ExecutionFiles.Json));
}
