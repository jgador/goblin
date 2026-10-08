using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.Work;
using Goblin.Contracts;
using Goblin.Core.Work;
using Goblin.Persistence;
using Microsoft.EntityFrameworkCore;
using Repository = Goblin.Persistence.Entities.GithubRepository;

namespace Goblin.Application.GitRepositories;

// Current account access is shared; operation approval and setup provenance
// remain distinct. Callers own loading and any transaction that rechecks access.
internal static class GitRepositoryAccess
{
    internal static async Task RequireOperationAsync(GoblinDbContext db, WorkSnapshot work,
        long attemptId, CancellationToken token)
    {
        AttemptSnapshot attempt = work.Attempts[^1];
        if (attempt.Id != attemptId || attempt.Target.GitRepository?.Grant is not { } grant)
            throw new ApplicationFailure("repository_operation_unavailable");
        if (grant.PolicyVersion == 2 &&
            (work.GitRepositoryAuthorization is not { Status: GitRepositoryAuthorizationStatus.Authorized } approved ||
                approved.Id != attemptId || approved.Target != attempt.Target) ||
            !await EnabledFor(db, grant).AnyAsync(token))
            throw new ApplicationFailure("repository_operation_unavailable");
    }

    internal static async Task RequireSetupAsync(GoblinDbContext db, WorkSnapshot work,
        long attemptId, int turnNumber, CancellationToken token)
    {
        AttemptSnapshot attempt = work.Attempts[^1];
        if (attempt.Id != attemptId || attempt.TurnNumber != turnNumber ||
            attempt.Target.GitRepository?.Grant is not { } grant ||
            !await EnabledFor(db, grant).AnyAsync(x => x.Name == attempt.Target.GitRepository.GitRepository, token))
            throw new ApplicationFailure("repository_setup_unavailable");
    }

    private static IQueryable<Repository> EnabledFor(GoblinDbContext db, GitRepositoryGrant grant) =>
        db.GithubRepositories.Where(x => x.Id == grant.GitRepositoryId && x.Enabled &&
            x.ConnectionId == grant.ConnectionId && x.Connection.Generation == grant.Generation &&
            x.Connection.AccountId == grant.AccountId &&
            x.Connection.Availability == nameof(GitHubConnectionStatus.Connected));
}
