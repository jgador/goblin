using System;
using System.Text.Json.Serialization;
using Goblin.Core.Work;

namespace Goblin.Application.ChatGpt;

// Supplied by the authenticated HTTP adapter, never deserialized from tool arguments.
public sealed class ChatGptReader
{
    public ChatGptReader(string issuer, string subject, string resource, DateTimeOffset expiresAt)
    {
        Issuer = issuer;
        Subject = subject;
        Resource = resource;
        ExpiresAt = expiresAt;
    }

    public string Issuer { get; }
    public string Subject { get; }
    public string Resource { get; }
    public DateTimeOffset ExpiresAt { get; }
}

public sealed class SharedWork
{
    [JsonPropertyName("workId")]
    public string WorkId { get; init; } = "";

    [JsonPropertyName("objective")]
    public string Objective { get; init; } = "";

    [JsonPropertyName("status")]
    public WorkStatus Status { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class SharedWorkPage
{
    [JsonPropertyName("items")]
    public SharedWork[] Items { get; init; } = [];

    [JsonPropertyName("nextAfterWorkId")]
    public string? NextAfterWorkId { get; init; }
}

public sealed class SharedWorkAttention
{
    [JsonPropertyName("workId")]
    public string WorkId { get; init; } = "";

    [JsonPropertyName("reason")]
    public AttentionReason? Reason { get; init; }

    [JsonPropertyName("failure")]
    public FailureKind? Failure { get; init; }
}

public sealed class SharedWorkResult
{
    [JsonPropertyName("workId")]
    public string WorkId { get; init; } = "";

    [JsonPropertyName("text")]
    public string Text { get; init; } = "";

    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }
}

public sealed class ChatGptAccessDenied : Exception
{
    public ChatGptAccessDenied() : base("This Work or result is not shared with this connection. Ask the Goblin owner to review access.") { }
}
