using System;
using System.Text.Json.Serialization;
using Goblin.Contracts;

namespace Goblin.Web.Http.Contracts;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ApiKeyAccountView), "apiKey")]
[JsonDerivedType(typeof(ChatGPTAccountView), "chatgpt")]
public abstract class AccountView
{
    protected AccountView() { }

    public static AccountView From(Goblin.Contracts.AccountView value) => value switch
    {
        Goblin.Contracts.ApiKeyAccountView => new ApiKeyAccountView(),
        Goblin.Contracts.ChatGPTAccountView account => ChatGPTAccountView.From(account),
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
}

public sealed class ApiKeyAccountView : AccountView
{
    [JsonConstructor]
    public ApiKeyAccountView()
    {
    }

    public static ApiKeyAccountView From(Goblin.Contracts.ApiKeyAccountView _) =>
        new();
}

public sealed class ChatGPTAccountView : AccountView
{
    [JsonConstructor]
    public ChatGPTAccountView(string? email, string? planType)
    {
        Email = email;
        PlanType = planType;
    }

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("planType")]
    public string? PlanType { get; init; }

    public static ChatGPTAccountView From(Goblin.Contracts.ChatGPTAccountView value) =>
        new(value.Email, value.PlanType);
}

public sealed class DeviceLogin
{
    [JsonConstructor]
    public DeviceLogin(string id, string verificationUrl, string userCode)
    {
        Id = id;
        VerificationUrl = verificationUrl;
        UserCode = userCode;
    }

    [JsonPropertyName("id")]
    public string Id { get; init; }

    [JsonPropertyName("verificationUrl")]
    public string VerificationUrl { get; init; }

    [JsonPropertyName("userCode")]
    public string UserCode { get; init; }

    public static DeviceLogin From(Goblin.Contracts.DeviceLogin value) =>
        new(value.Id, value.VerificationUrl, value.UserCode);
}

public sealed class Notice
{
    [JsonConstructor]
    public Notice(NoticeKind kind, string message)
    {
        Kind = kind;
        Message = message;
    }

    [JsonPropertyName("kind")]
    public NoticeKind Kind { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; }

    public static Notice From(Goblin.Contracts.Notice value) =>
        new(value.Kind, value.Message);
}

public sealed class AuthenticationState
{
    [JsonConstructor]
    public AuthenticationState(AccountView? account, DeviceLogin? login, Notice? notice, VerificationState? verification,
        bool runtimeReady)
    {
        Account = account;
        Login = login;
        Notice = notice;
        Verification = verification;
        RuntimeReady = runtimeReady;
    }

    [JsonPropertyName("account")]
    public AccountView? Account { get; init; }

    [JsonPropertyName("login")]
    public DeviceLogin? Login { get; init; }

    [JsonPropertyName("notice")]
    public Notice? Notice { get; init; }

    [JsonPropertyName("verification")]
    public VerificationState? Verification { get; init; }

    [JsonPropertyName("runtimeReady")]
    public bool RuntimeReady { get; init; }

    public static AuthenticationState From(Goblin.Contracts.AuthenticationState value) =>
        new(value.Account is null ? null : AccountView.From(value.Account),
            value.Login is null ? null : DeviceLogin.From(value.Login),
            value.Notice is null ? null : Notice.From(value.Notice), value.Verification, value.RuntimeReady);
}

public sealed class PromptResult
{
    [JsonConstructor]
    public PromptResult(string reply, string model, long durationMs, AuthenticationMethod authType)
    {
        Reply = reply;
        Model = model;
        DurationMs = durationMs;
        AuthType = authType;
    }

    [JsonPropertyName("reply")]
    public string Reply { get; init; }

    [JsonPropertyName("model")]
    public string Model { get; init; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; init; }

    [JsonPropertyName("authType")]
    public AuthenticationMethod AuthType { get; init; }

    public static PromptResult From(Goblin.Contracts.PromptResult value) =>
        new(value.Reply, value.Model, value.DurationMs, value.AuthType);
}

public sealed class RuntimeCapabilities
{
    [JsonConstructor]
    public RuntimeCapabilities(string runtime, bool textExecution, bool repositoryExecution, bool cancellation,
        bool liveApprovals, bool resumeSession)
    {
        Runtime = runtime;
        TextExecution = textExecution;
        RepositoryExecution = repositoryExecution;
        Cancellation = cancellation;
        LiveApprovals = liveApprovals;
        ResumeSession = resumeSession;
    }

    [JsonPropertyName("runtime")]
    public string Runtime { get; init; }

    [JsonPropertyName("textExecution")]
    public bool TextExecution { get; init; }

    [JsonPropertyName("repositoryExecution")]
    public bool RepositoryExecution { get; init; }

    [JsonPropertyName("cancellation")]
    public bool Cancellation { get; init; }

    [JsonPropertyName("liveApprovals")]
    public bool LiveApprovals { get; init; }

    [JsonPropertyName("resumeSession")]
    public bool ResumeSession { get; init; }

    public static RuntimeCapabilities From(Goblin.Contracts.Runtime.RuntimeCapabilities value) =>
        new(value.Runtime, value.TextExecution, value.RepositoryExecution, value.Cancellation, value.LiveApprovals,
            value.ResumeSession);
}
