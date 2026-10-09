using System;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.Work;
using Goblin.Contracts.Runtime;

namespace Goblin.Application.Connections;

public sealed class GitHubConnectionService : IDisposable
{
    private readonly IGitHubConnection _connection;
    private readonly GitHubStore? _store;
    // One service per application serializes connection observations and changes.
    private readonly SemaphoreSlim _gate = new(1);

    public GitHubConnectionService(IGitHubConnection connection) => _connection = connection;

    public GitHubConnectionService(IGitHubConnection connection, GitHubStore store) : this(connection) => _store = store;

    public Task<GitHubState> StatusAsync(CancellationToken token) => RunAsync(_connection.StatusAsync, token);

    public Task<GitHubState> ConnectAsync(CancellationToken token) => RunChangeAsync(_connection.StartAsync, token);

    public Task<GitHubState> DisconnectAsync(CancellationToken token) => RunChangeAsync(_connection.DisconnectAsync, token);

    public Task<GitHubState> CancelAsync(CancellationToken token) => RunChangeAsync(_connection.DisconnectAsync, token);

    public Task<GitHubState> CheckAsync(CancellationToken token) => RunAsync(() => _connection.CheckAsync(token), token);

    public Task<EnabledGitRepository[]> GitRepositoriesAsync(CancellationToken token) => Store.GitRepositoriesAsync(token);

    public Task<GitRepositoryInfo[]> AvailableGitRepositoriesAsync(int page, CancellationToken token) =>
        _connection.GitRepositoriesAsync(page, token);

    public async Task<EnabledGitRepository[]> SetGitRepositoryAsync(string repository, bool enabled, CancellationToken token)
    {
        GitRepositoryAccount account = (await _connection.StatusAsync()).Account
            ?? throw new ApplicationFailure("github_connection_required");
        GitRepositoryInfo selected = await _connection.GitRepositoryAsync(repository, token);
        await Store.SetGitRepositoryAsync(selected, enabled, account.Generation, token);
        return await Store.GitRepositoriesAsync();
    }

    private GitHubStore Store => _store ?? throw new InvalidOperationException("Durable Work is disabled.");

    private Task<GitHubState> RunChangeAsync(Func<Task<GitHubState>> action, CancellationToken token) => RunAsync(async () =>
    {
        if (_store is not null) await _store.BeginChangeAsync(token);
        return await action();
    }, token);

    private async Task<GitHubState> RunAsync(Func<Task<GitHubState>> action, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            GitHubState state = await action();
            if (_store is not null) await _store.ObserveAsync(state.Account, state.Status);
            return state;
        }
        finally { _gate.Release(); }
    }

    public void Dispose() => _gate.Dispose();
}
