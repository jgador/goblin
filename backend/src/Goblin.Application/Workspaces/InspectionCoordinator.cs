using System;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;

namespace Goblin.Application.Workspaces;

public sealed class InspectionCoordinator : BackgroundService
{
    private readonly InspectionStore _store;
    private readonly IInspectionHost _host;
    private readonly IServiceScopeFactory _scopes;

    public InspectionCoordinator(InspectionStore store, IInspectionHost host, IServiceScopeFactory scopes)
    {
        _store = store;
        _host = host;
        _scopes = scopes;
    }

    public async Task StartAsync(long id, CancellationToken token)
    {
        InspectionAllocation? allocation = await _store.ClaimAsync(id, token);
        if (allocation is null) return;
        try { await _host.StartAsync(allocation, token); }
        catch { await _store.ObserveAsync(id, InspectionObservation.Failed, CancellationToken.None); }
    }

    public async Task StopAsync(long id, CancellationToken token)
    {
        InspectionAllocation allocation = await _store.GetAsync(id, token);
        await _host.StopAsync(allocation, token);
        await _store.ObserveAsync(id, await _host.ObserveAsync(allocation, token), token);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        do
        {
            try
            {
                foreach (InspectionView session in await _store.PendingAsync(stoppingToken))
                {
                    try
                    {
                        if (session.State == InspectionState.Queued) await PublishStartAsync(session.Id);
                        else if (session.State == InspectionState.Stopping) await StopAsync(session.Id, stoppingToken);
                        else await _store.ObserveAsync(session.Id,
                            await _host.ObserveAsync(await _store.GetAsync(session.Id, stoppingToken), stoppingToken), stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                    catch { /* Unknown control-plane state retains its capacity reservation. */ }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch { /* A storage outage cannot authorize a new allocation. */ }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PublishStartAsync(long id)
    {
        // Wolverine's bus is scoped because it carries the active message
        // context. Keep that scope boundary limited to publication.
        using IServiceScope scope = _scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMessageBus>().PublishAsync(new StartInspection(id));
    }
}

public static class InspectionHandler
{
    public static Task Handle(StartInspection command, InspectionCoordinator coordinator, CancellationToken token) => coordinator.StartAsync(command.Id, token);

    public static Task Handle(StopInspection command, InspectionCoordinator coordinator, CancellationToken token) => coordinator.StopAsync(command.Id, token);
}
