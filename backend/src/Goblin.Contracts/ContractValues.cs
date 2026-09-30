using System.Text.Json.Serialization;

namespace Goblin.Contracts;

// These are closed Goblin contracts. Opaque runtime/model IDs remain strings.
public sealed class ContractEnumJsonConverter<T> : JsonStringEnumConverter<T> where T : struct, System.Enum
{
    public ContractEnumJsonConverter() : base(allowIntegerValues: false) { }
}

[JsonConverter(typeof(ContractEnumJsonConverter<NoticeKind>))]
public enum NoticeKind
{
    [JsonStringEnumMemberName("error")]
    Error,

    [JsonStringEnumMemberName("info")]
    Info
}

[JsonConverter(typeof(ContractEnumJsonConverter<VerificationState>))]
public enum VerificationState
{
    [JsonStringEnumMemberName("accepted")]
    Accepted,

    [JsonStringEnumMemberName("unverified")]
    Unverified
}

[JsonConverter(typeof(ContractEnumJsonConverter<AuthenticationMethod>))]
public enum AuthenticationMethod
{
    [JsonStringEnumMemberName("apiKey")]
    ApiKey,

    [JsonStringEnumMemberName("chatgpt")]
    ChatGPT
}

[JsonConverter(typeof(ContractEnumJsonConverter<GitHubConnectionStatus>))]
public enum GitHubConnectionStatus
{
    Disconnected,
    Connecting,
    Connected,
    Unavailable
}

[JsonConverter(typeof(ContractEnumJsonConverter<ConnectionAvailability>))]
public enum ConnectionAvailability
{
    Disconnected,
    Available,
    Unavailable,
    Changing,
    Verifying
}

[JsonConverter(typeof(ContractEnumJsonConverter<RepositoryOperationState>))]
public enum RepositoryOperationState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Uncertain
}

public static class RuntimeIds
{
    public const string Codex = "codex";
}

// Reverse-engineered database entities retain text columns. Parse only at that boundary.
public static class ContractValue
{
    public static T Parse<T>(string value) where T : struct, System.Enum =>
        System.Enum.TryParse<T>(value, out T parsed) && System.Enum.IsDefined(parsed) && parsed.ToString() == value
            ? parsed : throw new System.InvalidOperationException($"Invalid stored {typeof(T).Name}.");
}
