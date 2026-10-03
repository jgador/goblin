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

public enum GitRepositoryAuthorizationStatus
{
    Pending,
    Authorized,
    Denied,
    Invalidated
}

public sealed record GitRepositoryAuthorization
{
    public GitRepositoryAuthorization(long id, ExecutionTarget target, DateTimeOffset requestedAt)
    {
        Id = id;
        Target = target;
        RequestedAt = requestedAt;
    }

    public long Id { get; init; }
    public ExecutionTarget Target { get; init; }
    public bool EnableGitRepository { get; init; }
    public bool Retry { get; init; }
    public DateTimeOffset RequestedAt { get; init; }
    public GitRepositoryAuthorizationStatus Status { get; init; } = GitRepositoryAuthorizationStatus.Pending;
    public DateTimeOffset? AnsweredAt { get; init; }
    public string? DefaultBranch { get; init; }
}

public sealed partial class WorkItem
{
    public GitRepositoryAuthorization? GitRepositoryAuthorization { get; private set; }

    public void PrepareGitRepositoryAuthorization(long id, ExecutionTarget target, bool enableGitRepository, bool retry, DateTimeOffset now, string? defaultBranch = null)
    {
        Require(CurrentAttempt?.Status != AttemptStatus.Uncertain, WorkRule.ReconciliationRequired);
        Require(CurrentAttempt?.CleanupPending != true, WorkRule.ReconciliationRequired);
        Require(target.GitRepository?.Grant is not null, WorkRule.InvalidValue);
        ValidateNewAttempt(id, target);
        target.GitRepository!.Grant!.Authorize(Id, id, target.GitRepository.GitRepository, target.GitRepository.GitRepository, target.GitRepository.Grant.Branch, GitRepositoryOperationKind.Fetch);
        Require(CurrentAttempt?.Status != AttemptStatus.Failed || retry, WorkRule.InvalidTransition);
        if (retry)
            Require(Status == WorkStatus.NeedsAttention && (Attention?.Reason == AttentionReason.Failure || Attention?.Reason == AttentionReason.GitRepositoryRequired && GitRepositoryAuthorization?.Retry == true) &&
                CurrentAttempt?.Status == AttemptStatus.Failed, WorkRule.InvalidTransition);
        else
            Require(Status == WorkStatus.Ready || Status == WorkStatus.NeedsAttention &&
                Attention?.Reason is AttentionReason.GitRepositoryRequired or AttentionReason.InputRequired, WorkRule.InvalidTransition);
        ExecutionTarget? previous = GitRepositoryRequest?.Target ??
            (Attention?.Reason == AttentionReason.InputRequired ? CurrentAttempt?.Target : null);
        if (previous is not null)
            Require(previous.Runtime == target.Runtime && previous.ConnectionId == target.ConnectionId &&
                previous.RequestedModel == target.RequestedModel && previous.RequestedEffort == target.RequestedEffort,
                WorkRule.OwnershipMismatch);
        InvalidateGitRepositoryAuthorization(now);
        GitRepositoryAuthorization = new(id, target, now)
        {
            EnableGitRepository = enableGitRepository,
            Retry = retry,
            DefaultBranch = defaultBranch
        };
        GitRepositoryRequest = new(target, [target.GitRepository!.GitRepository]);
        Status = WorkStatus.NeedsAttention;
        Attention = new(AttentionReason.GitRepositoryRequired);
        Record(WorkEventKind.GitRepositoryRequested, now, text: "Review repository access and Git actions before authorizing this Work.");
        if (CurrentAttempt is { Status: AttemptStatus.Waiting, Target.GitRepository: not null, ReleaseWorkspace: false, OwnerId: { } owner } attempt)
        {
            attempt.ReleaseWorkspace = true;
            RequireCleanup(attempt.Id, owner, now);
        }
    }

    public void DenyGitRepositoryAuthorization(long id, DateTimeOffset now)
    {
        GitRepositoryAuthorization request = RequireGitRepositoryAuthorization(id);
        GitRepositoryAuthorization = request with { Status = GitRepositoryAuthorizationStatus.Denied, AnsweredAt = now };
        // Keep the saved setup so a different scope can be proposed, including after a retry.
        Record(WorkEventKind.GitRepositoryDenied, now, text: request.Target.GitRepository!.GitRepository);
    }

    private GitRepositoryAuthorization RequireGitRepositoryAuthorization(long id)
    {
        Require(GitRepositoryAuthorization is { Status: GitRepositoryAuthorizationStatus.Pending } request && request.Id == id,
            WorkRule.DecisionNotCurrent);
        return GitRepositoryAuthorization!;
    }

    private void InvalidateGitRepositoryAuthorization(DateTimeOffset now)
    {
        if (GitRepositoryAuthorization is not { Status: GitRepositoryAuthorizationStatus.Pending } request) return;
        GitRepositoryAuthorization = request with { Status = GitRepositoryAuthorizationStatus.Invalidated, AnsweredAt = now };
        Record(WorkEventKind.GitRepositoryAuthorizationInvalidated, now);
    }
}
