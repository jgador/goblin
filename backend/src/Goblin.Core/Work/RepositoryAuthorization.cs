using System;

namespace Goblin.Core.Work;

public sealed record GitDeliveryIntent(string? BaseBranch = null, bool Push = false, bool OpenPullRequest = false)
{
    public void Validate()
    {
        if (OpenPullRequest && !Push || BaseBranch is { } branch &&
            (branch.Length is 0 or > 200 || !char.IsAsciiLetterOrDigit(branch[0]) || Array.Exists(branch.ToCharArray(), c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '/' or '-')) ||
             branch.Contains("..") || branch.Contains("//") || branch.EndsWith('/') || branch.EndsWith('.') ||
             Array.Exists(branch.Split('/'), x => x.StartsWith('.') || x.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))))
            throw new WorkRuleException(WorkRule.InvalidValue);
    }
}

public enum RepositoryAuthorizationStatus { Pending, Authorized, Denied, Invalidated }
public sealed record RepositoryAuthorization(long Id, ExecutionTarget Target, bool EnableRepository,
    bool Retry, DateTimeOffset RequestedAt, RepositoryAuthorizationStatus Status = RepositoryAuthorizationStatus.Pending,
    DateTimeOffset? AnsweredAt = null, string? DefaultBranch = null);

public sealed partial class WorkItem
{
    public RepositoryAuthorization? RepositoryAuthorization { get; private set; }

    public void PrepareRepositoryAuthorization(long id, ExecutionTarget target, bool enableRepository, bool retry, DateTimeOffset now, string? defaultBranch = null)
    {
        Require(CurrentAttempt?.Status != AttemptStatus.Uncertain, WorkRule.ReconciliationRequired);
        Require(CurrentAttempt?.CleanupPending != true, WorkRule.ReconciliationRequired);
        Require(target.Repository?.Grant is not null, WorkRule.InvalidValue);
        ValidateNewAttempt(id, target);
        target.Repository!.Grant!.Authorize(Id, id, target.Repository.Repository, target.Repository.Repository, target.Repository.Grant.Branch, "fetch");
        Require(CurrentAttempt?.Status != AttemptStatus.Failed || retry, WorkRule.InvalidTransition);
        if (retry)
            Require(Status == WorkStatus.NeedsAttention && (Attention?.Reason == AttentionReason.Failure || Attention?.Reason == AttentionReason.RepositoryRequired && RepositoryAuthorization?.Retry == true) &&
                CurrentAttempt?.Status == AttemptStatus.Failed, WorkRule.InvalidTransition);
        else
            Require(Status == WorkStatus.Ready || Status == WorkStatus.NeedsAttention &&
                Attention?.Reason is AttentionReason.RepositoryRequired or AttentionReason.InputRequired, WorkRule.InvalidTransition);
        ExecutionTarget? previous = RepositoryRequest?.Target ??
            (Attention?.Reason == AttentionReason.InputRequired ? CurrentAttempt?.Target : null);
        if (previous is not null)
            Require(previous.Runtime == target.Runtime && previous.ConnectionId == target.ConnectionId &&
                previous.RequestedModel == target.RequestedModel && previous.RequestedEffort == target.RequestedEffort,
                WorkRule.OwnershipMismatch);
        InvalidateRepositoryAuthorization(now);
        RepositoryAuthorization = new(id, target, enableRepository, retry, now, DefaultBranch: defaultBranch);
        RepositoryRequest = new(target, [target.Repository!.Repository]);
        Status = WorkStatus.NeedsAttention;
        Attention = new(AttentionReason.RepositoryRequired);
        Record(WorkEventKind.RepositoryRequested, now, text: "Review repository access and Git actions before authorizing this Work.");
        if (CurrentAttempt is { Status: AttemptStatus.Waiting, Target.Repository: not null, ReleaseWorkspace: false, OwnerId: { } owner } attempt)
        {
            attempt.ReleaseWorkspace = true;
            RequireCleanup(attempt.Id, owner, now);
        }
    }

    public void DenyRepositoryAuthorization(long id, DateTimeOffset now)
    {
        RepositoryAuthorization request = RequireRepositoryAuthorization(id);
        RepositoryAuthorization = request with { Status = RepositoryAuthorizationStatus.Denied, AnsweredAt = now };
        // Keep the saved setup so a different scope can be proposed, including after a retry.
        Record(WorkEventKind.RepositoryDenied, now, text: request.Target.Repository!.Repository);
    }

    private RepositoryAuthorization RequireRepositoryAuthorization(long id)
    {
        Require(RepositoryAuthorization is { Status: RepositoryAuthorizationStatus.Pending } request && request.Id == id,
            WorkRule.DecisionNotCurrent);
        return RepositoryAuthorization!;
    }

    private void InvalidateRepositoryAuthorization(DateTimeOffset now)
    {
        if (RepositoryAuthorization is not { Status: RepositoryAuthorizationStatus.Pending } request) return;
        RepositoryAuthorization = request with { Status = RepositoryAuthorizationStatus.Invalidated, AnsweredAt = now };
        Record(WorkEventKind.RepositoryAuthorizationInvalidated, now);
    }
}
