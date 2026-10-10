using System.Linq;
using Goblin.Application.Work;
using Goblin.Core.Work;
using AttemptRow = Goblin.Persistence.Entities.ExecutionAttempt;

namespace Goblin.Application.GitRepositories;

internal sealed class GitRepositoryWorkOwner
{
    internal required string State { get; init; }
    internal required int TurnNumber { get; init; }

    internal WorkSnapshot Snapshot() => WorkStatePersistence.Snapshot(State);
}

internal static class GitRepositoryWorkQuery
{
    internal static IQueryable<GitRepositoryWorkOwner> ForAttempt(IQueryable<AttemptRow> attempts, long attemptId) =>
        attempts.Where(x => x.Id == attemptId).Select(x => new GitRepositoryWorkOwner
        {
            State = x.Work.State,
            TurnNumber = x.TurnNumber
        });
}
