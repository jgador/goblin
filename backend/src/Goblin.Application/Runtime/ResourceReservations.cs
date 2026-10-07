using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Core.Work;
using Goblin.Persistence;
using Microsoft.EntityFrameworkCore;
using AttemptRow = Goblin.Persistence.Entities.ExecutionAttempt;
using SessionRow = Goblin.Persistence.Entities.WorkspaceSession;

namespace Goblin.Application.Runtime;

// Queries over durable reservations, shared by execution, inspection, and
// connection changes. Callers retain ownership of their transaction and lock.
internal static class ResourceReservations
{
    internal static IQueryable<AttemptRow> Attempts(IQueryable<AttemptRow> attempts) =>
        attempts.Where(x => x.Status == nameof(AttemptStatus.Starting) ||
            x.Status == nameof(AttemptStatus.Running) ||
            x.Status == nameof(AttemptStatus.CancellationRequested) ||
            x.Status == nameof(AttemptStatus.Uncertain) || x.CleanupPending || x.WorkspaceRetained);

    internal static IQueryable<SessionRow> Inspections(IQueryable<SessionRow> sessions) =>
        sessions.Where(x => x.State == nameof(InspectionState.Starting) ||
            x.State == nameof(InspectionState.Available) ||
            x.State == nameof(InspectionState.Stopping) || x.State == nameof(InspectionState.NeedsAttention));

    internal static async Task<int> CountSandboxesAsync(GoblinDbContext db,
        IQueryable<AttemptRow> reservedAttempts, CancellationToken token)
    {
        int occupied = await reservedAttempts.CountAsync(x => x.GithubConnectionId != null, token);
        return occupied + await Inspections(db.WorkspaceSessions).CountAsync(token);
    }
}
