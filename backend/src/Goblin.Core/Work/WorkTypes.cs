using System;

namespace Goblin.Core.Work;

public enum WorkStatus { Ready, Queued, InProgress, Cancelling, NeedsAttention, Completed, Cancelled }
public enum AttemptStatus { Queued, Starting, Running, CancellationRequested, Succeeded, Failed, Uncertain, Cancelled }
public enum AttentionReason { InputRequired, ResultReview, Failure, UncertainExecution, CleanupRequired }

// Integrations translate upstream errors into these categories. Raw exceptions,
// response bodies, and credentials are not inputs to a failure transition.
public enum FailureKind
{
    DispatchFailed, ConnectionUnavailable, CapabilityUnavailable, HostUnavailable,
    ExecutionFailed, TimedOut, RuntimeDisconnected, CancellationFailed, CleanupFailed, StorageUnavailable
}

public enum WorkEventKind
{
    Created, Assigned, ExecutionQueued, ExecutionClaimed, ExecutionStarted,
    ExecutionFailed, ExecutionUncertain, ExecutionStopped, RetryRequested,
    InputRequested, InputProvided, ResultProposed, ChangesRequested, ResultApproved,
    CancellationRequested, Cancelled, ContextAdded, ProgressReported, ArtifactRecorded,
    CleanupRequired, CleanupFailed, CleanupCompleted
}

public enum WorkRule
{
    InvalidValue, InvalidTransition, AgentRequired, AttemptAlreadyExists,
    AttemptNotCurrent, OwnershipMismatch, ReconciliationRequired, DecisionNotCurrent
}

// These errors express product rules; the HTTP adapter chooses status codes.
public sealed class WorkRuleException : Exception
{
    public WorkRuleException(WorkRule rule) : base(rule.ToString()) => Rule = rule;

    public WorkRule Rule { get; }
}

public sealed record WorkAttention(AttentionReason Reason, FailureKind? Failure = null);

public sealed record WorkEvent(long Sequence, DateTimeOffset OccurredAt, WorkEventKind Kind,
    long? AttemptId = null, long? AgentId = null, long? DecisionId = null,
    FailureKind? Failure = null, string? Text = null);

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
    public RepositoryChange? Repository { get; }

    public ExecutionTarget(string runtime, long connectionId, string? requestedModel = null, RepositoryChange? repository = null)
    {
        if (string.IsNullOrWhiteSpace(runtime) || connectionId <= 0 ||
            (requestedModel is not null && string.IsNullOrWhiteSpace(requestedModel)))
            throw new WorkRuleException(WorkRule.InvalidValue);
        Runtime = runtime.Trim();
        ConnectionId = connectionId;
        RequestedModel = requestedModel?.Trim();
        Repository = repository;
    }
}

// Explicitly requested repository changes are an execution requirement, not a
// taxonomy imposed on every Work item. Credentials remain integration-owned.
public sealed record RepositoryChange
{
    public string Repository { get; }
    public string GitAuthorName { get; }
    public string GitAuthorEmail { get; }
    public RepositoryGrant? Grant { get; }
    public RepositoryChange(string repository, string gitAuthorName, string gitAuthorEmail, RepositoryGrant? grant = null)
    {
        string[] parts = (repository ?? "").Split('/');
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(gitAuthorName) || string.IsNullOrWhiteSpace(gitAuthorEmail) ||
            gitAuthorName.Contains('\n') || gitAuthorEmail.Contains('\n')) throw new WorkRuleException(WorkRule.InvalidValue);
        foreach (string part in parts)
        {
            if (part.Length == 0 || part is "." or "..") throw new WorkRuleException(WorkRule.InvalidValue);
            foreach (char c in part)
                if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) throw new WorkRuleException(WorkRule.InvalidValue);
        }
        Repository = repository!;
        GitAuthorName = gitAuthorName;
        GitAuthorEmail = gitAuthorEmail;
        Grant = grant;
    }
}

// Immutable authority captured when an attempt is queued; enforced outside the agent.
public sealed record RepositoryGrant(long ConnectionId, string Generation, string AccountId,
    string Login, long RepositoryId, string BaseBranch, string Branch, int PolicyVersion = 1)
{
    public void Authorize(long workId, long attemptId, string repository, string requestedRepository, string branch, string operation)
    {
        if (ConnectionId <= 0 || string.IsNullOrWhiteSpace(Generation) || PolicyVersion != 1 ||
            Branch != $"goblin/{workId}/{attemptId}" || branch != Branch || Branch == BaseBranch ||
            !string.Equals(repository, requestedRepository, StringComparison.OrdinalIgnoreCase) ||
            operation is not ("publish" or "pull-request" or "fetch"))
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
}
