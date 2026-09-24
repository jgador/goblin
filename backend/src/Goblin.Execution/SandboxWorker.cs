using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts.Runtime;
using Goblin.Core.Repositories;
using Goblin.Core.Work;
using Goblin.Integrations.Codex;

namespace Goblin.Execution;

public static class SandboxWorker
{
    public static async Task<int> RunAsync()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        const string root = "/workspace", checkout = "/workspace/repository";
        string state = Path.Combine(root, ".goblin");
        Directory.CreateDirectory(state);
        WorkerInput input = (await ExecutionFiles.ReadAsync<WorkerInput>("/run/input/input.json"))!;
        WorkSnapshot work = input.Work;
        int allocation = work.Attempts[^1].WorkspaceNumber;
        string home = "/runtime/home", codexHome = "/runtime/codex";
        Directory.CreateDirectory(home); Directory.CreateDirectory(codexHome);
        File.Copy("/run/credentials/auth.json", Path.Combine(codexHome, "auth.json"), true);
        File.SetUnixFileMode(Path.Combine(codexHome, "auth.json"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var environment = new Dictionary<string, string>
        {
            ["HOME"] = home,
            ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = "/dev/null"
        };
        while (true)
        {
            AttemptSnapshot attempt = work.Attempts[^1];
            string prefix = ClaimPrefix(state, attempt.Id, attempt.TurnNumber);
            // Each runtime turn has its own create-only fence, even in a retained pod.
            try
            {
                using var gate = new FileStream(prefix + ".claimed", FileMode.CreateNew, FileAccess.Write);
            }
            catch (IOException) { Emit(new(ObservationKind.Uncertain, Failure: FailureKind.HostUnavailable) { TurnNumber = attempt.TurnNumber }); return 1; }
            ExecutionObservation outcome;
            ExecutionSession? session = null;
            string stage = "Restore";
            try
            {
                RepositoryChange repository = attempt.Target.Repository!;
                string branch = repository.Grant!.Branch;
                if (!Directory.Exists(checkout))
                {
                    WorkspaceCheckpoint? restored = await RepositoryClient.RestoreAsync(work, root);
                    if (restored is null)
                    {
                        string bundle = Path.Combine(state, "input.bundle");
                        await RepositoryClient.DownloadAsync(attempt.Id, bundle);
                        await GitAsync(root, environment, "clone", "--branch", branch, "--", bundle, checkout);
                    }
                    else
                    {
                        if ((await GitAsync(checkout, environment, "rev-parse", "HEAD")).Trim() != restored.CommitSha) throw new IOException("Checkpoint mismatch.");
                        await GitAsync(checkout, environment, "checkout", "-B", branch, restored.CommitSha);
                    }
                }
                await PrepareCheckoutAsync(checkout, environment, repository);
                string baseline = (await GitAsync(checkout, environment, "rev-parse", "HEAD")).Trim();
                stage = "Setup memory";
                string setupEnvironment = RepositorySetupWorkspace.EnvironmentIdentity(input.SandboxImage ?? "unknown");
                var setupWorkspace = new RepositorySetupWorkspace(checkout, environment);
                RepositorySetupMemory[] memories = await setupWorkspace.SelectAsync(await RepositoryClient.SetupMemoryAsync(attempt.Id), setupEnvironment, CancellationToken.None);
                stage = "Runtime";
                await using (var codex = new CodexClient(new() { Home = home, CodexHome = codexHome, Workspace = checkout, Command = "codex", RepositoryExecution = true }))
                {
                    outcome = await new CodexWorkRunner(codex).RunAsync(work, true, value =>
                    {
                        session = value.Session ?? session;
                        Console.WriteLine("GOBLIN_PROGRESS " + JsonSerializer.Serialize(value with { TurnNumber = attempt.TurnNumber }, ExecutionFiles.Json));
                        return Task.CompletedTask;
                    }, CancellationToken.None, memories);
                }
                stage = "Setup verification";
                VerifiedRepositorySetup[] observations = await setupWorkspace.VerifyAsync(outcome.Setup ?? [], CancellationToken.None);
                // Candidate commands are private worker data, not Work messages,
                // Kubernetes logs, or runtime progress exposed to the user.
                outcome = outcome with { Setup = null };
                stage = "Checkpoint";
                WorkspaceFiles.EnsureQuiescent();
                await GitAsync(checkout, environment, "add", "--all");
                if (!string.IsNullOrWhiteSpace(await GitAsync(checkout, environment, "diff", "--cached", "--name-only")))
                    await GitAsync(checkout, environment, "commit", "-m", "Goblin Work " + work.Id.ToString(CultureInfo.InvariantCulture));
                stage = "Publish";
                await RepositoryClient.SubmitAsync(attempt.Id, branch, checkout, "publish");
                string commit = (await GitAsync(checkout, environment, "rev-parse", "HEAD")).Trim();
                await File.WriteAllTextAsync(Path.Combine(state, "changes.patch"), await GitAsync(checkout, environment, "diff", baseline, commit));
                string archive = Path.Combine("/tmp", Guid.NewGuid().ToString("N") + ".tar.gz");
                stage = "Archive";
                WorkspaceCheckpoint saved;
                try { WorkspaceFiles.Pack(root, archive); saved = await RepositoryClient.SaveAsync(work, commit, archive); }
                finally { File.Delete(archive); }
                if (observations.Length > 0)
                {
                    stage = "Save setup memory";
                    await RepositoryClient.SaveSetupMemoryAsync(attempt.Id, new(attempt.TurnNumber, saved.Id, setupEnvironment, observations));
                }
                outcome = outcome with { CheckpointId = saved.Id, ArtifactReference = "https://github.com/" + repository.Repository + "/tree/" + branch };
            }
            catch (TimeoutException) { outcome = new(ObservationKind.Failed, session, Failure: FailureKind.TimedOut); }
            catch { outcome = new(ObservationKind.Failed, session, Failure: FailureKind.ExecutionFailed); }
            outcome = outcome with { TurnNumber = attempt.TurnNumber };
            if (outcome.Kind is ObservationKind.Failed or ObservationKind.Uncertain)
                await ExecutionFiles.WriteAsync(prefix + ".failure.json", new { stage, failure = outcome.Failure });
            await ExecutionFiles.WriteAsync(prefix + ".result.json", outcome);
            Emit(outcome);
            if (outcome.Kind != ObservationKind.Paused || outcome.ReleaseWorkspace) return 0;
            // A semantic keep decision holds the environment. Polling transports an
            // already-claimed next turn; it never decides to execute or retry Work.
            while (true)
            {
                await Task.Delay(1000);
                WorkSnapshot? next;
                try { next = await RepositoryClient.CurrentAsync(attempt.Id); }
                catch { continue; }
                if (next is null) continue;
                AttemptSnapshot candidate = next.Attempts[^1];
                if (candidate.Id != attempt.Id || candidate.WorkspaceNumber != allocation || candidate.Status is AttemptStatus.Cancelled or AttemptStatus.CancellationRequested) return 0;
                if (candidate.TurnNumber > attempt.TurnNumber && candidate.Status == AttemptStatus.Starting)
                { work = next; break; }
            }
        }
    }
    public static string ClaimPrefix(string state, long attemptId, int turnNumber) => Path.Combine(state,
        "attempt-" + attemptId.ToString(CultureInfo.InvariantCulture) + "-turn-" + turnNumber.ToString(CultureInfo.InvariantCulture));

    public static async Task PrepareCheckoutAsync(string checkout, Dictionary<string, string> environment, RepositoryChange repository)
    {
        // Start the authorized branch at the surviving local HEAD. Git carries the
        // index, dirty files and untracked files across this switch, including edits
        // made before a failed runtime could publish or save a checkpoint.
        await GitAsync(checkout, environment, "checkout", "-B", repository.Grant!.Branch, "HEAD");
        await GitAsync(checkout, environment, "remote", "set-url", "origin", "https://github.com/" + repository.Repository + ".git");
        await GitAsync(checkout, environment, "config", "user.name", repository.GitAuthorName);
        await GitAsync(checkout, environment, "config", "user.email", repository.GitAuthorEmail);
    }

    private static async Task<string> GitAsync(string directory, Dictionary<string, string> environment, params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        info.Environment.Clear(); foreach (KeyValuePair<string, string> entry in environment) info.Environment[entry.Key] = entry.Value;
        info.ArgumentList.Add("-c"); info.ArgumentList.Add("core.hooksPath=/dev/null");
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using Process process = Process.Start(info)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(); Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); await error;
        if (process.ExitCode != 0) throw new IOException("Repository operation failed.");
        return await output;
    }
    private static void Emit(ExecutionObservation observation) => Console.WriteLine("GOBLIN_RESULT " + JsonSerializer.Serialize(observation, ExecutionFiles.Json));
}
