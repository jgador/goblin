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
        Require(CurrentAttempt is { Target.Repository: null }, WorkRule.InvalidTransition);
        RecordAnswer(decisionId, answer, now);
        SetRepositoryRequest(CurrentAttempt!.Target, repositories, now);
    }

    public void AuthorizeRepository(long attemptId, ExecutionTarget target, DateTimeOffset now)
    {
        Require(CurrentAttempt?.CleanupPending != true, WorkRule.ReconciliationRequired);
        Require(Status == WorkStatus.NeedsAttention &&
            (Attention?.Reason == AttentionReason.RepositoryRequired ||
             Attention?.Reason == AttentionReason.InputRequired && CurrentAttempt?.Target.Repository is null),
            WorkRule.InvalidTransition);
        ExecutionTarget previous = RepositoryRequest?.Target ?? CurrentAttempt!.Target;
        Require(target.Repository?.Grant is not null && previous.Repository is null &&
            previous.Runtime == target.Runtime && previous.ConnectionId == target.ConnectionId &&
            previous.RequestedModel == target.RequestedModel && previous.RequestedEffort == target.RequestedEffort,
            WorkRule.OwnershipMismatch);
        ValidateNewAttempt(attemptId, target);
        if (CurrentAttempt is { } prior)
        {
            Require(prior.Status is AttemptStatus.Waiting or AttemptStatus.Succeeded,
                WorkRule.InvalidTransition);
            if (_decisions.Count > 0 && _decisions[^1].AttemptId == prior.Id && _decisions[^1].Answer is null)
                RecordAnswer(_decisions[^1].Id, "Use " + target.Repository!.Repository + " for this Work.", now);
            // The conversation interaction ended; its immutable target and
            // runtime references remain on that attempt for provenance.
            prior.Status = AttemptStatus.Succeeded;
            prior.FinishedAt ??= now;
        }
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
            text: "Choose an enabled repository and authorize GitHub access for this Work.");
    }
}
