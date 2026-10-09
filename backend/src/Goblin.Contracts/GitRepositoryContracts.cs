using System.Threading;
using System.Threading.Tasks;
using Goblin.Core.Work;

namespace Goblin.Contracts.Runtime;

public sealed record GitHubState(bool Configured, string? Login, string? UserCode,
    string? VerificationUrl, string? Notice, GitHubConnectionStatus Status = GitHubConnectionStatus.Disconnected, GitRepositoryAccount? Account = null);

public interface IGitHubConnection : IGitRepositoryCatalog
{
    Task<GitHubState> StatusAsync();
    Task<GitHubState> StartAsync();
    Task<GitHubState> DisconnectAsync();
    Task<GitHubState> CheckAsync(CancellationToken token = default);
}

public sealed class GitRepositoryAccount
{
    public GitRepositoryAccount(string generation, string accountId, string login)
    {
        Generation = generation;
        AccountId = accountId;
        Login = login;
    }

    public string Generation { get; init; }

    public string AccountId { get; init; }

    public string Login { get; init; }
}

public sealed class GitRepositoryInfo
{
    public GitRepositoryInfo(long id, string name, string defaultBranch, bool canPush)
    {
        Id = id;
        Name = name;
        DefaultBranch = defaultBranch;
        CanPush = canPush;
    }

    public long Id { get; init; }

    public string Name { get; init; }

    public string DefaultBranch { get; init; }

    public bool CanPush { get; init; }
}

public sealed class GitRepositoryOperationResult
{
    public GitRepositoryOperationResult(string? commit, string? url)
    {
        Commit = commit;
        Url = url;
    }

    public string? Commit { get; init; }

    public string? Url { get; init; }
}

// Trusted application-side discovery. Workers never receive this connection.
public interface IGitRepositoryCatalog
{
    Task<GitRepositoryAccount?> GetAccountAsync(CancellationToken token);

    Task<GitRepositoryInfo> GitRepositoryAsync(string name, CancellationToken token);

    Task<GitRepositoryInfo[]> GitRepositoriesAsync(int page, CancellationToken token);
}

public interface IGitRepositoryRemote
{
    Task PrepareCheckpointAsync(GitRepositoryChange gitRepository, string directory, WorkspaceCheckpoint checkpoint, CancellationToken token) =>
        throw new System.NotSupportedException("Git checkpoint preparation is unavailable.");

    Task PrepareAsync(GitRepositoryChange gitRepository, string directory, string? checkpoint, CancellationToken token);

    Task<string> InspectBundleAsync(GitRepositoryChange gitRepository, string directory, string bundle, CancellationToken token);

    Task<GitRepositoryOperationResult> ExecuteAsync(GitRepositoryChange gitRepository, string directory, GitRepositoryOperationKind operation, string commit, CancellationToken token);

    Task<GitRepositoryOperationResult?> ReconcileAsync(GitRepositoryChange gitRepository, string directory, GitRepositoryOperationKind operation, string commit, CancellationToken token);
}

public interface IGitRepositoryBroker
{
    Task<string> PrepareAsync(WorkSnapshot work, CancellationToken token);

    Task<ExecutionObservation?> ObserveAsync(WorkSnapshot work, CancellationToken token);

    Task StopAsync(WorkSnapshot work, CancellationToken token);

    Task ReleaseAsync(WorkSnapshot work, CancellationToken token);
}
