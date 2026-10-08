using System;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.Runtime;
using Xunit;

namespace Goblin.Application.Tests;

public sealed class ModelCatalogRefreshTests
{
    [Fact]
    public async Task ShutdownCancelsAndJoinsDiscoveryWhileRejectingNewRefreshes()
    {
        using var refreshes = new ModelCatalogRefreshCoordinator();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int duplicateCalls = 0;
        refreshes.Schedule(1, async token =>
        {
            started.SetResult();
            using var registration = token.Register(() => cancelled.SetResult());
            await released.Task;
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task stopping = refreshes.StopAsync(default);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(stopping.IsCompleted);
        refreshes.Schedule(2, _ => { Interlocked.Increment(ref duplicateCalls); return Task.CompletedTask; });
        Assert.False(refreshes.IsRefreshing(2));
        released.SetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(refreshes.IsRefreshing(1));
        Assert.Equal(0, duplicateCalls);
    }

    [Fact]
    public async Task ConcurrentRequestsCoalescePerConnectionAndAllowIndependentDiscovery()
    {
        using var refreshes = new ModelCatalogRefreshCoordinator();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int duplicateCalls = 0;
        refreshes.Schedule(1, async _ => { first.SetResult(); await released.Task; });
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (int i = 0; i < 10; i++)
            refreshes.Schedule(1, _ => { Interlocked.Increment(ref duplicateCalls); return Task.CompletedTask; });
        refreshes.Schedule(2, async _ => { second.SetResult(); await released.Task; });
        await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(refreshes.IsRefreshing(1));
        Assert.True(refreshes.IsRefreshing(2));
        released.SetResult();
        await refreshes.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(refreshes.IsRefreshing(1));
        Assert.False(refreshes.IsRefreshing(2));
        Assert.Equal(0, duplicateCalls);
    }
}
