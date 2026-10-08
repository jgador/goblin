using System.Linq;
using Goblin.Core.Work;
using AttemptRow = Goblin.Persistence.Entities.ExecutionAttempt;

namespace Goblin.Application.Runtime;

// Keep storage predicates and encodings at the read boundary. Recovery needs
// only a delivery request, not a mutable attempt entity or its navigation graph.
internal static class ExecutionRecoveryQuery
{
    internal static IQueryable<ExecutionRecoveryRequest> Select(IQueryable<AttemptRow> attempts) =>
        attempts.Where(x => x.Status == nameof(AttemptStatus.Queued) ||
                x.Status == nameof(AttemptStatus.Starting) || x.Status == nameof(AttemptStatus.Running) ||
                x.Status == nameof(AttemptStatus.CancellationRequested) || x.Status == nameof(AttemptStatus.Uncertain) ||
                (x.CleanupPending && !x.CleanupFailed) || x.WorkspaceRetained)
            .OrderBy(x => x.QueuedAt)
            .Select(x => new ExecutionRecoveryRequest(x.WorkId, x.Id, x.TurnNumber,
                x.Status == nameof(AttemptStatus.Queued) ? ExecutionRecoveryAction.Dispatch : ExecutionRecoveryAction.Reconcile));
}
