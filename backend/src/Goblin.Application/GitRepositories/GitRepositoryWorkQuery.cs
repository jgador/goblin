using System;
using System.Linq;
using Goblin.Application.Work;
using Goblin.Core.Work;
using AttemptRow = Goblin.Persistence.Entities.ExecutionAttempt;
using WorkRow = Goblin.Persistence.Entities.WorkItem;

namespace Goblin.Application.GitRepositories;

internal sealed class GitRepositoryWorkOwner
{
    internal required long WorkId { get; init; }
    internal required string Objective { get; init; }
    internal required DateTime CreatedAt { get; init; }
    internal string? State { get; init; }
    internal required int TurnNumber { get; init; }

    internal WorkSnapshot Snapshot() => WorkStatePersistence.Snapshot(new WorkRow
    {
        Id = WorkId,
        Objective = Objective,
        CreatedAt = CreatedAt,
        State = State
    });
}

internal static class GitRepositoryWorkQuery
{
    internal static IQueryable<GitRepositoryWorkOwner> ForAttempt(IQueryable<AttemptRow> attempts, long attemptId) =>
        attempts.Where(x => x.Id == attemptId).Select(x => new GitRepositoryWorkOwner
        {
            WorkId = x.Work.Id,
            Objective = x.Work.Objective,
            CreatedAt = x.Work.CreatedAt,
            State = x.Work.State,
            TurnNumber = x.TurnNumber
        });
}
