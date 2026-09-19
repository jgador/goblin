using System;
using System.Collections.Generic;

namespace Goblin.Core.Work;

// This aggregate owns lifecycle rules. Persistence must serialize commands for
// a Work item and atomically commit state, history, and dispatch intent. A claim
// here is not a cross-process lock or a guarantee of exactly-once external work.
public sealed partial class WorkItem
{
    private readonly List<ExecutionAttempt> _attempts = [];
    private readonly List<WorkEvent> _history = [];
    private readonly List<WorkDecision> _decisions = [];
    private readonly List<WorkResult> _results = [];
    private readonly List<WorkMessage> _messages = [];
    private readonly List<WorkArtifact> _artifacts = [];

    public Guid Id { get; }
    public string Objective { get; }
    public Guid? AgentId { get; private set; }
    public WorkStatus Status { get; private set; } = WorkStatus.Ready;
    public WorkAttention? Attention { get; private set; }
    public IReadOnlyList<ExecutionAttempt> Attempts { get; }
    public IReadOnlyList<WorkEvent> History { get; }
    public IReadOnlyList<WorkDecision> Decisions { get; }
    public IReadOnlyList<WorkResult> Results { get; }
    public IReadOnlyList<WorkMessage> Messages { get; }
    public IReadOnlyList<WorkArtifact> Artifacts { get; }
    public ExecutionAttempt? CurrentAttempt => _attempts.Count == 0 ? null : _attempts[^1];

    public WorkItem(Guid id, string objective, DateTimeOffset now)
    {
        RequireId(id);
        Id = id;
        Objective = RequireText(objective);
        Attempts = _attempts.AsReadOnly();
        History = _history.AsReadOnly();
        Decisions = _decisions.AsReadOnly();
        Results = _results.AsReadOnly();
        Messages = _messages.AsReadOnly();
        Artifacts = _artifacts.AsReadOnly();
        Record(WorkEventKind.Created, now, text: Objective);
    }

    public void AddContext(Guid messageId, string text, DateTimeOffset now)
    {
        RequireId(messageId);
        Require(!_messages.Exists(x => x.Id == messageId), WorkRule.InvalidValue);
        string input = RequireText(text);
        _messages.Add(new(messageId, input, now));
        Record(WorkEventKind.ContextAdded, now, text: input);
    }

    public void ReportProgress(Guid attemptId, Guid ownerId, string text, DateTimeOffset now)
    {
        OwnedActiveAttempt(attemptId, ownerId);
        Record(WorkEventKind.ProgressReported, now, attemptId, text: RequireText(text));
    }

    public void AddArtifact(Guid attemptId, Guid ownerId, string reference, string name, DateTimeOffset now)
    {
        OwnedAttempt(attemptId, ownerId);
        string location = RequireText(reference);
        string title = RequireText(name);
        if (_artifacts.Exists(x => x.AttemptId == attemptId && x.Reference == location)) return;
        _artifacts.Add(new(attemptId, location, title, now));
        Record(WorkEventKind.ArtifactRecorded, now, attemptId, text: title);
    }

    public void Assign(Guid agentId, DateTimeOffset now)
    {
        RequireId(agentId);
        Require(Status == WorkStatus.Ready, WorkRule.InvalidTransition);
        if (AgentId == agentId) return;
        AgentId = agentId;
        Record(WorkEventKind.Assigned, now, agentId: agentId);
    }

    public void QueueExecution(Guid attemptId, ExecutionTarget target, DateTimeOffset now)
    {
        Require(Status == WorkStatus.Ready, WorkRule.InvalidTransition);
        ValidateNewAttempt(attemptId, target);
        Queue(attemptId, target, now);
    }

    // Only an explicit retry command can leave failure attention. Temporary
    // failures use precisely the same transition as other failures.
    public void RetryExecution(Guid attemptId, ExecutionTarget target, DateTimeOffset now)
    {
        Require(CurrentAttempt?.Status != AttemptStatus.Uncertain, WorkRule.ReconciliationRequired);
        Require(Status == WorkStatus.NeedsAttention && Attention?.Reason == AttentionReason.Failure &&
            CurrentAttempt?.Status == AttemptStatus.Failed, WorkRule.InvalidTransition);
        ValidateNewAttempt(attemptId, target);
        Record(WorkEventKind.RetryRequested, now, CurrentAttempt!.Id);
        Queue(attemptId, target, now);
    }

    // Commit the successful claim before launching anything externally. Every
    // subsequent delivery, even from this owner, returns false and must not launch.
    public bool TryClaimExecution(Guid attemptId, Guid ownerId, string environmentReference, DateTimeOffset now)
    {
        RequireId(ownerId);
        string environment = RequireText(environmentReference);
        if (CurrentAttempt is not { Status: AttemptStatus.Queued } attempt || attempt.Id != attemptId ||
            Status != WorkStatus.Queued) return false;
        attempt.Status = AttemptStatus.Starting;
        attempt.OwnerId = ownerId;
        attempt.EnvironmentReference = environment;
        attempt.ClaimedAt = now;
        Status = WorkStatus.InProgress;
        Record(WorkEventKind.ExecutionClaimed, now, attempt.Id);
        return true;
    }

    public void ExecutionStarted(Guid attemptId, Guid ownerId, ExecutionSession session, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);
        ExecutionAttempt attempt = OwnedAttempt(attemptId, ownerId);
        Require(attempt.Status is AttemptStatus.Starting or AttemptStatus.CancellationRequested or
            AttemptStatus.Uncertain or AttemptStatus.Succeeded or AttemptStatus.Failed or AttemptStatus.Cancelled,
            WorkRule.InvalidTransition);
        Require(attempt.StartedAt is null, WorkRule.InvalidTransition);
        attempt.Session = session;
        attempt.StartedAt = now;
        // A delayed acknowledgement can add provenance after a result or failure,
        // but cannot erase attention, cancellation, or a confirmed outcome.
        if (attempt.Status == AttemptStatus.Starting) attempt.Status = AttemptStatus.Running;
        Record(WorkEventKind.ExecutionStarted, now, attempt.Id);
    }

    public void DispatchFailed(Guid attemptId, FailureKind failure, DateTimeOffset now)
    {
        RequireFailure(failure);
        ExecutionAttempt attempt = RequireCurrent(attemptId);
        Require(attempt.Status == AttemptStatus.Queued, WorkRule.InvalidTransition);
        Fail(attempt, failure, uncertain: false, now);
    }

    // Call only with host/runtime evidence that the execution is no longer
    // running. A timeout or lost connection alone belongs in ExecutionUncertain.
    public void ExecutionFailed(Guid attemptId, Guid ownerId, FailureKind failure, DateTimeOffset now)
    {
        RequireFailure(failure);
        ExecutionAttempt attempt = OwnedActiveAttempt(attemptId, ownerId);
        Fail(attempt, failure, uncertain: false, now);
    }

    public void ExecutionUncertain(Guid attemptId, Guid ownerId, FailureKind failure, DateTimeOffset now)
    {
        RequireFailure(failure);
        ExecutionAttempt attempt = OwnedActiveAttempt(attemptId, ownerId);
        Fail(attempt, failure, uncertain: true, now);
    }

    // Reconciliation confirms a stopped execution; it never queues replacement
    // work. The host adapter is responsible for obtaining trustworthy evidence.
    public void ConfirmExecutionStopped(Guid attemptId, Guid ownerId, DateTimeOffset now)
    {
        ExecutionAttempt attempt = OwnedAttempt(attemptId, ownerId);
        Require(attempt.Status is AttemptStatus.Uncertain or AttemptStatus.CancellationRequested, WorkRule.InvalidTransition);
        attempt.FinishedAt = now;
        Record(WorkEventKind.ExecutionStopped, now, attempt.Id);
        if (attempt.CancellationRequestedAt is not null)
        {
            attempt.Status = AttemptStatus.Cancelled;
            Status = WorkStatus.Cancelled;
            Attention = null;
            Record(WorkEventKind.Cancelled, now, attempt.Id);
        }
        else
        {
            attempt.Status = AttemptStatus.Failed;
            Status = WorkStatus.NeedsAttention;
            Attention = new(AttentionReason.Failure, attempt.Failure);
        }
    }

    public void ProposeResult(Guid attemptId, Guid ownerId, string text, DateTimeOffset now)
    {
        string result = RequireText(text);
        ExecutionAttempt attempt = CompletableAttempt(attemptId, ownerId);
        Complete(attempt, now);
        _results.Add(new(attemptId, result, now));
        Status = WorkStatus.NeedsAttention;
        Attention = new(AttentionReason.ResultReview);
        Record(WorkEventKind.ResultProposed, now, attemptId, text: result);
    }

    // An input request is a completed runtime interaction with an unresolved
    // Work decision. Live runtime approvals will need a separate capability.
    public void RequestInput(Guid attemptId, Guid ownerId, Guid decisionId, string question, DateTimeOffset now)
    {
        RequireId(decisionId);
        string input = RequireText(question);
        Require(!_decisions.Exists(x => x.Id == decisionId), WorkRule.InvalidValue);
        ExecutionAttempt attempt = CompletableAttempt(attemptId, ownerId);
        Complete(attempt, now);
        _decisions.Add(new(decisionId, attemptId, input, now));
        Status = WorkStatus.NeedsAttention;
        Attention = new(AttentionReason.InputRequired);
        Record(WorkEventKind.InputRequested, now, attemptId, decisionId: decisionId, text: input);
    }

    public void AnswerDecision(Guid decisionId, string answer, DateTimeOffset now)
    {
        Require(CurrentAttempt?.CleanupPending != true, WorkRule.ReconciliationRequired);
        string input = RequireText(answer);
        Require(Status == WorkStatus.NeedsAttention && Attention?.Reason == AttentionReason.InputRequired,
            WorkRule.InvalidTransition);
        Require(_decisions.Count > 0 && _decisions[^1].Id == decisionId && _decisions[^1].Answer is null,
            WorkRule.DecisionNotCurrent);
        _decisions[^1] = _decisions[^1] with { Answer = input, AnsweredAt = now };
        Status = WorkStatus.Ready;
        Attention = null;
        Record(WorkEventKind.InputProvided, now, CurrentAttempt!.Id, decisionId: decisionId, text: input);
    }

    public void RequestChanges(Guid resultAttemptId, string feedback, DateTimeOffset now)
    {
        string input = RequireText(feedback);
        RequireReview(resultAttemptId);
        _results[^1] = _results[^1] with { RequestedChanges = input };
        Status = WorkStatus.Ready;
        Attention = null;
        Record(WorkEventKind.ChangesRequested, now, resultAttemptId, text: input);
    }

    public void ApproveResult(Guid resultAttemptId, DateTimeOffset now)
    {
        RequireReview(resultAttemptId);
        _results[^1] = _results[^1] with { ApprovedAt = now };
        Status = WorkStatus.Completed;
        Attention = null;
        Record(WorkEventKind.ResultApproved, now, resultAttemptId);
    }

    public void RequestCancellation(DateTimeOffset now)
    {
        Require(CurrentAttempt?.CleanupPending != true, WorkRule.ReconciliationRequired);
        if (Status is WorkStatus.Cancelled or WorkStatus.Cancelling) return;
        Require(Status != WorkStatus.Completed, WorkRule.InvalidTransition);
        ExecutionAttempt? attempt = CurrentAttempt;
        Record(WorkEventKind.CancellationRequested, now, attempt?.Id);
        if (attempt is not null && attempt.Status is AttemptStatus.Starting or AttemptStatus.Running or AttemptStatus.Uncertain)
        {
            attempt.Status = AttemptStatus.CancellationRequested;
            attempt.CancellationRequestedAt ??= now;
            Status = WorkStatus.Cancelling;
            Attention = null;
            return;
        }
        if (attempt?.Status == AttemptStatus.Queued)
        {
            attempt.Status = AttemptStatus.Cancelled;
            attempt.CancellationRequestedAt = now;
            attempt.FinishedAt = now;
        }
        Status = WorkStatus.Cancelled;
        Attention = null;
        Record(WorkEventKind.Cancelled, now, attempt?.Id);
    }

    private void RequireReview(Guid resultAttemptId)
    {
        Require(CurrentAttempt?.CleanupPending != true, WorkRule.ReconciliationRequired);
        RequireCurrent(resultAttemptId);
        Require(Status == WorkStatus.NeedsAttention && Attention?.Reason == AttentionReason.ResultReview &&
            _results.Count > 0 && _results[^1].AttemptId == resultAttemptId, WorkRule.InvalidTransition);
    }

    private void ValidateNewAttempt(Guid attemptId, ExecutionTarget target)
    {
        Require(CurrentAttempt?.CleanupPending != true, WorkRule.ReconciliationRequired);
        RequireId(attemptId);
        ArgumentNullException.ThrowIfNull(target);
        Require(AgentId is not null, WorkRule.AgentRequired);
        Require(!_attempts.Exists(x => x.Id == attemptId), WorkRule.AttemptAlreadyExists);
    }

    private void Queue(Guid attemptId, ExecutionTarget target, DateTimeOffset now)
    {
        _attempts.Add(new(attemptId, Id, AgentId!.Value, target, now));
        Status = WorkStatus.Queued;
        Attention = null;
        Record(WorkEventKind.ExecutionQueued, now, attemptId, AgentId);
    }

    private ExecutionAttempt RequireCurrent(Guid attemptId)
    {
        Require(CurrentAttempt is not null && CurrentAttempt.Id == attemptId, WorkRule.AttemptNotCurrent);
        return CurrentAttempt!;
    }

    private ExecutionAttempt OwnedAttempt(Guid attemptId, Guid ownerId)
    {
        RequireId(ownerId);
        ExecutionAttempt attempt = RequireCurrent(attemptId);
        Require(attempt.OwnerId == ownerId, WorkRule.OwnershipMismatch);
        return attempt;
    }

    private ExecutionAttempt OwnedActiveAttempt(Guid attemptId, Guid ownerId)
    {
        ExecutionAttempt attempt = OwnedAttempt(attemptId, ownerId);
        Require(attempt.Status is AttemptStatus.Starting or AttemptStatus.Running or AttemptStatus.CancellationRequested,
            WorkRule.InvalidTransition);
        return attempt;
    }

    private ExecutionAttempt CompletableAttempt(Guid attemptId, Guid ownerId)
    {
        ExecutionAttempt attempt = OwnedAttempt(attemptId, ownerId);
        Require(attempt.Status is AttemptStatus.Starting or AttemptStatus.Running or AttemptStatus.CancellationRequested or AttemptStatus.Uncertain,
            WorkRule.InvalidTransition);
        return attempt;
    }

    private static void Complete(ExecutionAttempt attempt, DateTimeOffset now)
    {
        attempt.Status = AttemptStatus.Succeeded;
        attempt.FinishedAt = now;
    }

    private void Fail(ExecutionAttempt attempt, FailureKind failure, bool uncertain, DateTimeOffset now)
    {
        attempt.Status = uncertain ? AttemptStatus.Uncertain : AttemptStatus.Failed;
        attempt.Failure = failure;
        attempt.FinishedAt = uncertain ? null : now;
        Status = WorkStatus.NeedsAttention;
        Attention = new(uncertain ? AttentionReason.UncertainExecution : AttentionReason.Failure, failure);
        Record(uncertain ? WorkEventKind.ExecutionUncertain : WorkEventKind.ExecutionFailed,
            now, attempt.Id, failure: failure);
    }

    private void Record(WorkEventKind kind, DateTimeOffset now, Guid? attemptId = null,
        Guid? agentId = null, Guid? decisionId = null, FailureKind? failure = null, string? text = null) =>
        _history.Add(new(_history.Count + 1L, now, kind, attemptId, agentId, decisionId, failure, text));

    private static string RequireText(string value)
    {
        Require(!string.IsNullOrWhiteSpace(value), WorkRule.InvalidValue);
        return value.Trim();
    }

    private static void RequireId(Guid id) => Require(id != Guid.Empty, WorkRule.InvalidValue);
    private static void RequireFailure(FailureKind failure) => Require(Enum.IsDefined(failure), WorkRule.InvalidValue);
    private static void Require(bool condition, WorkRule rule)
    {
        if (!condition) throw new WorkRuleException(rule);
    }
}
