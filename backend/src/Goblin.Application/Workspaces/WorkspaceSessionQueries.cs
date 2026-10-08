using System.Linq;
using Goblin.Core.Work;
using SessionRow = Goblin.Persistence.Entities.WorkspaceSession;

namespace Goblin.Application.Workspaces;

// EF-translatable counterparts to WorkspaceSessionRules. The tests keep these
// persistence predicates aligned with each distinct lifecycle meaning.
internal static class WorkspaceSessionQueries
{
    internal static IQueryable<SessionRow> Active(IQueryable<SessionRow> sessions) =>
        sessions.Where(x => x.State == nameof(InspectionState.Queued) ||
            x.State == nameof(InspectionState.Starting) ||
            x.State == nameof(InspectionState.Available) ||
            x.State == nameof(InspectionState.Stopping) || x.State == nameof(InspectionState.NeedsAttention));

    internal static IQueryable<SessionRow> HoldingCapacity(IQueryable<SessionRow> sessions) =>
        sessions.Where(x => x.State == nameof(InspectionState.Starting) ||
            x.State == nameof(InspectionState.Available) ||
            x.State == nameof(InspectionState.Stopping) || x.State == nameof(InspectionState.NeedsAttention));

    internal static IQueryable<SessionRow> RequiringObservation(IQueryable<SessionRow> sessions) =>
        sessions.Where(x => x.State == nameof(InspectionState.Queued) ||
            x.State == nameof(InspectionState.Starting) ||
            x.State == nameof(InspectionState.Available) || x.State == nameof(InspectionState.Stopping));
}
