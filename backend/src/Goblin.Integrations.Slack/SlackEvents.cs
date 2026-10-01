using System;
using System.Text.Json;
using System.Text.RegularExpressions;
using Goblin.Contracts.Conversations;

namespace Goblin.Integrations.Slack;

public static class SlackEvents
{
    // Only authenticated Socket Mode envelopes from the verified app are input.
    public static ExternalMessage? Parse(JsonElement envelope, ExternalInstallation installation)
    {
        if (SlackApi.String(envelope, "type") != "events_api" || !envelope.TryGetProperty("payload", out JsonElement payload) ||
            SlackApi.String(payload, "api_app_id") != installation.AppId || SlackApi.String(payload, "team_id") != installation.WorkspaceId ||
            !payload.TryGetProperty("event", out JsonElement message)) return null;
        string type = SlackApi.String(message, "type"), user = SlackApi.String(message, "user"), channel = SlackApi.String(message, "channel");
        bool direct = type == "message" && SlackApi.String(message, "channel_type") == "im";
        if ((!direct && type != "app_mention") || !SlackApi.Id(user, 'U', 'W') || user == installation.BotUserId ||
            !SlackApi.Id(channel, 'D', 'C', 'G') || message.TryGetProperty("bot_id", out _) ||
            message.TryGetProperty("subtype", out _)) return null;
        string text = SlackApi.String(message, "text").Replace("<@" + installation.BotUserId + ">", "", StringComparison.Ordinal).Trim();
        string timestamp = SlackApi.String(message, "ts"), thread = SlackApi.String(message, "thread_ts"), id = SlackApi.String(payload, "event_id");
        if (thread.Length == 0) thread = timestamp;
        if (text.Length is 0 or > 4000 || !Timestamp(timestamp) || !Timestamp(thread) || !Regex.IsMatch(id, "^Ev[A-Za-z0-9]{1,62}$", RegexOptions.CultureInvariant)) return null;
        return new(installation, id, user, channel, thread, timestamp, text, direct);
    }

    private static bool Timestamp(string value) => Regex.IsMatch(value, "^[0-9]{1,16}\\.[0-9]{1,8}$", RegexOptions.CultureInvariant);
}
