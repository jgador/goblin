using System;

namespace Goblin.Core.Work;

public enum WorkStatus
{
    Ready,
    Queued,
    InProgress,
    Cancelling,
    NeedsAttention,
    Completed,
    Cancelled
}

public enum AttemptStatus
{
    Queued,
    Starting,
    Running,
    CancellationRequested,
    Succeeded,
    Failed,
    Uncertain,
    Cancelled,
    Waiting
}

public enum AttentionReason
{
    InputRequired,
    ResultReview,
    Failure,
    UncertainExecution,
    CleanupRequired,
    GitRepositoryRequired
}

// Integrations translate upstream errors into these categories. Raw exceptions,
// response bodies, and credentials are not inputs to a failure transition.
public enum FailureKind
{
    DispatchFailed,
    ConnectionUnavailable,
    CapabilityUnavailable,
    HostUnavailable,
    ExecutionFailed,
    TimedOut,
    RuntimeDisconnected,
    CancellationFailed,
    CleanupFailed,
    StorageUnavailable
}

public enum WorkEventKind
{
    Created,
    Assigned,
    ExecutionQueued,
    ExecutionClaimed,
    ExecutionStarted,
    ExecutionFailed,
    ExecutionUncertain,
    ExecutionStopped,
    RetryRequested,
    InputRequested,
    InputProvided,
    ResultProposed,
    ChangesRequested,
    ResultApproved,
    CancellationRequested,
    Cancelled,
    ContextAdded,
    ProgressReported,
    ArtifactRecorded,
    CleanupRequired,
    CleanupFailed,
    CleanupCompleted,
    ExecutionContinued,
    WorkspaceSaved,
    WorkspaceReleased,
    GitRepositoryRequested,
    GitRepositoryAuthorized,
    GitRepositoryDenied,
    GitRepositoryAuthorizationInvalidated
}

public enum WorkRule
{
    InvalidValue,
    InvalidTransition,
    AgentRequired,
    AttemptAlreadyExists,
    AttemptNotCurrent,
    OwnershipMismatch,
    ReconciliationRequired,
    DecisionNotCurrent
}

// These errors express product rules; the HTTP adapter chooses status codes.
public sealed class WorkRuleException : Exception
{
    public WorkRuleException(WorkRule rule) : base(rule.ToString()) => Rule = rule;

    public WorkRule Rule { get; }
}

public sealed record WorkAttention(AttentionReason Reason, FailureKind? Failure = null);

public sealed record WorkEvent
{
    public WorkEvent(long sequence, DateTimeOffset occurredAt, WorkEventKind kind)
    {
        Sequence = sequence;
        OccurredAt = occurredAt;
        Kind = kind;
    }

    public long Sequence { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public WorkEventKind Kind { get; init; }
    public long? AttemptId { get; init; }
    public long? AgentId { get; init; }
    public long? DecisionId { get; init; }
    public FailureKind? Failure { get; init; }
    public string? Text { get; init; }
}

public sealed record WorkDecision(long Id, long AttemptId, string Question,
    DateTimeOffset RequestedAt, string? Answer = null, DateTimeOffset? AnsweredAt = null);

public sealed record WorkResult(long AttemptId, string Text, DateTimeOffset ProposedAt,
    DateTimeOffset? ApprovedAt = null, string? RequestedChanges = null);

public sealed record WorkMessage(long Id, string Text, DateTimeOffset CreatedAt);

public sealed record WorkArtifact(long AttemptId, string Reference, string Name, DateTimeOffset CreatedAt);

// Runtime/session identifiers are opaque references. There are deliberately no
// Codex thread/turn types, authentication fields, or transport handles here.
public sealed record ExecutionTarget
{
    public string Runtime { get; }
    public long ConnectionId { get; }
    public string? RequestedModel { get; }
    public string? RequestedEffort { get; }
    public GitRepositoryChange? GitRepository { get; }

    public ExecutionTarget(string runtime, long connectionId, string? requestedModel = null,
        GitRepositoryChange? gitRepository = null, string? requestedEffort = null)
    {
        if (string.IsNullOrWhiteSpace(runtime) || connectionId <= 0 ||
            (requestedModel is not null && string.IsNullOrWhiteSpace(requestedModel)) ||
            (requestedEffort is not null && string.IsNullOrWhiteSpace(requestedEffort)))
            throw new WorkRuleException(WorkRule.InvalidValue);
        Runtime = runtime.Trim();
        ConnectionId = connectionId;
        RequestedModel = requestedModel?.Trim();
        RequestedEffort = requestedEffort?.Trim();
        GitRepository = gitRepository;
    }
}

// Explicitly requested repository changes are an execution requirement, not a
// taxonomy imposed on every Work item. Credentials remain integration-owned.
public sealed record GitRepositoryChange
{
    public string GitRepository { get; }
    public string GitAuthorName { get; }
    public string GitAuthorEmail { get; }
    public GitRepositoryGrant? Grant { get; }

    // An authenticated requester's opaque source reference, supplied by the
    // application adapter. It is attribution, never repository authority.
    public string? RequestedBy
    {
        get;
        init
        {
            if (value is not null && (value.Length is 0 or > 256 || Array.Exists(value.ToCharArray(), char.IsControl)))
                throw new WorkRuleException(WorkRule.InvalidValue);
            field = value;
        }
    }

    public GitRepositoryChange(string gitRepository, string gitAuthorName, string gitAuthorEmail, GitRepositoryGrant? grant = null)
    {
        string[] parts = (gitRepository ?? "").Split('/');
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(gitAuthorName) || string.IsNullOrWhiteSpace(gitAuthorEmail) ||
            Array.Exists(gitAuthorName.ToCharArray(), char.IsControl) || Array.Exists(gitAuthorEmail.ToCharArray(), char.IsControl)) throw new WorkRuleException(WorkRule.InvalidValue);
        foreach (string part in parts)
        {
            if (part.Length == 0 || part is "." or "..") throw new WorkRuleException(WorkRule.InvalidValue);
            foreach (char c in part)
                if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) throw new WorkRuleException(WorkRule.InvalidValue);
        }
        GitRepository = gitRepository!;
        GitAuthorName = gitAuthorName;
        GitAuthorEmail = gitAuthorEmail;
        Grant = grant;
    }
}

// Immutable authority captured when an attempt is queued; enforced outside the agent.
public sealed record GitRepositoryGrant
{
    public GitRepositoryGrant()
    {
    }

    public long ConnectionId { get; init; }
    public string Generation { get; init; } = null!;
    public string AccountId { get; init; } = null!;
    public string Login { get; init; } = null!;
    public long GitRepositoryId { get; init; }
    public string BaseBranch { get; init; } = null!;
    public string Branch { get; init; } = null!;
    public int PolicyVersion { get; init; } = 2;
    public bool AllowPush { get; init; }
    public bool AllowPullRequest { get; init; }

    public bool HasSupportedPolicy() => PolicyVersion == 2;

    public bool PublishesChanges() => HasSupportedPolicy() && AllowPush;

    public bool AllowsOperation(GitRepositoryOperationKind operation) =>
        Enum.IsDefined(operation) && HasSupportedPolicy() &&
        operation switch
        {
            GitRepositoryOperationKind.Publish => AllowPush,
            GitRepositoryOperationKind.PullRequest => AllowPush && AllowPullRequest,
            _ => true
        };

    public void Authorize(long workId, long attemptId, string gitRepository, string requestedGitRepository, string branch, GitRepositoryOperationKind operation)
    {
        if (ConnectionId <= 0 || string.IsNullOrWhiteSpace(Generation) ||
            Branch != $"goblin/{workId}/{attemptId}" || branch != Branch || Branch == BaseBranch ||
            !string.Equals(gitRepository, requestedGitRepository, StringComparison.OrdinalIgnoreCase) ||
            !AllowsOperation(operation))
            throw new WorkRuleException(WorkRule.OwnershipMismatch);
    }
}

public sealed record ExecutionSession(string? Model = null, string? SessionReference = null,
    string? OperationReference = null);

public sealed class ExecutionAttempt
{
    internal ExecutionAttempt(long id, long workId, long agentId, ExecutionTarget target, DateTimeOffset now)
    {
        Id = id;
        WorkId = workId;
        AgentId = agentId;
        Target = target;
        QueuedAt = now;
    }

    public long Id { get; }
    public long WorkId { get; }
    public long AgentId { get; }
    public ExecutionTarget Target { get; }
    public DateTimeOffset QueuedAt { get; }
    public AttemptStatus Status { get; internal set; } = AttemptStatus.Queued;
    public long? OwnerId { get; internal set; }
    public string? EnvironmentReference { get; internal set; }
    public ExecutionSession? Session { get; internal set; }
    public DateTimeOffset? ClaimedAt { get; internal set; }
    public DateTimeOffset? StartedAt { get; internal set; }
    public DateTimeOffset? FinishedAt { get; internal set; }
    public DateTimeOffset? CancellationRequestedAt { get; internal set; }
    public FailureKind? Failure { get; internal set; }
    public bool CleanupPending { get; internal set; }
    public bool CleanupFailed { get; internal set; }
    public bool ReasoningOnly { get; internal set; }
    public int TurnNumber { get; internal set; } = 1;
    public int WorkspaceNumber { get; internal set; } = 1;
    public bool ReleaseWorkspace { get; internal set; } = true;
    public long? CheckpointId { get; internal set; }
    public ExecutionTurnRecord[] PriorTurns { get; internal set; } = [];
}

// Runtime turns and physical allocations do not replace the logical attempt.
public sealed record ExecutionTurnRecord
{
    public ExecutionTurnRecord(int number, int workspaceNumber)
    {
        Number = number;
        WorkspaceNumber = workspaceNumber;
    }

    public int Number { get; init; }
    public int WorkspaceNumber { get; init; }
    public long? OwnerId { get; init; }
    public string? EnvironmentReference { get; init; }
    public ExecutionSession? Session { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public long? CheckpointId { get; init; }

    internal static ExecutionTurnRecord Capture(ExecutionAttempt attempt) =>
        new(attempt.TurnNumber, attempt.WorkspaceNumber)
        {
            OwnerId = attempt.OwnerId,
            EnvironmentReference = attempt.EnvironmentReference,
            Session = attempt.Session,
            StartedAt = attempt.StartedAt,
            FinishedAt = attempt.FinishedAt,
            CheckpointId = attempt.CheckpointId
        };
}
