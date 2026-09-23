using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;

namespace Goblin.Application.Workspaces;

public sealed class InspectionCoordinator : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IInspectionHost _host;
    public InspectionCoordinator(IServiceScopeFactory scopes, IInspectionHost host) { _scopes = scopes; _host = host; }
    public async Task StartAsync(long id, CancellationToken token)
    {
        using IServiceScope scope = _scopes.CreateScope();
        InspectionStore store = scope.ServiceProvider.GetRequiredService<InspectionStore>();
        string capability = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        InspectionAllocation? allocation = await store.ClaimAsync(id, capability, token);
        if (allocation is null) return;
        try { await _host.StartAsync(allocation, capability, token); }
        catch { await store.ObserveAsync(id, "Failed", CancellationToken.None); }
    }
    public async Task StopAsync(long id, CancellationToken token)
    {
        using IServiceScope scope = _scopes.CreateScope();
        InspectionStore store = scope.ServiceProvider.GetRequiredService<InspectionStore>();
        InspectionAllocation allocation = await store.GetAsync(id, token);
        await _host.StopAsync(allocation, token);
        await store.ObserveAsync(id, await _host.ObserveAsync(allocation, token), token);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        do
        {
            try
            {
                using IServiceScope scope = _scopes.CreateScope();
                InspectionStore store = scope.ServiceProvider.GetRequiredService<InspectionStore>();
                foreach (InspectionView session in await store.PendingAsync(stoppingToken))
                {
                    try
                    {
                        if (session.State == "Queued") await scope.ServiceProvider.GetRequiredService<IMessageBus>().PublishAsync(new StartInspection(session.Id));
                        else if (session.State == "Stopping") await StopAsync(session.Id, stoppingToken);
                        else await store.ObserveAsync(session.Id, await _host.ObserveAsync(await store.GetAsync(session.Id, stoppingToken), stoppingToken), stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                    catch { /* Unknown control-plane state retains its capacity reservation. */ }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch { /* A storage outage cannot authorize a new allocation. */ }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
public static class InspectionHandler
{
    public static Task Handle(StartInspection command, InspectionCoordinator coordinator, CancellationToken token) => coordinator.StartAsync(command.Id, token);
    public static Task Handle(StopInspection command, InspectionCoordinator coordinator, CancellationToken token) => coordinator.StopAsync(command.Id, token);
}
