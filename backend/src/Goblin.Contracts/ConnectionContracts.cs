using System.Text.Json.Serialization;

namespace Goblin.Contracts;

// Goblin's public HTTP contract intentionally exposes only account summaries and final replies.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ApiKeyAccountView), "apiKey")]
[JsonDerivedType(typeof(ChatGPTAccountView), "chatgpt")]
public abstract class AccountView
{ }

public sealed class ApiKeyAccountView : AccountView
{ }

public sealed class ChatGPTAccountView : AccountView
{
    public ChatGPTAccountView(string? email, string? planType)
    {
        Email = email;
        PlanType = planType;
    }

    public string? Email { get; init; }

    public string? PlanType { get; init; }
}

public sealed class DeviceLogin
{
    public DeviceLogin(string id, string verificationUrl, string userCode)
    {
        Id = id;
        VerificationUrl = verificationUrl;
        UserCode = userCode;
    }

    public string Id { get; init; }

    public string VerificationUrl { get; init; }

    public string UserCode { get; init; }
}

public sealed class Notice
{
    public Notice(NoticeKind kind, string message)
    {
        Kind = kind;
        Message = message;
    }

    public NoticeKind Kind { get; init; }

    public string Message { get; init; }
}

public sealed class AuthenticationState
{
    public AuthenticationState(AccountView? account, DeviceLogin? login, Notice? notice,
        VerificationState? verification, bool runtimeReady)
    {
        Account = account;
        Login = login;
        Notice = notice;
        Verification = verification;
        RuntimeReady = runtimeReady;
    }

    public AccountView? Account { get; init; }

    public DeviceLogin? Login { get; init; }

    public Notice? Notice { get; init; }

    public VerificationState? Verification { get; init; }

    public bool RuntimeReady { get; init; }
}

public sealed class PromptResult
{
    public PromptResult(string reply, string model, long durationMs, AuthenticationMethod authType)
    {
        Reply = reply;
        Model = model;
        DurationMs = durationMs;
        AuthType = authType;
    }

    public string Reply { get; init; }

    public string Model { get; init; }

    public long DurationMs { get; init; }

    public AuthenticationMethod AuthType { get; init; }
}
