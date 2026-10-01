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
                await Task.WhenAny(receive, process);
                await cancellation.CancelAsync();
                try { await Task.WhenAll(receive, process); } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
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
            using JsonDocument? document = await SlackApi.ReceiveAsync(socket, token);
            if (document is null || SlackApi.String(document.RootElement, "type") == "disconnect") return;
            JsonElement envelope = document.RootElement;
            string envelopeId = SlackApi.String(envelope, "envelope_id");
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
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { envelope_id = envelopeId }), WebSocketMessageType.Text, true, token);
        }
    }

    private async Task ProcessAsync(SlackCredentials credentials, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            ExternalReply? reply = await _conversations.ProcessNextAsync(credentials.Installation, token);
            if (reply is null) { await Task.Delay(500, token); continue; }
            // Never publish Work contents into a channel or DM. Local sign-in
            // remains required to inspect results and authorize repositories.
            string text = reply.Kind switch
            {
                ExternalReplyKind.WorkSaved => $"Saved in Goblin. <{_origin}/work?item={reply.WorkId}|Open Work> to follow progress and respond.",
                ExternalReplyKind.LinkConfirmationRequired => "Linking code received. Return to Goblin’s Settings → Integrations → Slack and select Grant local owner access. Once approved, send me a new DM with your request.",
                ExternalReplyKind.AccessRequired => "Your Slack identity needs access. In Goblin’s Settings → Integrations → Slack, select Link a Slack identity and complete the confirmation. Then send your request again.",
                ExternalReplyKind.CommandRejected => "This message could not be applied. Open Goblin to review Work before sending another request.",
                _ => throw new InvalidOperationException("Unknown conversation reply kind.")
            };
            try { await _api.PostAsync(credentials, reply.ChannelId, reply.ThreadId, text, token); }
            catch (SlackFailure) { /* A reply failure cannot roll back or repeat accepted Work. */ }
        }
    }
}
