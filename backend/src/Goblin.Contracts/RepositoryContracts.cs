using System.Threading;
using System.Threading.Tasks;
using Goblin.Core.Work;

namespace Goblin.Contracts.Runtime;

public sealed record RepositoryAccount(string Generation, string AccountId, string Login);
public sealed record RepositoryInfo(long Id, string Name, string DefaultBranch, bool CanPush);
public sealed record RepositoryOperationResult(string? Commit, string? Url);

public interface IRepositoryRemote
{
    Task PrepareAsync(RepositoryChange repository, string directory, string? checkpoint, CancellationToken token);
    Task<string> InspectBundleAsync(RepositoryChange repository, string directory, string bundle, CancellationToken token);
    Task<RepositoryOperationResult> ExecuteAsync(RepositoryChange repository, string directory, string operation, string commit, CancellationToken token);
    Task<RepositoryOperationResult?> ReconcileAsync(RepositoryChange repository, string directory, string operation, string commit, CancellationToken token);
}

public interface IRepositoryBroker
{
    Task<string> PrepareAsync(WorkSnapshot work, CancellationToken token);
    Task<ExecutionObservation?> ObserveAsync(WorkSnapshot work, CancellationToken token);
    Task StopAsync(WorkSnapshot work, CancellationToken token);
    Task ReleaseAsync(WorkSnapshot work, CancellationToken token);
}
