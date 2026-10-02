using System.Text.Json.Serialization;

namespace Goblin.Integrations.GitHub;

public sealed class GitHubUserResponse
{
    [JsonPropertyName("id")]
    [JsonRequired]
    public long Id { get; init; }

    [JsonPropertyName("login")]
    [JsonRequired]
    public string Login { get; init; } = null!;
}

public sealed class GitHubRepositoryResponse
{
    [JsonPropertyName("id")]
    [JsonRequired]
    public long Id { get; init; }

    [JsonPropertyName("full_name")]
    [JsonRequired]
    public string FullName { get; init; } = null!;

    [JsonPropertyName("default_branch")]
    [JsonRequired]
    public string DefaultBranch { get; init; } = null!;

    [JsonPropertyName("permissions")]
    public GitHubRepositoryPermissions? Permissions { get; init; }
}

public sealed class GitHubRepositoryPermissions
{
    [JsonPropertyName("push")]
    public bool Push { get; init; }
}

public sealed class GitHubPullRequestResponse
{
    [JsonPropertyName("html_url")]
    [JsonRequired]
    public string? HtmlUrl { get; init; }
}
