using System.Threading;
using System.Threading.Tasks;
using Goblin.Core.Work;

namespace Goblin.Contracts.Runtime;

public sealed class RepositoryAccount
{
    public RepositoryAccount(string generation, string accountId, string login)
    {
        Generation = generation;
        AccountId = accountId;
        Login = login;
    }

    public string Generation { get; init; }

    public string AccountId { get; init; }

    public string Login { get; init; }
}

public sealed class RepositoryInfo
{
    public RepositoryInfo(long id, string name, string defaultBranch, bool canPush)
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

public sealed class RepositoryOperationResult
{
    public RepositoryOperationResult(string? commit, string? url)
    {
        Commit = commit;
        Url = url;
    }

    public string? Commit { get; init; }

    public string? Url { get; init; }
}

// Trusted application-side discovery. Workers never receive this connection.
public interface IRepositoryCatalog
{
    Task<RepositoryAccount?> GetAccountAsync(CancellationToken token);

    Task<RepositoryInfo> RepositoryAsync(string name, CancellationToken token);

    Task<RepositoryInfo[]> RepositoriesAsync(int page, CancellationToken token);
}

public interface IRepositoryRemote
{
    Task PrepareCheckpointAsync(RepositoryChange repository, string directory, WorkspaceCheckpoint checkpoint, CancellationToken token) =>
        throw new System.NotSupportedException("Git checkpoint preparation is unavailable.");

    Task PrepareAsync(RepositoryChange repository, string directory, string? checkpoint, CancellationToken token);

    Task<string> InspectBundleAsync(RepositoryChange repository, string directory, string bundle, CancellationToken token);

    Task<RepositoryOperationResult> ExecuteAsync(RepositoryChange repository, string directory, RepositoryOperationKind operation, string commit, CancellationToken token);

    Task<RepositoryOperationResult?> ReconcileAsync(RepositoryChange repository, string directory, RepositoryOperationKind operation, string commit, CancellationToken token);
}

public interface IRepositoryBroker
{
    Task<string> PrepareAsync(WorkSnapshot work, CancellationToken token);

    Task<ExecutionObservation?> ObserveAsync(WorkSnapshot work, CancellationToken token);

    Task StopAsync(WorkSnapshot work, CancellationToken token);

    Task ReleaseAsync(WorkSnapshot work, CancellationToken token);
}
