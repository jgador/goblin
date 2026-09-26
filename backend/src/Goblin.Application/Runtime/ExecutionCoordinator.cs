using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.Work;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;

namespace Goblin.Application.Runtime;

public sealed class ExecutionCoordinator
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IExecutionHost _host;
    private readonly IDispatchFailureJournal _failures;

    public ExecutionCoordinator(IServiceScopeFactory scopes, IExecutionHost host, IDispatchFailureJournal failures)
    {
        _scopes = scopes;
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
            using IServiceScope scope = _scopes.CreateScope();
            claimed = await scope.ServiceProvider.GetRequiredService<WorkStore>().ClaimAsync(command,
                _host.EnvironmentFor,
                target => _host.Capabilities.Any(x => x.Runtime == target.Runtime &&
                    (target.Repository is null ? x.TextExecution : x.RepositoryExecution)), token);
            if (claimed is null) return;
            await _host.StartAsync(claimed, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Shutdown is not permission to redeliver a claimed external start.
            if (claimed is not null)
                await _failures.RecordAsync(new(command.WorkId, command.AttemptId, FailureKind.HostUnavailable, TurnNumber: command.TurnNumber), CancellationToken.None);
            throw;
        }
        catch
        {
            await _failures.RecordAsync(new(command.WorkId, command.AttemptId,
                claimed is null ? FailureKind.DispatchFailed : FailureKind.HostUnavailable, TurnNumber: command.TurnNumber), CancellationToken.None);
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
            await _failures.RecordAsync(new(command.WorkId, command.AttemptId, FailureKind.StorageUnavailable, TurnNumber: command.TurnNumber), CancellationToken.None);
            throw;
        }
    }

    private async Task ObserveAndSaveAsync(ReconcileWork command, CancellationToken token)
    {
        WorkSnapshot work;
        using (IServiceScope scope = _scopes.CreateScope())
            work = (await scope.ServiceProvider.GetRequiredService<WorkStore>().GetAsync(command.WorkId, token)).Work;
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
        using (IServiceScope scope = _scopes.CreateScope())
        {
            WorkStore store = scope.ServiceProvider.GetRequiredService<WorkStore>();
            string[] repositories = observation.Kind == ObservationKind.WorkspaceRequired && attempt.Target.Repository is null
                ? await store.RepositorySuggestionsAsync(work, token) : [];
            long decisionId = observation.Kind is ObservationKind.InputRequired or ObservationKind.Paused
                ? await scope.ServiceProvider.GetRequiredService<IdentityStore>().NextEventAsync(token) : 0;
            await store.MutateAsync(work.Id, current =>
            {
                ExecutionAttempt? a = current.CurrentAttempt;
                if (a is null || a.Id != attempt.Id || a.OwnerId != attempt.OwnerId || observation.TurnNumber != a.TurnNumber ||
                    a.Status is AttemptStatus.Succeeded or AttemptStatus.Failed or AttemptStatus.Cancelled) return;
                if (a.Status == AttemptStatus.Waiting && observation.Kind is ObservationKind.Paused or ObservationKind.InputRequired or ObservationKind.Pending or ObservationKind.WorkspaceRequired) return;
                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (a.StartedAt is null && observation.Session is not null)
                    current.ExecutionStarted(a.Id, a.OwnerId!.Value, observation.Session, now);
                if (observation.CheckpointId is not null)
                    current.SaveWorkspace(a.Id, a.OwnerId!.Value, observation.CheckpointId.Value, now);
                switch (observation.Kind)
                {
                    case ObservationKind.Running:
                        if (!string.IsNullOrWhiteSpace(observation.Text) && a.Status != AttemptStatus.Uncertain &&
                            current.History.LastOrDefault(x => x.AttemptId == a.Id && x.Kind == WorkEventKind.ProgressReported)?.Text != observation.Text)
                            current.ReportProgress(a.Id, a.OwnerId!.Value, observation.Text, now);
                        break;
                    case ObservationKind.Result:
                        current.ProposeResult(a.Id, a.OwnerId!.Value, observation.Text!, now);
                        if (observation.ArtifactReference is not null)
                            current.AddArtifact(a.Id, a.OwnerId.Value, observation.ArtifactReference, "Execution workspace", now);
                        break;
                    case ObservationKind.WorkspaceRequired:
                        if (a.Status == AttemptStatus.CancellationRequested && a.Target.Repository is null)
                            current.ConfirmExecutionStopped(a.Id, a.OwnerId!.Value, now);
                        else if (a.Target.Repository is null)
                            current.RequestRepositorySetupFromExecution(a.Id, a.OwnerId!.Value, repositories, now);
                        else current.RequireRepositoryExecution(a.Id, a.OwnerId!.Value, now);
                        break;
                    case ObservationKind.Paused:
                        current.PauseForInput(a.Id, a.OwnerId!.Value, decisionId, observation.Text!, observation.ReleaseWorkspace, now);
                        if (observation.ReleaseWorkspace) current.RequireCleanup(a.Id, a.OwnerId.Value, now);
                        break;
                    case ObservationKind.InputRequired:
                        current.RequestInput(a.Id, a.OwnerId!.Value, decisionId, observation.Text!, now);
                        if (observation.ArtifactReference is not null)
                            current.AddArtifact(a.Id, a.OwnerId.Value, observation.ArtifactReference, "Execution workspace", now);
                        break;
                    case ObservationKind.Failed:
                        // The host only reports Failed after proving the process stopped.
                        if (a.Status == AttemptStatus.Uncertain)
                            current.ConfirmExecutionStopped(a.Id, a.OwnerId!.Value, now);
                        else current.ExecutionFailed(a.Id, a.OwnerId!.Value, observation.Failure ?? FailureKind.ExecutionFailed, now);
                        break;
                    case ObservationKind.Uncertain:
                        if (a.Status != AttemptStatus.Uncertain)
                            current.ExecutionUncertain(a.Id, a.OwnerId!.Value, observation.Failure ?? FailureKind.RuntimeDisconnected, now);
                        break;
                    case ObservationKind.Stopped:
                        if (a.Status is not (AttemptStatus.Uncertain or AttemptStatus.CancellationRequested))
                            current.ExecutionUncertain(a.Id, a.OwnerId!.Value, FailureKind.RuntimeDisconnected, now);
                        current.ConfirmExecutionStopped(a.Id, a.OwnerId!.Value, now);
                        break;
                }
                if (a.Status is AttemptStatus.Succeeded or AttemptStatus.Failed or AttemptStatus.Cancelled)
                    current.RequireCleanup(a.Id, a.OwnerId!.Value, now);
            }, token);
        }
        if (observation.Kind is ObservationKind.Result or ObservationKind.InputRequired or ObservationKind.Failed or ObservationKind.Stopped ||
            observation.Kind == ObservationKind.Paused && observation.ReleaseWorkspace ||
            observation.Kind == ObservationKind.WorkspaceRequired && attempt.Target.Repository is null)
            await CleanupAsync(work, token);
        if (observation.Kind == ObservationKind.WorkspaceRequired && attempt.Target.Repository is not null)
            await _host.CleanupAsync(work, token);
    }

    private async Task CleanupAsync(WorkSnapshot work, CancellationToken token)
    {
        AttemptSnapshot attempt = work.Attempts[^1];
        try
        {
            await _host.CleanupAsync(work, token);
            using IServiceScope scope = _scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<WorkStore>().MutateAsync(work.Id, current =>
            {
                if (current.CurrentAttempt is { } currentAttempt && currentAttempt.Id == attempt.Id &&
                    currentAttempt.OwnerId == attempt.OwnerId && currentAttempt.TurnNumber == attempt.TurnNumber)
                    current.ConfirmCleanup(attempt.Id, attempt.OwnerId!.Value, DateTimeOffset.UtcNow);
            }, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch
        {
            await _failures.RecordAsync(new(work.Id, attempt.Id, FailureKind.CleanupFailed, Cleanup: true, TurnNumber: attempt.TurnNumber), CancellationToken.None);
            await DrainFailuresAsync(CancellationToken.None);
        }
    }

    public async Task DrainFailuresAsync(CancellationToken token)
    {
        foreach (DispatchFailureEvidence failure in await _failures.ReadAsync(token))
        {
            using IServiceScope scope = _scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<WorkStore>().MutateAsync(failure.WorkId, work =>
            {
                ExecutionAttempt? a = work.CurrentAttempt;
                if (a is null || a.Id != failure.AttemptId || (failure.TurnNumber != 0 && failure.TurnNumber != a.TurnNumber)) return;
                if (a.CleanupPending && a.Status is AttemptStatus.Succeeded or AttemptStatus.Failed or AttemptStatus.Cancelled or AttemptStatus.Waiting)
                    work.ReportCleanupFailure(a.Id, a.OwnerId!.Value, DateTimeOffset.UtcNow);
                else if (a.Status == AttemptStatus.Queued) work.DispatchFailed(a.Id, failure.Failure, DateTimeOffset.UtcNow);
                else if (a.Status is AttemptStatus.Starting or AttemptStatus.Running or AttemptStatus.CancellationRequested or AttemptStatus.Waiting)
                    work.ExecutionUncertain(a.Id, a.OwnerId!.Value, failure.Failure, DateTimeOffset.UtcNow);
            }, token);
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
    private readonly IServiceScopeFactory _scopes;
    private readonly ExecutionCoordinator _coordinator;

    public WorkRecovery(IServiceScopeFactory scopes, ExecutionCoordinator coordinator)
    {
        _scopes = scopes;
        _coordinator = coordinator;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = _scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WorkStore>().RecoverConnectionReservationsAsync(cancellationToken);
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
                using IServiceScope scope = _scopes.CreateScope();
                Persistence.Entities.ExecutionAttempt[] attempts = await scope.ServiceProvider.GetRequiredService<WorkStore>().RecoverableAsync(stoppingToken);
                foreach (Persistence.Entities.ExecutionAttempt attempt in attempts)
                {
                    try
                    {
                        if (attempt.Status == "Queued")
                            await scope.ServiceProvider.GetRequiredService<IMessageBus>().PublishAsync(new DispatchWork(attempt.WorkId, attempt.Id, attempt.TurnNumber));
                        else await _coordinator.ReconcileAsync(new(attempt.WorkId, attempt.Id, attempt.TurnNumber), stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch { /* One unavailable host must not hide other Work. */ }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { /* Keep history/readiness available; persisted ownership forbids relaunch. */ }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
