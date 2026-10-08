using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.GitRepositories;
using Goblin.Application.Work;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;

namespace Goblin.Application.Workspaces;

// Verification may contact the remote repository. Persistence rechecks the
// attempt and turn afterwards, without holding a transaction over remote I/O.
public sealed class WorkspaceCheckpointCoordinator
{
    private readonly GitRepositoryBroker _broker;
    private readonly WorkspaceCheckpoints _checkpoints;

    public WorkspaceCheckpointCoordinator(GitRepositoryBroker broker, WorkspaceCheckpoints checkpoints)
    {
        _broker = broker;
        _checkpoints = checkpoints;
    }

    public async Task<WorkspaceCheckpoint> SaveAsync(long attemptId, int turn, string commit, CancellationToken token)
    {
        WorkSnapshot work = await _broker.CurrentAsync(attemptId, token);
        AttemptSnapshot attempt = work.Attempts[^1];
        if (attempt.TurnNumber != turn || attempt.Status is not (AttemptStatus.Starting or AttemptStatus.Running))
            throw new ApplicationFailure("workspace_changed");
        await _broker.VerifyCheckpointAsync(work, commit, token);
        return await _checkpoints.SaveVerifiedAsync(work, turn, commit, token);
    }
}
