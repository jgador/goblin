using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Goblin.Web;

internal sealed class UnlockRequest
{
    [JsonPropertyName("password")]
    [JsonConverter(typeof(RequestStringConverter))]
    public string? Password { get; init; }
}

internal sealed class ApiKeyRequest
{
    [JsonPropertyName("apiKey")]
    [JsonConverter(typeof(RequestStringConverter))]
    public string? ApiKey { get; init; }
}

internal sealed class PromptRequest
{
    [JsonPropertyName("prompt")]
    [JsonConverter(typeof(RequestStringConverter))]
    public string? Prompt { get; init; }
}

internal sealed class TimeZoneRequest
{
    [JsonPropertyName("timeZone")]
    [JsonConverter(typeof(RequestStringConverter))]
    public string? TimeZone { get; init; }

    [JsonPropertyName("expectedTimeZone")]
    [JsonConverter(typeof(RequestStringConverter))]
    public string? ExpectedTimeZone { get; init; }
}

internal sealed class GitRepositorySelectionRequest
{
    [JsonPropertyName("repository")]
    [JsonConverter(typeof(RequestStringConverter))]
    public string? GitRepository { get; init; }

    [JsonPropertyName("enabled")]
    [JsonConverter(typeof(RequestStringConverter))]
    public string? Enabled { get; init; }
}

internal sealed class SlackConfirmationRequest
{
    [JsonPropertyName("code")]
    [JsonConverter(typeof(RequestStringConverter))]
    public string? Code { get; init; }
}

internal sealed class SlackConnectRequest
{
    [JsonPropertyName("appToken")]
    [JsonConverter(typeof(RequestStringConverter))]
    public string? AppToken { get; init; }

    [JsonPropertyName("botToken")]
    [JsonConverter(typeof(RequestStringConverter))]
    public string? BotToken { get; init; }
}

internal sealed class SlackIdentityRequest
{
    [JsonPropertyName("id")]
    [JsonConverter(typeof(RequestStringConverter))]
    public string? Id { get; init; }
}

// Small form-like endpoints historically treat non-string values as missing.
// Keep that behavior without introducing a DOM into their request contracts.
internal sealed class RequestStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return reader.GetString();
        reader.Skip();
        return null;
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}
