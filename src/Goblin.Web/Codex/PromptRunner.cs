using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Protocol;

namespace Goblin.Web.Codex;

public sealed class PromptRunner(CodexClient codex, TimeSpan timeout)
{
    private static PublicError Cancelled() => new("prompt_cancelled", "The prompt test was cancelled.", 408);

    public async Task<(string Reply, string Model, long DurationMs)> RunAsync(string prompt, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) throw Cancelled();
        var elapsed = Stopwatch.StartNew();
        var gate = new object();
        string? threadId = null, turnId = null;
        var finished = false;
        var messages = new OrderedDictionary<string, string>();
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Save(ThreadItem item)
        {
            if (item is not AgentMessageThreadItem message || message.Phase == MessagePhase.Commentary) return;
            messages[message.Id] = message.Text;
            if (messages.Count > 32 || string.Join("\n\n", messages.Values).Length > 8000)
                completion.TrySetException(new PublicError("prompt_reply_too_large",
                    "The reply was too long for a connection test. Try a shorter prompt.", 502));
        }

        void Notification(ServerNotification notification)
        {
            lock (gate)
            {
                var (eventThreadId, eventTurnId) = notification switch
                {
                    TurnStartedServerNotification e => (e.Params.ThreadId, e.Params.Turn.Id),
                    ItemCompletedServerNotification e => (e.Params.ThreadId, e.Params.TurnId),
                    TurnCompletedServerNotification e => (e.Params.ThreadId, e.Params.Turn.Id),
                    _ => (null, null)
                };
                if (threadId is null || threadId != eventThreadId || eventTurnId is null ||
                    (turnId is not null && turnId != eventTurnId)) return;
                turnId ??= eventTurnId;
                if (notification is ItemCompletedServerNotification item) Save(item.Params.Item);
                if (notification is TurnCompletedServerNotification ended)
                {
                    finished = true;
                    var turn = ended.Params.Turn;
                    if (turn.Status == TurnStatus.Failed) { completion.TrySetException(GenerationError(turn.Error)); return; }
                    if (turn.Status != TurnStatus.Completed) { completion.TrySetException(Cancelled()); return; }
                    foreach (var value in turn.Items) Save(value);
                    var reply = string.Join("\n\n", messages.Values).Trim();
                    if (reply.Length == 0)
                        completion.TrySetException(new PublicError("prompt_empty_reply", "The model finished without a text reply. Please retry.", 502));
                    else completion.TrySetResult(reply);
                }
            }
        }

        void Disconnected() => completion.TrySetException(PublicError.RuntimeUnavailable());
        codex.Notification += Notification;
        codex.Disconnected += Disconnected;
        using var cancellation = cancellationToken.Register(() => completion.TrySetException(Cancelled()));
        try
        {
            var thread = await codex.RequestAsync<ThreadStartParams, ThreadStartResponse>("thread/start", new()
            {
                Cwd = codex.Options.Workspace,
                Ephemeral = true,
                ApprovalPolicy = AskForApproval.Never,
                Sandbox = SandboxMode.ReadOnly,
                BaseInstructions = "Answer the user's connection-test prompt briefly, using only the text in this conversation. Do not call tools, inspect files, browse, or perform any actions."
            });
            lock (gate) threadId = thread.Thread.Id;
            if (!thread.Thread.Ephemeral || thread.ModelProvider != "openai" ||
                thread.Sandbox is not ReadOnlySandboxPolicy || thread.ApprovalPolicy != AskForApproval.Never)
                throw new PublicError("prompt_configuration_error", "Codex could not create an isolated prompt test. Check the pinned runtime version.", 502);
            if (cancellationToken.IsCancellationRequested) throw Cancelled();
            var started = await codex.RequestAsync<TurnStartParams, TurnStartResponse>("turn/start", new()
            {
                ThreadId = threadId,
                Input = [new TextUserInput { Text = prompt }],
                ApprovalPolicy = AskForApproval.Never,
                SandboxPolicy = new ReadOnlySandboxPolicy { NetworkAccess = false }
            });
            lock (gate)
            {
                if (turnId is not null && turnId != started.Turn.Id) throw PublicError.RuntimeUnavailable();
                turnId = started.Turn.Id;
            }
            string reply;
            try { reply = await completion.Task.WaitAsync(timeout); }
            catch (TimeoutException)
            {
                throw new PublicError("prompt_timeout", "The prompt test took too long and was cancelled. Please retry.", 504);
            }
            return (reply, thread.Model, elapsed.ElapsedMilliseconds);
        }
        finally
        {
            codex.Notification -= Notification;
            codex.Disconnected -= Disconnected;
            // Observe faults even if thread/start or turn/start failed before the completion wait.
            _ = completion.Task.Exception;
            if (threadId is not null && codex.Ready)
            {
                if (turnId is not null && !finished)
                    try { await codex.RequestAsync<TurnInterruptParams, TurnInterruptResponse>("turn/interrupt", new() { ThreadId = threadId, TurnId = turnId }); }
                    catch { codex.Fail(); }
                if (codex.Ready)
                    try { await codex.RequestAsync<ThreadUnsubscribeParams, ThreadUnsubscribeResponse>("thread/unsubscribe", new() { ThreadId = threadId }); }
                    catch { }
            }
        }
    }

    private static PublicError GenerationError(TurnError? error)
    {
        var info = error?.CodexErrorInfo;
        var status = info switch
        {
            HttpConnectionFailedCodexErrorInfo value => value.HttpConnectionFailed.HttpStatusCode,
            ResponseStreamConnectionFailedCodexErrorInfo value => value.ResponseStreamConnectionFailed.HttpStatusCode,
            ResponseStreamDisconnectedCodexErrorInfo value => value.ResponseStreamDisconnected.HttpStatusCode,
            ResponseTooManyFailedAttemptsCodexErrorInfo value => value.ResponseTooManyFailedAttempts.HttpStatusCode,
            _ => null
        };
        if (info == CodexErrorInfo.Unauthorized || status == 401)
            return new("prompt_unauthorized", "OpenAI rejected the saved login. Disconnect Codex and sign in again.", 502);
        if (info == CodexErrorInfo.UsageLimitExceeded || info == CodexErrorInfo.RateLimitExceeded || status == 429)
            return new("prompt_limit_reached", "The connected account has reached a usage or rate limit. Check your plan or API billing, then retry later.", 429);
        if (status == 403)
            return new("prompt_access_denied", "The connected account cannot use this model. Check your account's model access and permissions.", 502);
        return new("prompt_failed", "The model request failed. Check your account's model access and billing, then retry.", 502);
    }
}
