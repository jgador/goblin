using System;

namespace Goblin.Core.Work;

// Suggestions are not authority. Only an explicit command carrying a freshly
// bound repository grant can create the repository attempt.
public sealed record WorkRepositoryRequest(ExecutionTarget Target, string[] Repositories);

public sealed partial class WorkItem
{
    public WorkRepositoryRequest? RepositoryRequest { get; private set; }

    public void RequestRepositorySetup(ExecutionTarget target, string[] repositories, DateTimeOffset now)
    {
        Require(Status == WorkStatus.Ready && AgentId is not null && target.Repository is null,
            WorkRule.InvalidTransition);
        Require(CurrentAttempt?.CleanupPending != true, WorkRule.ReconciliationRequired);
        SetRepositoryRequest(target, repositories, now);
    }

    public void RequestRepositorySetupFromExecution(long attemptId, long ownerId, string[] repositories, DateTimeOffset now)
    {
        ExecutionAttempt attempt = OwnedActiveAttempt(attemptId, ownerId);
        Require(attempt.Target.Repository is null, WorkRule.InvalidTransition);
        attempt.Status = AttemptStatus.Waiting;
        attempt.ReleaseWorkspace = true;
        attempt.FinishedAt = now;
        SetRepositoryRequest(attempt.Target, repositories, now);
        RequireCleanup(attemptId, ownerId, now);
    }

    public void RequestRepositorySetupForAnswer(long decisionId, string answer, string[] repositories, DateTimeOffset now)
    {
        Require(CurrentAttempt is not null, WorkRule.InvalidTransition);
        RecordAnswer(decisionId, answer, now);
        SetRepositoryRequest(CurrentAttempt!.Target, repositories, now);
        if (CurrentAttempt is { Target.Repository: not null, ReleaseWorkspace: false, OwnerId: { } owner } attempt)
        {
            attempt.ReleaseWorkspace = true;
            RequireCleanup(attempt.Id, owner, now);
        }
    }

    public void AuthorizeRepository(long attemptId, ExecutionTarget target, DateTimeOffset now)
    {
        RepositoryAuthorization approval = RequireRepositoryAuthorization(attemptId);
        Require(CurrentAttempt?.CleanupPending != true, WorkRule.ReconciliationRequired);
        Require(Status == WorkStatus.NeedsAttention && Attention?.Reason == AttentionReason.RepositoryRequired,
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
                RecordAnswer(_decisions[^1].Id, "Use " + target.Repository!.Repository + " for this Work.", now);
            }
            // The conversation interaction ended; its immutable target and
            // runtime references remain on that attempt for provenance.
            prior.Status = AttemptStatus.Succeeded;
            prior.FinishedAt ??= now;
        }
        RepositoryAuthorization = approval with { Status = RepositoryAuthorizationStatus.Authorized, AnsweredAt = now };
        RepositoryRequest = null;
        Record(WorkEventKind.RepositoryAuthorized, now, attemptId, text: target.Repository!.Repository);
        Queue(attemptId, target, now);
    }

    private void SetRepositoryRequest(ExecutionTarget target, string[] repositories, DateTimeOffset now)
    {
        RepositoryRequest = new(target, (string[])repositories.Clone());
        Status = WorkStatus.NeedsAttention;
        Attention = new(AttentionReason.RepositoryRequired);
        Record(WorkEventKind.RepositoryRequested, now, CurrentAttempt?.Id,
            text: "Choose a repository and review GitHub access for this Work.");
    }
}
