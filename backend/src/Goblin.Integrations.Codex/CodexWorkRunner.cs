using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts.Runtime;
using Goblin.Core.GitRepositories;
using Goblin.Core.Work;
using Goblin.Protocol;

namespace Goblin.Integrations.Codex;

// Durable execution has its own inputs/results. The restricted verification
// endpoint and its cancellation/size policy do not define Work execution.
public sealed class CodexWorkRunner
{
    private readonly CodexClient _codex;

    public CodexWorkRunner(CodexClient codex) => _codex = codex;

    public async Task<ExecutionObservation> RunAsync(WorkSnapshot work, bool gitRepositoryChanges,
        Func<ExecutionObservation, Task> progress, CancellationToken token, GitRepositorySetupMemory[]? setupMemory = null)
    {
        var transcript = new CodexTurnTranscript(128, 64000, "\n", countSeparators: false,
            trimResult: false, progressLimit: 4000,
            tooLarge: () => new IntegrationFailure("work_result_too_large", "The result exceeded the supported size."),
            completionError: turn => turn.Status != TurnStatus.Completed
                ? new IntegrationFailure("work_execution_failed", "Execution did not complete.") : null,
            resultError: _ => null,
            notificationError: () => new IntegrationFailure("work_execution_failed", "Execution needs attention."));
        void Disconnected() => transcript.Fail(IntegrationFailure.RuntimeUnavailable());
        _codex.Notification += transcript.Handle;
        _codex.Disconnected += Disconnected;
        try
        {
            await _codex.StartAsync(token);
            ThreadStartResponse thread = await _codex.RequestAsync<ThreadStartParams, ThreadStartResponse>("thread/start", new()
            {
                Cwd = _codex.Options.Workspace,
                Ephemeral = false,
                Model = work.Attempts[^1].Target.RequestedModel,
                ApprovalPolicy = AskForApproval.Never,
                Sandbox = gitRepositoryChanges ? SandboxMode.DangerFullAccess : SandboxMode.ReadOnly,
                BaseInstructions = "You execute a Goblin Work item. Return a JSON object with kind and text. " +
                    "Use kind result for a proposed outcome requiring human review, or input for a question that prevents progress. " +
                    "Do not claim approval or completion on behalf of the user. " +
                    "Set releaseWorkspace based on whether this conversation still needs repository compute. " +
                    "Use false when continuing interactive investigation needs the existing workspace, true when waiting for review, longer human input, or no further file access. " +
                    "A release request suspends compute while preserving files on this Work's persistent volume. PostgreSQL stores conversation and Git provenance, never workspace file archives. Respect an explicit request to keep the workspace open. " +
                    (gitRepositoryChanges ? GitRepositorySetupInstructions.Text + "Work only on the assigned repository and branch in this isolated environment. " +
                        "This Work's workspace can contain edits and local commits from earlier attempts. Inspect git status and git diff before editing; preserve unfinished changes and use the supplied Work context to continue. " +
                        "Commit locally. The approved repository grant controls publication: " +
                        (work.Attempts[^1].Target.GitRepository?.RequestedBy is { } requester
                            ? $"Include the exact trailer 'Requested-by: {requester}' in every new commit. This identifies the authenticated requester; do not invent a Co-authored-by email. " : "") +
                        $"push allowed={work.Attempts[^1].Target.GitRepository?.Grant?.AllowPush}; draft PR allowed={work.Attempts[^1].Target.GitRepository?.Grant?.AllowPullRequest}. " +
                        "Use goblin-github publish or goblin-github pull-request only when allowed. Goblin saves local checkpoints without publishing when push is not approved. " +
                        "Use goblin-github fetch to refresh origin branches before incorporating upstream changes locally. " +
                        "GitHub credentials are held by Goblin. Main and other branches cannot be published or merged through these operations. " :
                        "Use only the supplied context. Do not call tools, inspect files, browse, or run commands. " +
                        "If the latest request requires cloning, inspecting, running, or changing repository files, return kind workspace with a short reason. " +
                        "Goblin uses the enabled repository's default branch and connected GitHub commit identity. Ask only for missing repository or task information; do not ask for Git author settings. " +
                        "Do not ask the user to attach a checkout, enable network access, or change filesystem permissions. " +
                        "Otherwise answer or ask clarifying questions using the saved Work context.")
            }, token);
            transcript.SetThread(thread.Thread.Id);
            string context = JsonSerializer.Serialize(new CodexWorkContext(work.Objective, work.Messages,
                work.Decisions, work.Results, work.Artifacts)
            {
                GitRepositorySetupMemory = gitRepositoryChanges ? setupMemory ?? [] : null
            });
            // JSON Schema construction and the protocol's arbitrary outputSchema
            // payload intentionally remain dynamic; result data is a typed contract.
            var properties = new Dictionary<string, object>
            {
                ["kind"] = new { type = "string", @enum = !gitRepositoryChanges ? new[] { "result", "input", "workspace" } : ["result", "input"] },
                ["text"] = new { type = "string" },
                ["releaseWorkspace"] = new { type = "boolean" }
            };
            if (gitRepositoryChanges) properties["setup"] = GitRepositorySetupInstructions.Schema();
            JsonElement schema = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                additionalProperties = false,
                required = properties.Keys.ToArray(),
                properties
            });
            TurnStartResponse started = await _codex.RequestAsync<TurnStartParams, TurnStartResponse>("turn/start", new()
            {
                ThreadId = transcript.ThreadId!,
                Input = [new TextUserInput { Text = context }],
                Effort = work.Attempts[^1].Target.RequestedEffort,
                ApprovalPolicy = AskForApproval.Never,
                OutputSchema = schema,
                SandboxPolicy = gitRepositoryChanges ? new ExternalSandboxSandboxPolicy { NetworkAccess = NetworkAccess.Enabled }
                    : new ReadOnlySandboxPolicy { NetworkAccess = false }
            }, token);
            transcript.SetTurn(started.Turn.Id);
            var session = new ExecutionSession(thread.Model, transcript.ThreadId!, transcript.TurnId!);
            await progress(new(ObservationKind.Running, session) { TurnNumber = work.Attempts[^1].TurnNumber });
            Task<string> completed = transcript.Completion.Task.WaitAsync(TimeSpan.FromMinutes(30), token);
            string? reported = null;
            while (!completed.IsCompleted)
            {
                await Task.WhenAny(completed, Task.Delay(TimeSpan.FromSeconds(1), token));
                token.ThrowIfCancellationRequested();
                string? next = transcript.LatestProgress;
                if (!string.IsNullOrWhiteSpace(next) && next != reported)
                {
                    await progress(new(ObservationKind.Running, session, next) { TurnNumber = work.Attempts[^1].TurnNumber });
                    reported = next;
                }
            }
            string text = await completed;
            CodexWorkResult output = CodexWorkResults.Read(text, gitRepositoryChanges);
            string? kind = output.Kind;
            string? body = output.Text;
            GitRepositorySetup[]? setups = CodexWorkResults.Setup(output);
            return new(kind == "workspace" ? ObservationKind.WorkspaceRequired : kind == "input" ? ObservationKind.Paused : ObservationKind.Result, session, body)
            {
                Setup = setups,
                TurnNumber = work.Attempts[^1].TurnNumber,
                ReleaseWorkspace = !gitRepositoryChanges || kind == "result" ||
                    ((CodexGitRepositoryWorkResult)output).ReleaseWorkspace
            };
        }
        finally
        {
            _codex.Notification -= transcript.Handle;
            _codex.Disconnected -= Disconnected;
            _ = transcript.Completion.Task.Exception;
            string? threadId = transcript.ThreadId;
            string? turnId = transcript.TurnId;
            if (threadId is not null && turnId is not null && !transcript.Completion.Task.IsCompletedSuccessfully && _codex.Ready)
                try { await _codex.RequestAsync<TurnInterruptParams, TurnInterruptResponse>("turn/interrupt", new() { ThreadId = threadId, TurnId = turnId }); }
                catch { _codex.Fail(); }
        }
    }
}
