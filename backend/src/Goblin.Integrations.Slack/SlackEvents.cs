using System;
using System.Text.RegularExpressions;
using Goblin.Contracts.Conversations;

namespace Goblin.Integrations.Slack;

public static class SlackEvents
{
    // Only authenticated Socket Mode envelopes from the verified app are input.
    public static ExternalMessage? Parse(SlackSocketEnvelope envelope, ExternalInstallation installation)
    {
        SlackEventPayload? payload = envelope.Payload;
        SlackMessageEvent? message = payload?.Event;
        if (envelope.Type != "events_api" || payload?.AppId != installation.AppId ||
            payload.TeamId != installation.WorkspaceId || message is null) return null;
        string type = message.Type, user = message.User, channel = message.Channel;
        bool direct = type == "message" && message.ChannelType == "im";
        if ((!direct && type != "app_mention") || !SlackApi.Id(user, 'U', 'W') || user == installation.BotUserId ||
            !SlackApi.Id(channel, 'D', 'C', 'G') || message.HasBotId || message.HasSubtype) return null;
        string text = message.Text.Replace("<@" + installation.BotUserId + ">", "", StringComparison.Ordinal).Trim();
        string timestamp = message.Timestamp, thread = message.ThreadTimestamp, id = payload.EventId;
        if (thread.Length == 0) thread = timestamp;
        if (text.Length is 0 or > 4000 || !Timestamp(timestamp) || !Timestamp(thread) || !Regex.IsMatch(id, "^Ev[A-Za-z0-9]{1,62}$", RegexOptions.CultureInvariant)) return null;
        return new(installation, id, user, channel, thread, timestamp, text, direct);
    }

    private static bool Timestamp(string value) => Regex.IsMatch(value, "^[0-9]{1,16}\\.[0-9]{1,8}$", RegexOptions.CultureInvariant);
}
