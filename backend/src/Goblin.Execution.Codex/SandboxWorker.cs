using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts.Runtime;
using Goblin.Core.GitRepositories;
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
        Dictionary<string, string> environment = GitRepositoryProcess.CreateEnvironment(home);
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
                GitRepositoryChange gitRepository = attempt.Target.GitRepository!;
                string branch = gitRepository.Grant!.Branch;
                if (!Directory.Exists(checkout))
                {
                    string bundle = Path.Combine(state, "input.bundle");
                    await GitRepositoryClient.DownloadAsync(attempt.Id, bundle);
                    await GitRepositoryProcess.RunAsync(root, environment, CancellationToken.None,
                        "clone", "--branch", branch, "--", bundle, checkout);
                }
                await PrepareCheckoutAsync(checkout, environment, gitRepository);
                string baseline = (await GitRepositoryProcess.RunAsync(checkout, environment, CancellationToken.None,
                    "rev-parse", "HEAD")).Trim();
                stage = "Setup memory";
                string setupEnvironment = GitRepositorySetupWorkspace.EnvironmentIdentity(input.SandboxImage ?? "unknown");
                var setupWorkspace = new GitRepositorySetupWorkspace(checkout, environment);
                GitRepositorySetupMemory[] memories = await setupWorkspace.SelectAsync(await GitRepositoryClient.SetupMemoryAsync(attempt.Id), setupEnvironment, CancellationToken.None);
                stage = "Runtime";
                await using (var codex = new CodexClient(new() { Home = home, CodexHome = codexHome, Workspace = checkout, Command = "codex", GitRepositoryExecution = true }))
                {
                    outcome = await new CodexWorkRunner(codex).RunAsync(work, true, value =>
                    {
                        session = value.Session ?? session;
                        Console.WriteLine("GOBLIN_PROGRESS " + JsonSerializer.Serialize(value with { TurnNumber = attempt.TurnNumber }, ExecutionFiles.Json));
                        return Task.CompletedTask;
                    }, CancellationToken.None, memories);
                }
                stage = "Setup verification";
                VerifiedGitRepositorySetup[] observations = await setupWorkspace.VerifyAsync(outcome.Setup ?? [], CancellationToken.None);
                // Candidate commands are private worker data, not Work messages,
                // Kubernetes logs, or runtime progress exposed to the user.
                outcome = outcome with { Setup = null };
                stage = "Checkpoint";
                WorkspaceFiles.EnsureQuiescent();
                await GitRepositoryProcess.RunAsync(checkout, environment, CancellationToken.None, "add", "--all");
                if (!string.IsNullOrWhiteSpace(await GitRepositoryProcess.RunAsync(checkout, environment,
                    CancellationToken.None, "diff", "--cached", "--name-only")))
                    await GitRepositoryProcess.RunAsync(checkout, environment, CancellationToken.None,
                        "commit", "-m", CommitMessage(work.Id, gitRepository));
                bool publish = gitRepository.Grant!.PublishesChanges();
                stage = publish ? "Publish" : "Verify local Git checkpoint";
                string? artifact = await GitRepositoryClient.SubmitAsync(attempt.Id, branch, checkout, publish ? GitRepositoryOperationKind.Publish : GitRepositoryOperationKind.Checkpoint);
                if (gitRepository.Grant.AllowPullRequest && outcome.Kind == ObservationKind.Result)
                    artifact = await GitRepositoryClient.SubmitAsync(attempt.Id, branch, checkout, GitRepositoryOperationKind.PullRequest);
                string commit = (await GitRepositoryProcess.RunAsync(checkout, environment, CancellationToken.None,
                    "rev-parse", "HEAD")).Trim();
                await GitRepositoryProcess.WriteOutputAsync(checkout, environment, Path.Combine(state, "changes.patch"),
                    CancellationToken.None, "diff", baseline, commit);
                stage = "Git checkpoint";
                WorkspaceCheckpoint saved = await GitRepositoryClient.SaveCheckpointAsync(work, commit);
                if (observations.Length > 0)
                {
                    stage = "Save setup memory";
                    await GitRepositoryClient.SaveSetupMemoryAsync(attempt.Id, new(attempt.TurnNumber, saved.Id, setupEnvironment, observations));
                }
                outcome = outcome with { CheckpointId = saved.Id, ArtifactReference = artifact };
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
                try { next = await GitRepositoryClient.CurrentAsync(attempt.Id); }
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

    public static string CommitMessage(long workId, GitRepositoryChange repository) =>
        "Goblin Work " + workId.ToString(CultureInfo.InvariantCulture) +
        (repository.RequestedBy is { } requester ? "\n\nRequested-by: " + requester : "");

    public static async Task PrepareCheckoutAsync(string checkout, Dictionary<string, string> environment, GitRepositoryChange gitRepository)
    {
        // Start the authorized branch at the surviving local HEAD. Git carries the
        // index, dirty files and untracked files across this switch, including edits
        // made before a failed runtime could publish or save a checkpoint.
        await GitRepositoryProcess.RunAsync(checkout, environment, CancellationToken.None,
            "checkout", "-B", gitRepository.Grant!.Branch, "HEAD");
        await GitRepositoryProcess.RunAsync(checkout, environment, CancellationToken.None,
            "remote", "set-url", "origin", "https://github.com/" + gitRepository.GitRepository + ".git");
        await GitRepositoryProcess.RunAsync(checkout, environment, CancellationToken.None,
            "config", "user.name", gitRepository.GitAuthorName);
        await GitRepositoryProcess.RunAsync(checkout, environment, CancellationToken.None,
            "config", "user.email", gitRepository.GitAuthorEmail);
    }

    private static void Emit(ExecutionObservation observation) => Console.WriteLine("GOBLIN_RESULT " + JsonSerializer.Serialize(observation, ExecutionFiles.Json));
}
