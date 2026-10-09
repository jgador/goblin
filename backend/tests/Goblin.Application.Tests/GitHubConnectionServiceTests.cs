using System;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.Connections;
using Goblin.Contracts.Runtime;
using Xunit;

namespace Goblin.Application.Tests;

public sealed class GitHubConnectionServiceTests
{
    [Fact]
    public async Task CancelledWaitAndFailedOperationReleaseTheSharedConnectionGate()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new Connection(async () =>
        {
            entered.SetResult();
            await release.Task;
            throw new InvalidOperationException("Test failure");
        });
        using var service = new GitHubConnectionService(connection);
        Task<GitHubState> checking = service.CheckAsync(default);
        await entered.Task;
        using var cancelled = new CancellationTokenSource();
        Task<GitHubState> waiting = service.StatusAsync(cancelled.Token);
        Assert.Equal(0, connection.StatusCalls);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        release.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => checking);
        GitHubState state = await service.StatusAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, connection.StatusCalls);
        Assert.Null(state.Account);
    }

    private sealed class Connection : IGitHubConnection
    {
        private readonly Func<Task<GitHubState>> _check;

        public Connection(Func<Task<GitHubState>> check) => _check = check;

        public int StatusCalls { get; private set; }

        public Task<GitHubState> StatusAsync()
        {
            StatusCalls++;
            return Task.FromResult(new GitHubState(true, null, null, null, null));
        }

        public Task<GitHubState> CheckAsync(CancellationToken token = default) => _check();
        public Task<GitHubState> StartAsync() => throw new NotSupportedException();
        public Task<GitHubState> DisconnectAsync() => throw new NotSupportedException();
        public Task<GitRepositoryAccount?> GetAccountAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<GitRepositoryInfo> GitRepositoryAsync(string name, CancellationToken token) => throw new NotSupportedException();
        public Task<GitRepositoryInfo[]> GitRepositoriesAsync(int page, CancellationToken token) => throw new NotSupportedException();
    }
}
