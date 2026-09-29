using System.Text.Json.Serialization;

namespace Goblin.Web.Http.Contracts;

public sealed class GitHubState
{
    [JsonConstructor]
    public GitHubState(bool configured, string? login, string? userCode, string? verificationUrl, string? notice,
        string status = "Disconnected", RepositoryAccount? account = null)
    {
        Configured = configured;
        Login = login;
        UserCode = userCode;
        VerificationUrl = verificationUrl;
        Notice = notice;
        Status = status;
        Account = account;
    }

    [JsonPropertyName("configured")]
    public bool Configured { get; init; }

    [JsonPropertyName("login")]
    public string? Login { get; init; }

    [JsonPropertyName("userCode")]
    public string? UserCode { get; init; }

    [JsonPropertyName("verificationUrl")]
    public string? VerificationUrl { get; init; }

    [JsonPropertyName("notice")]
    public string? Notice { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; }

    [JsonPropertyName("account")]
    public RepositoryAccount? Account { get; init; }

    public static GitHubState From(Goblin.Integrations.GitHub.GitHubState value) =>
        new(value.Configured, value.Login, value.UserCode, value.VerificationUrl, value.Notice, value.Status,
            value.Account is null ? null : RepositoryAccount.From(value.Account));
}

public sealed class EnabledRepository
{
    [JsonConstructor]
    public EnabledRepository(long id, string name, string defaultBranch, bool enabled)
    {
        Id = id;
        Name = name;
        DefaultBranch = defaultBranch;
        Enabled = enabled;
    }

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; }

    [JsonPropertyName("defaultBranch")]
    public string DefaultBranch { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    public static EnabledRepository From(Goblin.Application.Work.EnabledRepository value) =>
        new(value.Id, value.Name, value.DefaultBranch, value.Enabled);
}

public sealed class RepositoryAccount
{
    [JsonConstructor]
    public RepositoryAccount(string generation, string accountId, string login)
    {
        Generation = generation;
        AccountId = accountId;
        Login = login;
    }

    [JsonPropertyName("generation")]
    public string Generation { get; init; }

    [JsonPropertyName("accountId")]
    public string AccountId { get; init; }

    [JsonPropertyName("login")]
    public string Login { get; init; }

    public static RepositoryAccount From(Goblin.Contracts.Runtime.RepositoryAccount value) =>
        new(value.Generation, value.AccountId, value.Login);
}

public sealed class RepositoryInfo
{
    [JsonConstructor]
    public RepositoryInfo(long id, string name, string defaultBranch, bool canPush)
    {
        Id = id;
        Name = name;
        DefaultBranch = defaultBranch;
        CanPush = canPush;
    }

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; }

    [JsonPropertyName("defaultBranch")]
    public string DefaultBranch { get; init; }

    [JsonPropertyName("canPush")]
    public bool CanPush { get; init; }

    public static RepositoryInfo From(Goblin.Contracts.Runtime.RepositoryInfo value) =>
        new(value.Id, value.Name, value.DefaultBranch, value.CanPush);
}
