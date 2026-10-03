using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Goblin.Integrations.Slack;

public sealed class SlackSocketEnvelope
{
    [JsonPropertyName("type")]
    [JsonConverter(typeof(SlackStringConverter))]
    public string Type { get; init; } = "";

    [JsonPropertyName("envelope_id")]
    [JsonConverter(typeof(SlackStringConverter))]
    public string EnvelopeId { get; init; } = "";

    [JsonPropertyName("connection_info")]
    public SlackConnectionInfo? ConnectionInfo { get; init; }

    [JsonPropertyName("payload")]
    public SlackEventPayload? Payload { get; init; }
}

public sealed class SlackConnectionInfo
{
    [JsonPropertyName("app_id")]
    [JsonConverter(typeof(SlackStringConverter))]
    public string AppId { get; init; } = "";
}

public sealed class SlackEventPayload
{
    [JsonPropertyName("api_app_id")]
    [JsonConverter(typeof(SlackStringConverter))]
    public string AppId { get; init; } = "";

    [JsonPropertyName("team_id")]
    [JsonConverter(typeof(SlackStringConverter))]
    public string TeamId { get; init; } = "";

    [JsonPropertyName("event_id")]
    [JsonConverter(typeof(SlackStringConverter))]
    public string EventId { get; init; } = "";

    [JsonPropertyName("event")]
    public SlackMessageEvent? Event { get; init; }
}

public sealed class SlackMessageEvent
{
    // Presence, including explicit null, marks bot/subtype messages as unsupported.
    [JsonPropertyName("bot_id")]
    [JsonConverter(typeof(SlackStringConverter))]
    public string? BotId { get; init { field = value; HasBotId = true; } }

    [JsonIgnore]
    public bool HasBotId { get; private init; }

    [JsonPropertyName("subtype")]
    [JsonConverter(typeof(SlackStringConverter))]
    public string? Subtype { get; init { field = value; HasSubtype = true; } }

    [JsonIgnore]
    public bool HasSubtype { get; private init; }

    [JsonPropertyName("type")]
    [JsonConverter(typeof(SlackStringConverter))]
    public string Type { get; init; } = "";

    [JsonPropertyName("user")]
    [JsonConverter(typeof(SlackStringConverter))]
    public string User { get; init; } = "";

    [JsonPropertyName("channel")]
    [JsonConverter(typeof(SlackStringConverter))]
    public string Channel { get; init; } = "";

    [JsonPropertyName("channel_type")]
    [JsonConverter(typeof(SlackStringConverter))]
    public string ChannelType { get; init; } = "";

    [JsonPropertyName("text")]
    [JsonConverter(typeof(SlackStringConverter))]
    public string Text { get; init; } = "";

    [JsonPropertyName("ts")]
    [JsonConverter(typeof(SlackStringConverter))]
    public string Timestamp { get; init; } = "";

    [JsonPropertyName("thread_ts")]
    [JsonConverter(typeof(SlackStringConverter))]
    public string ThreadTimestamp { get; init; } = "";
}

public sealed class SlackSetupRecovery
{
    [JsonPropertyName("appId")]
    [JsonConverter(typeof(SlackStringConverter))]
    public string? AppId { get; init; }
}

public sealed class SlackRuntimeTokens
{
    [JsonPropertyName("appToken")]
    [JsonConverter(typeof(SlackStringConverter))]
    public string AppToken { get; init; } = "";

    [JsonPropertyName("botToken")]
    [JsonConverter(typeof(SlackStringConverter))]
    public string BotToken { get; init; } = "";
}

public sealed class SlackProfileAccount
{
    [JsonPropertyName("token")]
    [JsonConverter(typeof(SlackStringConverter))]
    public string Token { get; init; } = "";
}

public sealed class SlackCliApps
{
    [JsonPropertyName("apps")]
    public Dictionary<string, SlackCliApp>? Apps { get; init; }
}

public sealed class SlackCliApp
{
    [JsonPropertyName("app_id")]
    [JsonConverter(typeof(SlackStringConverter))]
    public string AppId { get; init; } = "";
}

internal sealed class SlackCliHooksResponse
{
    [JsonPropertyName("hooks")]
    public required SlackCliHooks Hooks { get; init; }

    [JsonPropertyName("config")]
    public required SlackCliConfig Config { get; init; }
}

internal sealed class SlackCliHooks
{
    [JsonPropertyName("get-manifest")]
    public required string GetManifest { get; init; }

    [JsonPropertyName("deploy")]
    public required string Deploy { get; init; }
}

internal sealed class SlackCliConfig
{
    [JsonPropertyName("sdk-managed-connection-enabled")]
    public required bool SdkManagedConnectionEnabled { get; init; }
}

public class SlackResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }
}

internal sealed class SlackAuthResponse : SlackResponse
{
    [JsonPropertyName("team_id")]
    [JsonConverter(typeof(SlackStringConverter))]
    public string TeamId { get; init; } = "";

    [JsonPropertyName("user_id")]
    [JsonConverter(typeof(SlackStringConverter))]
    public string UserId { get; init; } = "";

    [JsonPropertyName("bot_id")]
    [JsonConverter(typeof(SlackStringConverter))]
    public string BotId { get; init; } = "";

    [JsonPropertyName("team")]
    [JsonConverter(typeof(SlackStringConverter))]
    public string Team { get; init; } = "";
}

internal sealed class SlackBotResponse : SlackResponse
{
    [JsonPropertyName("bot")]
    public SlackConnectionInfo? Bot { get; init; }
}

internal sealed class SlackOpenResponse : SlackResponse
{
    [JsonPropertyName("url")]
    [JsonConverter(typeof(SlackStringConverter))]
    public string Url { get; init; } = "";
}

internal sealed class SlackAcknowledgement
{
    public SlackAcknowledgement(string envelopeId)
    {
        EnvelopeId = envelopeId;
    }

    [JsonPropertyName("envelope_id")]
    public string EnvelopeId { get; init; }
}

internal sealed class SlackPostMessageRequest
{
    public SlackPostMessageRequest(string channel, string threadTimestamp, string text)
    {
        Channel = channel;
        ThreadTimestamp = threadTimestamp;
        Text = text;
    }

    [JsonPropertyName("channel")]
    public string Channel { get; init; }

    [JsonPropertyName("thread_ts")]
    public string ThreadTimestamp { get; init; }

    [JsonPropertyName("text")]
    public string Text { get; init; }

    public KeyValuePair<string, string>[] Form() =>
    [
        new("channel", Channel), new("thread_ts", ThreadTimestamp), new("text", Text),
        new("unfurl_links", "false"), new("unfurl_media", "false"), new("parse", "none")
    ];
}

internal sealed class SlackStringConverter : JsonConverter<string>
{
    public override bool HandleNull => true;

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return reader.GetString()!;
        reader.Skip();
        return "";
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}
