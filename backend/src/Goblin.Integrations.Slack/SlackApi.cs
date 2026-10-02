using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts.Conversations;

namespace Goblin.Integrations.Slack;

public sealed class SlackFailure : Exception
{
    public SlackFailure(string message = "Slack could not complete this operation. Check the connection and try again.") : base(message) { }
}

public sealed record SlackCredentials(
    [property: JsonPropertyName("InstallationId")] string InstallationId,
    [property: JsonPropertyName("AppToken")] string AppToken,
    [property: JsonPropertyName("BotToken")] string BotToken,
    [property: JsonPropertyName("Workspace")] string Workspace,
    [property: JsonPropertyName("WorkspaceId")] string WorkspaceId,
    [property: JsonPropertyName("AppId")] string AppId,
    [property: JsonPropertyName("BotUserId")] string BotUserId)
{
    [JsonPropertyName("Installation")]
    public ExternalInstallation Installation => new(InstallationId, WorkspaceId, AppId, BotUserId);

    public override string ToString() => "Slack credentials (redacted)";
}

public sealed class SlackApi : IDisposable
{
    private readonly HttpClient _http;

    public SlackApi(HttpMessageHandler? handler = null)
    {
        _http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new("https://slack.com/api/"),
            Timeout = TimeSpan.FromSeconds(20)
        };
    }

    private async Task<T> CallAsync<T>(string method, string credential, IEnumerable<KeyValuePair<string, string>> body, CancellationToken token) where T : SlackResponse
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, method)
        {
            Content = new FormUrlEncodedContent(body)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return await SendAsync<T>(request, token, method == "auth.test");
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken token, bool verifyRuntimeScopes = false) where T : SlackResponse
    {
        try
        {
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests) throw new SlackFailure("Slack is rate limiting requests. Wait a minute, then try again.");
            if (!response.IsSuccessStatusCode) throw new SlackFailure();
            if (verifyRuntimeScopes)
            {
                string[] scopes = response.Headers.TryGetValues("x-oauth-scopes", out IEnumerable<string>? values) ? string.Join(",", values).Split(',', StringSplitOptions.TrimEntries) : [];
                if (new[] { "app_mentions:read", "im:history", "chat:write", "users:read" }.Any(scope => !scopes.Contains(scope)))
                    throw new SlackFailure("This bot token is missing Goblin’s required Slack scopes. Reinstall the app with Goblin’s manifest, then reconnect.");
            }
            using Stream stream = await response.Content.ReadAsStreamAsync(token);
            using var memory = new MemoryStream();
            byte[] buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, token)) > 0)
            {
                if (memory.Length + read > 1024 * 1024) throw new SlackFailure();
                memory.Write(buffer, 0, read);
            }
            T result = JsonSerializer.Deserialize<T>(memory.ToArray()) ?? throw new SlackFailure();
            if (!result.Ok) throw new SlackFailure();
            return result;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or IOException) { throw new SlackFailure(); }
    }

    public async Task<SlackCredentials> VerifyAsync(string appToken, string botToken, CancellationToken token)
    {
        if (!ValidToken(appToken, "xapp-") || !ValidToken(botToken, "xoxb-"))
            throw new SlackFailure("Enter the app-level xapp token and bot xoxb token from the same Slack app.");
        SlackAuthResponse auth = await CallAsync<SlackAuthResponse>("auth.test", botToken, [], token);
        string team = auth.TeamId, user = auth.UserId, bot = auth.BotId;
        if (!Id(team, 'T') || !Id(user, 'U', 'W') || !Id(bot, 'B')) throw new SlackFailure();
        SlackBotResponse info = await CallAsync<SlackBotResponse>("bots.info", botToken, [new("bot", bot)], token);
        string app = info.Bot?.AppId ?? "";
        if (!Id(app, 'A')) throw new SlackFailure();
        var credentials = new SlackCredentials(Guid.NewGuid().ToString("N"), appToken, botToken, auth.Team, team, app, user);
        using ClientWebSocket socket = await OpenAsync(credentials, token);
        return credentials;
    }

    public async Task<ClientWebSocket> OpenAsync(SlackCredentials credentials, CancellationToken token)
    {
        SlackOpenResponse response = await CallAsync<SlackOpenResponse>("apps.connections.open", credentials.AppToken, [], token);
        if (!Uri.TryCreate(response.Url, UriKind.Absolute, out Uri? url) ||
            url.Scheme != "wss" || url.UserInfo.Length != 0 || !url.IsDefaultPort ||
            !(url.Host == "wss.slack.com" || url.Host.EndsWith(".slack.com", StringComparison.Ordinal))) throw new SlackFailure();
        var socket = new ClientWebSocket();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(url, timeout.Token);
            SlackSocketEnvelope hello = await ReceiveAsync(socket, timeout.Token) ?? throw new SlackFailure();
            if (hello.Type != "hello" || hello.ConnectionInfo?.AppId != credentials.AppId)
                throw new SlackFailure("The app and bot tokens belong to different Slack apps.");
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }

    public static async Task<SlackSocketEnvelope?> ReceiveAsync(ClientWebSocket socket, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        WebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(chunk, token);
            if (received.MessageType == WebSocketMessageType.Close) return null;
            if (received.MessageType != WebSocketMessageType.Text || buffer.Length + received.Count > 1024 * 1024) throw new SlackFailure();
            buffer.Write(chunk, 0, received.Count);
        } while (!received.EndOfMessage);
        return JsonSerializer.Deserialize<SlackSocketEnvelope>(buffer.ToArray()) ?? throw new SlackFailure();
    }

    public async Task PostAsync(SlackCredentials credentials, string channel, string thread, string text, CancellationToken token)
    {
        var request = new SlackPostMessageRequest(channel, thread, text);
        await CallAsync<SlackResponse>("chat.postMessage", credentials.BotToken, request.Form(), token);
    }

    public async Task SetIconAsync(string setupToken, string appId, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "apps.icon.set");
        request.Headers.Authorization = new("Bearer", setupToken);
        var multipart = new MultipartFormDataContent
        {
            { new StringContent(appId), "app_id" }
        };
        var image = new StreamContent(Assets.Icon());
        image.Headers.ContentType = new("image/png");
        multipart.Add(image, "file", "goblin.png");
        request.Content = multipart;
        await SendAsync<SlackResponse>(request, token);
    }

    public static bool Id(string value, params char[] prefixes) => value.Length is >= 2 and <= 64 && Array.IndexOf(prefixes, value[0]) >= 0 && Regex.IsMatch(value, "^[A-Z0-9]+$", RegexOptions.CultureInvariant);

    private static bool ValidToken(string value, string prefix) => value.StartsWith(prefix, StringComparison.Ordinal) && value.Length is > 12 and < 2048 && Regex.IsMatch(value, "^[A-Za-z0-9-]+$", RegexOptions.CultureInvariant);

    public void Dispose() => _http.Dispose();
}

public static class Assets
{
    public static Stream Icon() => typeof(Assets).Assembly.GetManifestResourceStream("Goblin.Integrations.Slack.slack.png")!;

    public static string Manifest { get; } = ReadManifest();

    private static string ReadManifest()
    {
        using var reader = new StreamReader(typeof(Assets).Assembly.GetManifestResourceStream("Goblin.Integrations.Slack.manifest.json")!);
        return reader.ReadToEnd();
    }
}
