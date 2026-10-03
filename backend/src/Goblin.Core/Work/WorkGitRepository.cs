using System;

namespace Goblin.Core.Work;

// Suggestions are not authority. Only an explicit command carrying a freshly
// bound repository grant can create the repository attempt.
public sealed record WorkGitRepositoryRequest(ExecutionTarget Target, string[] GitRepositories);

public sealed partial class WorkItem
{
    public WorkGitRepositoryRequest? GitRepositoryRequest { get; private set; }

    public void RequestGitRepositorySetup(ExecutionTarget target, string[] gitRepositories, DateTimeOffset now)
    {
        Require(Status == WorkStatus.Ready && AgentId is not null && target.GitRepository is null,
            WorkRule.InvalidTransition);
        Require(CurrentAttempt?.CleanupPending != true, WorkRule.ReconciliationRequired);
        SetGitRepositoryRequest(target, gitRepositories, now);
    }

    public void RequestGitRepositorySetupFromExecution(long attemptId, long ownerId, string[] gitRepositories, DateTimeOffset now)
    {
        ExecutionAttempt attempt = OwnedActiveAttempt(attemptId, ownerId);
        Require(attempt.Target.GitRepository is null, WorkRule.InvalidTransition);
        attempt.Status = AttemptStatus.Waiting;
        attempt.ReleaseWorkspace = true;
        attempt.FinishedAt = now;
        SetGitRepositoryRequest(attempt.Target, gitRepositories, now);
        RequireCleanup(attemptId, ownerId, now);
    }

    public void RequestGitRepositorySetupForAnswer(long decisionId, string answer, string[] gitRepositories, DateTimeOffset now)
    {
        Require(CurrentAttempt is not null, WorkRule.InvalidTransition);
        RecordAnswer(decisionId, answer, now);
        SetGitRepositoryRequest(CurrentAttempt!.Target, gitRepositories, now);
        if (CurrentAttempt is { Target.GitRepository: not null, ReleaseWorkspace: false, OwnerId: { } owner } attempt)
        {
            attempt.ReleaseWorkspace = true;
            RequireCleanup(attempt.Id, owner, now);
        }
    }

    public void AuthorizeGitRepository(long attemptId, ExecutionTarget target, DateTimeOffset now)
    {
        GitRepositoryAuthorization approval = RequireGitRepositoryAuthorization(attemptId);
        Require(CurrentAttempt?.CleanupPending != true, WorkRule.ReconciliationRequired);
        Require(Status == WorkStatus.NeedsAttention && Attention?.Reason == AttentionReason.GitRepositoryRequired,
            WorkRule.InvalidTransition);
        Require(target == approval.Target, WorkRule.OwnershipMismatch);
        ValidateNewAttempt(attemptId, target);
        if (approval.Retry)
        {
            Require(CurrentAttempt?.Status == AttemptStatus.Failed, WorkRule.InvalidTransition);
            Record(WorkEventKind.RetryRequested, now, CurrentAttempt!.Id);
        }
        if (!approval.Retry && CurrentAttempt is { Status: AttemptStatus.Waiting } prior)
        {
            if (_decisions.Count > 0 && _decisions[^1].AttemptId == prior.Id && _decisions[^1].Answer is null)
            {
                Status = WorkStatus.NeedsAttention; Attention = new(AttentionReason.InputRequired);
                RecordAnswer(_decisions[^1].Id, "Use " + target.GitRepository!.GitRepository + " for this Work.", now);
            }
            // The conversation interaction ended; its immutable target and
            // runtime references remain on that attempt for provenance.
            prior.Status = AttemptStatus.Succeeded;
            prior.FinishedAt ??= now;
        }
        GitRepositoryAuthorization = approval with { Status = GitRepositoryAuthorizationStatus.Authorized, AnsweredAt = now };
        GitRepositoryRequest = null;
        Record(WorkEventKind.GitRepositoryAuthorized, now, attemptId, text: target.GitRepository!.GitRepository);
        Queue(attemptId, target, now);
    }

    private void SetGitRepositoryRequest(ExecutionTarget target, string[] gitRepositories, DateTimeOffset now)
    {
        GitRepositoryRequest = new(target, (string[])gitRepositories.Clone());
        Status = WorkStatus.NeedsAttention;
        Attention = new(AttentionReason.GitRepositoryRequired);
        Record(WorkEventKind.GitRepositoryRequested, now, CurrentAttempt?.Id,
            text: "Choose a repository and review GitHub access for this Work.");
    }
}
