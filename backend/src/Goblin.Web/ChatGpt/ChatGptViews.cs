using System;
using System.Text.Json.Serialization;
using Goblin.Integrations.ChatGpt;
using Env = Goblin.Contracts.Configuration.EnvironmentVariables;

namespace Goblin.Web;

internal sealed class ChatGptSetupView
{
    [JsonPropertyName("configured")]
    public bool Configured { get; init; }
    [JsonPropertyName("name")]
    public string Name => "Goblin";
    [JsonPropertyName("description")]
    public string Description => ChatGptConfiguration.Description;
    [JsonPropertyName("logoUrl")]
    public string LogoUrl => "/assets/branding/icon.svg";
    [JsonPropertyName("serverUrl")]
    public string? ServerUrl { get; init; }
    [JsonPropertyName("issuer")]
    public string? Issuer { get; init; }
    [JsonPropertyName("clientId")]
    public string ClientId => ChatGptConfiguration.ClientId;
    [JsonPropertyName("scope")]
    public string Scope => ChatGptConfiguration.AuthorizationScopes;

    [JsonPropertyName("ownerSubject")]
    public string? OwnerSubject { get; init; }

    [JsonPropertyName("clientConfigured")]
    public bool ClientConfigured { get; init; }

    [JsonPropertyName("redirectUri")]
    public string? RedirectUri { get; init; }

    [JsonPropertyName("environmentVariables")]
    public ChatGptEnvironmentView EnvironmentVariables { get; } = new();

    [JsonPropertyName("grants")]
    public ChatGptGrantView[] Grants { get; init; } = [];
}

internal sealed class ChatGptEnvironmentView
{
    [JsonPropertyName("publicOrigin")]
    public string PublicOrigin => Env.GoblinPublicOrigin;

    [JsonPropertyName("enabled")]
    public string Enabled => Env.GoblinChatGptEnabled;
}

internal sealed class ChatGptGrantView
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }
    [JsonPropertyName("subject")]
    public required string Subject { get; init; }
    [JsonPropertyName("workId")]
    public required string WorkId { get; init; }
    [JsonPropertyName("resultAttemptId")]
    public string? ResultAttemptId { get; init; }
    [JsonPropertyName("expiresAt")]
    public DateTimeOffset ExpiresAt { get; init; }
    [JsonPropertyName("expired")]
    public bool Expired { get; init; }
}

internal sealed class ChatGptWorkChoice
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }
    [JsonPropertyName("objective")]
    public required string Objective { get; init; }
}

internal sealed class ChatGptVerificationView
{
    [JsonPropertyName("discovery")]
    public bool Discovery { get; init; }
    [JsonPropertyName("database")]
    public bool Database { get; init; }
    [JsonPropertyName("issuerResponses")]
    public bool IssuerResponses { get; init; }
    [JsonPropertyName("checkedAt")]
    public DateTimeOffset CheckedAt { get; init; }
}
