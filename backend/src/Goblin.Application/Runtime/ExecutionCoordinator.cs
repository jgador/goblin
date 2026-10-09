using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.Connections;
using Goblin.Application.Work;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;

namespace Goblin.Application.Runtime;

public sealed class ExecutionCoordinator
{
    private readonly WorkStore _store;
    private readonly IdentityStore _identities;
    private readonly IExecutionHost _host;
    private readonly IDispatchFailureJournal _failures;

    public ExecutionCoordinator(WorkStore store, IdentityStore identities, IExecutionHost host, IDispatchFailureJournal failures)
    {
        _store = store;
        _identities = identities;
        _host = host;
        _failures = failures;
    }

    public async Task DispatchAsync(DispatchWork command, CancellationToken token)
    {
        WorkSnapshot? claimed = null;
        try
        {
            // Evidence written during a database outage wins over redelivery.
            if ((await _failures.ReadAsync(token)).Any(x => x.AttemptId == command.AttemptId)) return;
            claimed = await _store.ClaimAsync(command,
                _host.EnvironmentFor,
                target => _host.Capabilities.Any(x => x.Runtime == target.Runtime &&
                    (target.GitRepository is null ? x.TextExecution : x.GitRepositoryExecution)), token);
            if (claimed is null) return;
            await _host.StartAsync(claimed, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Shutdown is not permission to redeliver a claimed external start.
            if (claimed is not null)
                await _failures.RecordAsync(new(command.WorkId, command.AttemptId, FailureKind.HostUnavailable, turnNumber: command.TurnNumber), CancellationToken.None);
            throw;
        }
        catch
        {
            await _failures.RecordAsync(new(command.WorkId, command.AttemptId,
                claimed is null ? FailureKind.DispatchFailed : FailureKind.HostUnavailable, turnNumber: command.TurnNumber), CancellationToken.None);
            await DrainFailuresAsync(CancellationToken.None);
        }
    }

    public async Task ReconcileAsync(ReconcileWork command, CancellationToken token)
    {
        try { await ObserveAndSaveAsync(command, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch
        {
            // Even a failed database read is evidence, not permission to run the
            // attempt again. Surface it after storage returns.
            await _failures.RecordAsync(new(command.WorkId, command.AttemptId, FailureKind.StorageUnavailable, turnNumber: command.TurnNumber), CancellationToken.None);
            throw;
        }
    }

    private async Task ObserveAndSaveAsync(ReconcileWork command, CancellationToken token)
    {
        WorkSnapshot work = (await _store.GetAsync(command.WorkId, token)).Work;
        AttemptSnapshot? attempt = work.Attempts.LastOrDefault();
        if (attempt is null || attempt.Id != command.AttemptId || attempt.OwnerId is null || (command.TurnNumber != 0 && command.TurnNumber != attempt.TurnNumber)) return;
        bool active = attempt.Status is AttemptStatus.Starting or AttemptStatus.Running or
            AttemptStatus.CancellationRequested or AttemptStatus.Uncertain or AttemptStatus.Waiting;
        if (!active)
        {
            if (attempt.CleanupPending) await CleanupAsync(work, token);
            return;
        }
        ExecutionObservation observation;
        try { observation = await _host.ObserveAsync(work, attempt.CancellationRequestedAt is not null, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch
        {
            observation = new(ObservationKind.Uncertain, Failure: attempt.CancellationRequestedAt is not null
                ? FailureKind.CancellationFailed : FailureKind.HostUnavailable);
        }
        string[] gitRepositories = observation.Kind == ObservationKind.WorkspaceRequired && attempt.Target.GitRepository is null
            ? await _store.GitRepositorySuggestionsAsync(work, token) : [];
        long decisionId = observation.Kind is ObservationKind.InputRequired or ObservationKind.Paused
            ? await _identities.NextEventAsync(token) : 0;
        await _store.MutateAsync(work.Id, current => ExecutionObservationTransition.Apply(current, attempt,
            observation, gitRepositories, decisionId, DateTimeOffset.UtcNow), token);
        if (observation.Kind is ObservationKind.Result or ObservationKind.InputRequired or ObservationKind.Failed or ObservationKind.Stopped ||
            observation.Kind == ObservationKind.Paused && observation.ReleaseWorkspace ||
            observation.Kind == ObservationKind.WorkspaceRequired && attempt.Target.GitRepository is null)
            await CleanupAsync(work, token);
        if (observation.Kind == ObservationKind.WorkspaceRequired && attempt.Target.GitRepository is not null)
            await _host.CleanupAsync(work, token);
    }

    private async Task CleanupAsync(WorkSnapshot work, CancellationToken token)
    {
        AttemptSnapshot attempt = work.Attempts[^1];
        try
        {
            await _host.CleanupAsync(work, token);
            await _store.MutateAsync(work.Id, current =>
            {
                if (current.CurrentAttempt is { } currentAttempt && currentAttempt.Id == attempt.Id &&
                    currentAttempt.OwnerId == attempt.OwnerId && currentAttempt.TurnNumber == attempt.TurnNumber)
                    current.ConfirmCleanup(attempt.Id, attempt.OwnerId!.Value, DateTimeOffset.UtcNow);
            }, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch
        {
            await _failures.RecordAsync(new(work.Id, attempt.Id, FailureKind.CleanupFailed, cleanup: true, turnNumber: attempt.TurnNumber), CancellationToken.None);
            await DrainFailuresAsync(CancellationToken.None);
        }
    }

    public async Task DrainFailuresAsync(CancellationToken token)
    {
        foreach (DispatchFailureEvidence failure in await _failures.ReadAsync(token))
        {
            await _store.MutateAsync(failure.WorkId, work =>
                DispatchFailureTransition.Apply(work, failure, DateTimeOffset.UtcNow), token);
            await _failures.RemoveAsync(failure.AttemptId, token);
        }
    }
}

public static class DispatchWorkHandler
{
    public static Task Handle(DispatchWork command, ExecutionCoordinator coordinator, CancellationToken token) =>
        coordinator.DispatchAsync(command, token);
}

public static class ReconcileWorkHandler
{
    public static Task Handle(ReconcileWork command, ExecutionCoordinator coordinator, CancellationToken token) =>
        coordinator.ReconcileAsync(command, token);
}

// Polling is observation and capacity scheduling, never a retry of failed Work.
// The persisted claim is authoritative even after all in-memory state is lost.
public sealed class WorkRecovery : BackgroundService
{
    private readonly WorkStore _store;
    private readonly ConnectionStore _connections;
    private readonly IServiceScopeFactory _scopes;
    private readonly ExecutionCoordinator _coordinator;

    public WorkRecovery(WorkStore store, ConnectionStore connections, IServiceScopeFactory scopes, ExecutionCoordinator coordinator)
    {
        _store = store;
        _connections = connections;
        _scopes = scopes;
        _coordinator = coordinator;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await _connections.RecoverReservationsAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        do
        {
            try
            {
                await _coordinator.DrainFailuresAsync(stoppingToken);
                ExecutionRecoveryRequest[] requests = await _store.RecoverableAsync(stoppingToken);
                foreach (ExecutionRecoveryRequest request in requests)
                {
                    try
                    {
                        if (request.Action == ExecutionRecoveryAction.Dispatch)
                            await PublishDispatchAsync(request);
                        else await _coordinator.ReconcileAsync(new(request.WorkId, request.AttemptId, request.TurnNumber), stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch { /* One unavailable host must not hide other Work. */ }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { /* Keep history/readiness available; persisted ownership forbids relaunch. */ }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PublishDispatchAsync(ExecutionRecoveryRequest request)
    {
        // The bus carries a scoped message context; stores own their contexts
        // per operation and do not need this publication scope.
        using IServiceScope scope = _scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMessageBus>().PublishAsync(new DispatchWork(request.WorkId, request.AttemptId, request.TurnNumber));
    }
}
