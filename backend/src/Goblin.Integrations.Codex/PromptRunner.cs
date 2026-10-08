using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Protocol;

namespace Goblin.Integrations.Codex;

public sealed class PromptRunner
{
    private readonly CodexClient _codex;
    private readonly TimeSpan _timeout;

    public PromptRunner(CodexClient codex, TimeSpan timeout)
    {
        _codex = codex;
        _timeout = timeout;
    }

    private static IntegrationFailure Cancelled() => new("prompt_cancelled", "The prompt was cancelled.");

    public async Task<(string Reply, string Model, long DurationMs)> RunAsync(string prompt, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) throw Cancelled();
        var elapsed = Stopwatch.StartNew();
        var transcript = new CodexTurnTranscript(32, 8000, "\n\n", countSeparators: true,
            trimResult: true, progressLimit: 0,
            tooLarge: () => new IntegrationFailure("prompt_reply_too_large", "The reply was too long. Try a shorter prompt."),
            completionError: turn => turn.Status == TurnStatus.Failed ? GenerationError(turn.Error) :
                turn.Status != TurnStatus.Completed ? Cancelled() : null,
            resultError: reply => reply.Length == 0
                ? new IntegrationFailure("prompt_empty_reply", "The model finished without a text reply. Please retry.") : null);
        void Disconnected() => transcript.Fail(IntegrationFailure.RuntimeUnavailable());
        _codex.Notification += transcript.Handle;
        _codex.Disconnected += Disconnected;
        using CancellationTokenRegistration cancellation = cancellationToken.Register(() => transcript.Fail(Cancelled()));
        try
        {
            ThreadStartResponse thread = await _codex.RequestAsync<ThreadStartParams, ThreadStartResponse>("thread/start", new()
            {
                Cwd = _codex.Options.Workspace,
                Ephemeral = true,
                ApprovalPolicy = AskForApproval.Never,
                Sandbox = SandboxMode.ReadOnly,
                BaseInstructions = "Answer the user's prompt briefly, using only the text in this conversation. Do not call tools, inspect files, browse, or perform any actions."
            });
            transcript.SetThread(thread.Thread.Id);
            if (!thread.Thread.Ephemeral || thread.ModelProvider != "openai" ||
                thread.Sandbox is not ReadOnlySandboxPolicy ||
                thread.ApprovalPolicy is not StringAskForApproval { Value: AskForApprovalValue.Never })
                throw new IntegrationFailure("prompt_configuration_error", "Codex could not create an isolated conversation. Check the pinned runtime version.");
            if (cancellationToken.IsCancellationRequested) throw Cancelled();
            TurnStartResponse started = await _codex.RequestAsync<TurnStartParams, TurnStartResponse>("turn/start", new()
            {
                ThreadId = transcript.ThreadId!,
                Input = [new TextUserInput { Text = prompt }],
                ApprovalPolicy = AskForApproval.Never,
                SandboxPolicy = new ReadOnlySandboxPolicy { NetworkAccess = false }
            });
            transcript.SetTurn(started.Turn.Id);
            string reply;
            try { reply = await transcript.Completion.Task.WaitAsync(_timeout); }
            catch (TimeoutException)
            {
                throw new IntegrationFailure("prompt_timeout", "The prompt took too long and was cancelled. Please retry.");
            }
            return (reply, thread.Model, elapsed.ElapsedMilliseconds);
        }
        finally
        {
            _codex.Notification -= transcript.Handle;
            _codex.Disconnected -= Disconnected;
            // Observe faults even if thread/start or turn/start failed before the completion wait.
            _ = transcript.Completion.Task.Exception;
            string? threadId = transcript.ThreadId;
            string? turnId = transcript.TurnId;
            if (threadId is not null && _codex.Ready)
            {
                if (turnId is not null && !transcript.Finished)
                    try { await _codex.RequestAsync<TurnInterruptParams, TurnInterruptResponse>("turn/interrupt", new() { ThreadId = threadId, TurnId = turnId }); }
                    catch { _codex.Fail(); }
                if (_codex.Ready)
                    try { await _codex.RequestAsync<ThreadUnsubscribeParams, ThreadUnsubscribeResponse>("thread/unsubscribe", new() { ThreadId = threadId }); }
                    catch { }
            }
        }
    }

    private static IntegrationFailure GenerationError(TurnError? error)
    {
        CodexErrorInfo? info = error?.CodexErrorInfo;
        ushort? status = info switch
        {
            HttpConnectionFailedCodexErrorInfo value => value.HttpConnectionFailed.HttpStatusCode,
            ResponseStreamConnectionFailedCodexErrorInfo value => value.ResponseStreamConnectionFailed.HttpStatusCode,
            ResponseStreamDisconnectedCodexErrorInfo value => value.ResponseStreamDisconnected.HttpStatusCode,
            ResponseTooManyFailedAttemptsCodexErrorInfo value => value.ResponseTooManyFailedAttempts.HttpStatusCode,
            _ => null
        };
        if (info is StringCodexErrorInfo { Value: CodexErrorInfoValue.Unauthorized } || status == 401)
            return new("prompt_unauthorized", "OpenAI rejected the saved login. Disconnect Codex and sign in again.");
        if (info is StringCodexErrorInfo { Value: CodexErrorInfoValue.UsageLimitExceeded or CodexErrorInfoValue.RateLimitExceeded } || status == 429)
            return new("prompt_limit_reached", "The connected account has reached a usage or rate limit. Check your plan or API billing, then retry later.");
        if (status == 403)
            return new("prompt_access_denied", "The connected account cannot use this model. Check your account's model access and permissions.");
        return new("prompt_failed", "The model request failed. Check your account's model access and billing, then retry.");
    }
}
