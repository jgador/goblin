using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Goblin.Application.Runtime;

// Refreshes belong to the controller lifetime, independently of the request
// that notices stale data. Shutdown closes admission, cancels and joins them.
public sealed class ModelCatalogRefreshCoordinator : IHostedService, IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<long, Task> _refreshes = [];
    private readonly CancellationTokenSource _shutdown = new();
    private bool _stopping;
    private bool _disposed;

    public ModelCatalogRefreshCoordinator() { }

    public bool IsRefreshing(long connectionId)
    {
        lock (_gate) return _refreshes.ContainsKey(connectionId);
    }

    public void Schedule(long connectionId, Func<CancellationToken, Task> refresh)
    {
        lock (_gate)
        {
            if (_stopping || _refreshes.ContainsKey(connectionId)) return;
            CancellationToken token = _shutdown.Token;
            _refreshes[connectionId] = Task.Run(async () =>
            {
                try { await refresh(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch { /* Database recovery remains owned by the normal health path. */ }
                finally
                {
                    lock (_gate) _refreshes.Remove(connectionId);
                }
            });
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task[] refreshes;
        lock (_gate)
        {
            _stopping = true;
            refreshes = [.. _refreshes.Values];
        }
        await _shutdown.CancelAsync();
        await Task.WhenAll(refreshes).WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stopping = true;
        }
        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}
