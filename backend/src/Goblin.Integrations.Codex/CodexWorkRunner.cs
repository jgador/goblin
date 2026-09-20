using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;
using Goblin.Protocol;

namespace Goblin.Integrations.Codex;

// Durable execution has its own inputs/results. The restricted verification
// endpoint and its cancellation/size policy do not define Work execution.
public sealed class CodexWorkRunner
{
    private readonly CodexClient _codex;

    public CodexWorkRunner(CodexClient codex) => _codex = codex;

    public async Task<ExecutionObservation> RunAsync(WorkSnapshot work, bool repositoryChanges,
        Func<ExecutionObservation, Task> progress, CancellationToken token)
    {
        object gate = new();
        string? threadId = null, turnId = null;
        string? latestProgress = null;
        var messages = new OrderedDictionary<string, string>();
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Save(ThreadItem item)
        {
            if (item is not AgentMessageThreadItem message) return;
            if (message.Phase == MessagePhase.Commentary)
            {
                latestProgress = message.Text[..Math.Min(message.Text.Length, 4000)];
                return;
            }
            messages[message.Id] = message.Text;
            if (messages.Count > 128 || messages.Values.Sum(x => x.Length) > 64000)
                completion.TrySetException(new IntegrationFailure("work_result_too_large", "The result exceeded the supported size."));
        }
        void Notification(ServerNotification notification)
        {
            lock (gate)
            {
                (string? thread, string? turn) = notification switch
                {
                    TurnStartedServerNotification e => (e.Params.ThreadId, e.Params.Turn.Id),
                    ItemCompletedServerNotification e => (e.Params.ThreadId, e.Params.TurnId),
                    TurnCompletedServerNotification e => (e.Params.ThreadId, e.Params.Turn.Id),
                    ErrorServerNotification e => (e.Params.ThreadId, e.Params.TurnId),
                    _ => (null, null)
                };
                if (threadId is null || thread != threadId || turn is null || (turnId is not null && turn != turnId)) return;
                turnId ??= turn;
                if (notification is ErrorServerNotification)
                {
                    completion.TrySetException(new IntegrationFailure("work_execution_failed", "Execution needs attention."));
                    return;
                }
                if (notification is ItemCompletedServerNotification item) Save(item.Params.Item);
                if (notification is TurnCompletedServerNotification done)
                {
                    if (done.Params.Turn.Status != TurnStatus.Completed)
                    {
                        completion.TrySetException(new IntegrationFailure("work_execution_failed", "Execution did not complete."));
                        return;
                    }
                    foreach (ThreadItem value in done.Params.Turn.Items) Save(value);
                    completion.TrySetResult(string.Join("\n", messages.Values));
                }
            }
        }
        void Disconnected() => completion.TrySetException(IntegrationFailure.RuntimeUnavailable());
        _codex.Notification += Notification;
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
                Sandbox = repositoryChanges ? SandboxMode.DangerFullAccess : SandboxMode.ReadOnly,
                BaseInstructions = "You execute a Goblin Work item. Return a JSON object with kind and text. " +
                    "Use kind result for a proposed outcome requiring human review, or input for a question that prevents progress. " +
                    "Do not claim approval or completion on behalf of the user. " +
                    (repositoryChanges ? "Work only on the assigned repository and branch in this isolated environment. " +
                        "Commit locally and use goblin-github publish to publish the branch; use goblin-github pull-request to open its draft PR after publishing. " +
                        "Use goblin-github fetch to refresh origin branches before incorporating upstream changes locally. " +
                        "GitHub credentials are held by Goblin. Main and other branches cannot be published or merged through these operations. " :
                        "Use only the supplied context. Do not call tools, inspect files, browse, or run commands. ")
            }, token);
            lock (gate) threadId = thread.Thread.Id;
            string context = JsonSerializer.Serialize(new { work.Objective, work.Messages, work.Decisions, work.Results, work.Artifacts });
            JsonElement schema = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                additionalProperties = false,
                required = new[] { "kind", "text" },
                properties = new { kind = new { type = "string", @enum = new[] { "result", "input" } }, text = new { type = "string" } }
            });
            TurnStartResponse started = await _codex.RequestAsync<TurnStartParams, TurnStartResponse>("turn/start", new()
            {
                ThreadId = threadId,
                Input = [new TextUserInput { Text = context }],
                ApprovalPolicy = AskForApproval.Never,
                OutputSchema = schema,
                SandboxPolicy = repositoryChanges ? new ExternalSandboxSandboxPolicy { NetworkAccess = NetworkAccess.Enabled }
                    : new ReadOnlySandboxPolicy { NetworkAccess = false }
            }, token);
            lock (gate)
            {
                if (turnId is not null && turnId != started.Turn.Id) throw IntegrationFailure.RuntimeUnavailable();
                turnId = started.Turn.Id;
            }
            var session = new ExecutionSession(thread.Model, threadId, turnId);
            await progress(new(ObservationKind.Running, session));
            Task<string> completed = completion.Task.WaitAsync(TimeSpan.FromMinutes(30), token);
            string? reported = null;
            while (!completed.IsCompleted)
            {
                await Task.WhenAny(completed, Task.Delay(TimeSpan.FromSeconds(1), token));
                token.ThrowIfCancellationRequested();
                string? next;
                lock (gate) next = latestProgress;
                if (!string.IsNullOrWhiteSpace(next) && next != reported)
                {
                    await progress(new(ObservationKind.Running, session, next));
                    reported = next;
                }
            }
            string text = await completed;
            using JsonDocument output = JsonDocument.Parse(text);
            string? kind = output.RootElement.GetProperty("kind").GetString();
            string? body = output.RootElement.GetProperty("text").GetString();
            if (string.IsNullOrWhiteSpace(body) || kind is not ("result" or "input"))
                throw new IntegrationFailure("invalid_work_result", "The runtime returned an invalid result.");
            return new(kind == "input" ? ObservationKind.InputRequired : ObservationKind.Result, session, body);
        }
        finally
        {
            _codex.Notification -= Notification;
            _codex.Disconnected -= Disconnected;
            _ = completion.Task.Exception;
            if (threadId is not null && turnId is not null && !completion.Task.IsCompletedSuccessfully && _codex.Ready)
                try { await _codex.RequestAsync<TurnInterruptParams, TurnInterruptResponse>("turn/interrupt", new() { ThreadId = threadId, TurnId = turnId }); }
                catch { _codex.Fail(); }
        }
    }
}
