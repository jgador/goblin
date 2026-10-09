using System.Text.Json.Serialization;
using Goblin.Contracts;

namespace Goblin.Web.Http.Contracts;

public sealed class GitHubState
{
    [JsonConstructor]
    public GitHubState(bool configured, string? login, string? userCode, string? verificationUrl, string? notice,
        GitHubConnectionStatus status = GitHubConnectionStatus.Disconnected, GitRepositoryAccount? account = null)
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
    public GitHubConnectionStatus Status { get; init; }

    [JsonPropertyName("account")]
    public GitRepositoryAccount? Account { get; init; }

    public static GitHubState From(Goblin.Contracts.Runtime.GitHubState value) =>
        new(value.Configured, value.Login, value.UserCode, value.VerificationUrl, value.Notice, value.Status,
            value.Account is null ? null : GitRepositoryAccount.From(value.Account));
}

public sealed class EnabledGitRepository
{
    [JsonConstructor]
    public EnabledGitRepository(long id, string name, string defaultBranch, bool enabled)
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

    public static EnabledGitRepository From(Goblin.Application.Work.EnabledGitRepository value) =>
        new(value.Id, value.Name, value.DefaultBranch, value.Enabled);
}

public sealed class GitRepositoryAccount
{
    [JsonConstructor]
    public GitRepositoryAccount(string generation, string accountId, string login)
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

    public static GitRepositoryAccount From(Goblin.Contracts.Runtime.GitRepositoryAccount value) =>
        new(value.Generation, value.AccountId, value.Login);
}

public sealed class GitRepositoryInfo
{
    [JsonConstructor]
    public GitRepositoryInfo(long id, string name, string defaultBranch, bool canPush)
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

    public static GitRepositoryInfo From(Goblin.Contracts.Runtime.GitRepositoryInfo value) =>
        new(value.Id, value.Name, value.DefaultBranch, value.CanPush);
}
