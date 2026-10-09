using System;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts.Conversations;
using Microsoft.Extensions.Hosting;

namespace Goblin.Integrations.Slack;

public sealed class SlackConnection : BackgroundService
{
    private readonly SlackApi _api;
    private readonly SlackCredentialStore _store;
    private readonly IExternalConversations _conversations;
    private readonly string _origin;

    private readonly SemaphoreSlim _changes = new(1);

    private readonly Lock _gate = new();
    private SlackCredentials? _credentials;
    private CancellationTokenSource? _connection;

    private SlackConnectionView _view = new(SlackConnectionStatus.Disconnected, null, null, null, null, null);

    public SlackConnection(SlackApi api, SlackCredentialStore store, IExternalConversations conversations, string origin)
    {
        _api = api; _store = store; _conversations = conversations; _origin = origin.TrimEnd('/');
        try { _credentials = store.Read(); }
        catch (SlackFailure failure) { _view = _view with { Status = SlackConnectionStatus.Unavailable, Notice = failure.Message }; }
        if (_credentials is { } credentials) SetView(credentials, SlackConnectionStatus.Connecting);
    }

    public SlackConnectionView View { get { lock (_gate) return _view; } }

    public ExternalInstallation? Installation { get { lock (_gate) return _credentials?.Installation; } }

    public async Task ConnectAsync(string appToken, string botToken, CancellationToken token)
    {
        await _changes.WaitAsync(token);
        try
        {
            if (Installation is not null) throw new SlackFailure("Disconnect the current Slack app before connecting another.");
            SlackCredentials credentials = await _api.VerifyAsync(appToken.Trim(), botToken.Trim(), token);
            await _store.SaveAsync(credentials, token);
            lock (_gate) { _credentials = credentials; SetView(credentials, SlackConnectionStatus.Connecting); }
        }
        finally { _changes.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken token)
    {
        await _changes.WaitAsync(token);
        try
        {
            _store.Clear();
            lock (_gate)
            {
                _credentials = null; _connection?.Cancel();
                _view = new(SlackConnectionStatus.Disconnected, null, null, null, null, null);
            }
        }
        finally { _changes.Release(); }
    }

    private void SetView(SlackCredentials credentials, SlackConnectionStatus status, string? notice = null)
    {
        lock (_gate) if (_credentials?.InstallationId == credentials.InstallationId)
            _view = new(status, credentials.Workspace, credentials.WorkspaceId, credentials.AppId, credentials.BotUserId, notice);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            SlackCredentials? credentials;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            lock (_gate) { credentials = _credentials; _connection = cancellation; }
            if (credentials is null)
            {
                lock (_gate) if (_connection == cancellation) _connection = null;
                await Task.Delay(1000, stoppingToken); continue;
            }
            try
            {
                using ClientWebSocket socket = await _api.OpenAsync(credentials, cancellation.Token);
                SetView(credentials, SlackConnectionStatus.Connected);
                Task receive = ReceiveAsync(socket, credentials, cancellation.Token);
                Task process = ProcessAsync(credentials, cancellation.Token);
                Task notifications = NotificationsAsync(credentials, cancellation.Token);
                await Task.WhenAny(receive, process, notifications);
                await cancellation.CancelAsync();
                try { await Task.WhenAll(receive, process, notifications); } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested)
            {
                lock (_gate) if (_credentials?.InstallationId == credentials.InstallationId)
                    SetView(credentials, SlackConnectionStatus.Unavailable, "Slack is disconnected. Goblin will reconnect; accepted Work continues locally.");
            }
            finally { lock (_gate) if (_connection == cancellation) _connection = null; }
            // Transport reconnection never retries failed Work.
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task ReceiveAsync(ClientWebSocket socket, SlackCredentials credentials, CancellationToken token)
    {
        while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            SlackSocketEnvelope? envelope = await SlackApi.ReceiveAsync(socket, token);
            if (envelope is null || envelope.Type == "disconnect") return;
            string envelopeId = envelope.EnvelopeId;
            if (envelopeId.Length is 0 or > 200) continue;
            ExternalMessage? message = SlackEvents.Parse(envelope, credentials.Installation);
            if (message is not null)
            {
                // Ack only after durable acceptance. If storage is unavailable,
                // close the connection so Slack can redeliver the same event.
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                await _conversations.AcceptAsync(message, deadline.Token);
            }
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new SlackAcknowledgement(envelopeId)), WebSocketMessageType.Text, true, token);
        }
    }

    private async Task ProcessAsync(SlackCredentials credentials, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            ExternalReply? reply = await _conversations.ProcessNextAsync(credentials.Installation, token);
            if (reply is null) { await Task.Delay(500, token); continue; }
            // Acknowledgements confirm acceptance; notifications carry saved outcomes.
            string text = reply.Kind switch
            {
                ExternalReplyKind.WorkSaved => $"Saved in Goblin. <{_origin}/work?item={reply.WorkId}|Open Work> to follow progress and respond.",
                ExternalReplyKind.LinkConfirmationRequired => "Linking code received. Return to Goblin’s Settings → Integrations → Slack and select Grant local owner access. Once approved, send me a new DM with your request.",
                ExternalReplyKind.AccessRequired => "Your Slack identity needs access. In Goblin’s Settings → Integrations → Slack, select Link a Slack identity and complete the confirmation. Then send your request again.",
                ExternalReplyKind.CommandRejected => "This message could not be applied. Open Goblin to review Work before sending another request.",
                _ => throw new InvalidOperationException("Unknown conversation reply kind.")
            };
            if (reply.Notice is { } notice)
                text += "\n\n" + notice.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
            try { await _api.PostAsync(credentials, reply.ChannelId, reply.ThreadId, text, token); }
            catch (SlackFailure) { /* A reply failure cannot roll back or repeat accepted Work. */ }
        }
    }

    private async Task NotificationsAsync(SlackCredentials credentials, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await DeliverQuestionAsync(credentials, token);
            await DeliverUpdatesAsync(credentials, token);
            await Task.Delay(TimeSpan.FromSeconds(3), token);
        }
    }

    internal async Task DeliverUpdatesAsync(SlackCredentials credentials, CancellationToken token)
    {
        if (Installation?.Id != credentials.InstallationId) return;
        ExternalWorkUpdate[] updates = await _conversations.PendingUpdatesAsync(credentials.Installation, token);
        foreach (ExternalWorkUpdate update in updates)
        {
            if (Installation?.Id != credentials.InstallationId) return;
            if (!await _conversations.UpdateIsCurrentAsync(credentials.Installation, update, token)) continue;
            await DeliverUpdateAsync(credentials, update, token);
        }
    }

    private async Task DeliverUpdateAsync(SlackCredentials credentials, ExternalWorkUpdate update, CancellationToken token)
    {
        string heading = update.Content.Kind switch
        {
            ExternalWorkUpdateKind.ResultReady => "Goblin finished execution. Your result is ready for review:",
            ExternalWorkUpdateKind.Completed => "Work completed. The result has been approved:",
            ExternalWorkUpdateKind.Failed => "Goblin encountered an error:",
            ExternalWorkUpdateKind.Uncertain => "Goblin needs your attention:",
            ExternalWorkUpdateKind.CleanupFailed => "Goblin encountered a cleanup error:",
            ExternalWorkUpdateKind.Cancelled => "Goblin update:",
            _ => throw new InvalidOperationException("Unknown Work update kind.")
        };
        string text = EscapeExcerpt(update.Content.Text);
        string action = update.Content.Kind switch
        {
            ExternalWorkUpdateKind.ResultReady => $"<{_origin}/work?item={update.WorkId}|Review the result in Goblin> to approve it or request changes.",
            ExternalWorkUpdateKind.Failed => $"<{_origin}/work?item={update.WorkId}|Open Work> to review the error and explicitly retry or cancel. Goblin will not retry automatically.",
            _ => $"<{_origin}/work?item={update.WorkId}|Open Work in Goblin>."
        };
        if (Installation?.Id != credentials.InstallationId) return;
        try { await _api.PostAsync(credentials, update.ChannelId, update.ThreadId, $"{heading}\n{text}\n\n{action}", token); }
        catch (SlackFailure) { return; }
        // As with questions, a crash after posting but before saving can duplicate
        // the notification. Delivery never dispatches or retries agent execution.
        await _conversations.UpdateSentAsync(credentials.Installation, update, token);
    }

    internal async Task DeliverQuestionAsync(SlackCredentials credentials, CancellationToken token)
    {
        if (Installation?.Id != credentials.InstallationId) return;
        ExternalQuestion? question = await _conversations.NextQuestionAsync(credentials.Installation, token);
        if (question is null) return;
        // Escape Slack's special characters so question text cannot inject mentions or links.
        string text = EscapeExcerpt(question.Text);
        text = $"Goblin needs your answer:\n{text}\n\nReply in this thread to answer. In a channel, mention @goblin in your reply. You can also <{_origin}/work?item={question.WorkId}|answer in Goblin>.";
        if (Installation?.Id != credentials.InstallationId) return;
        try { await _api.PostAsync(credentials, question.ChannelId, question.ThreadId, text, token); }
        catch (SlackFailure) { return; } // Keep the saved question pending for the next poll.
        // A crash between posting and saving this marker can duplicate a notification,
        // but cannot repeat Work execution or change its decision.
        await _conversations.QuestionSentAsync(credentials.Installation, question, token);
    }

    private static string EscapeExcerpt(string text)
    {
        int length = System.Math.Min(text.Length, 3000);
        if (length < text.Length && char.IsHighSurrogate(text[length - 1])) length--;
        string excerpt = length < text.Length ? text[..length] + "…" : text;
        return excerpt.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }
}
