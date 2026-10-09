using System.Linq;
using Goblin.Core.Work;
using AttemptRow = Goblin.Persistence.Entities.ExecutionAttempt;

namespace Goblin.Application.GitRepositories;

// Repository mutation and checkpointing share one execution-ownership boundary.
// Keep its in-memory and EF-translatable forms aligned.
internal static class GitRepositoryAttemptPolicy
{
    internal static bool AllowsOperations(AttemptStatus status) =>
        status is AttemptStatus.Starting or AttemptStatus.Running;

    internal static bool AllowsOperations(string status) =>
        status == nameof(AttemptStatus.Starting) || status == nameof(AttemptStatus.Running);

    internal static IQueryable<AttemptRow> AllowingOperations(IQueryable<AttemptRow> attempts) =>
        attempts.Where(x => x.Status == nameof(AttemptStatus.Starting) ||
            x.Status == nameof(AttemptStatus.Running));
}
