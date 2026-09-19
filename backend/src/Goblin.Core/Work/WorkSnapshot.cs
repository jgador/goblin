using System;

namespace Goblin.Core.Work;

// A versioned, Goblin-owned persistence contract. Infrastructure chooses its
// encoding. Restoring never emits events or executes side effects.
public sealed record WorkSnapshot(int SchemaVersion, Guid Id, string Objective,
    Guid? AgentId, WorkStatus Status, WorkAttention? Attention,
    AttemptSnapshot[] Attempts, WorkEvent[] History, WorkDecision[] Decisions,
    WorkResult[] Results, WorkMessage[] Messages, WorkArtifact[] Artifacts);

public sealed record AttemptSnapshot(Guid Id, Guid WorkId, Guid AgentId,
    ExecutionTarget Target, DateTimeOffset QueuedAt, AttemptStatus Status,
    Guid? OwnerId, string? EnvironmentReference, ExecutionSession? Session,
    DateTimeOffset? ClaimedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    DateTimeOffset? CancellationRequestedAt, FailureKind? Failure,
    bool CleanupPending = false, bool CleanupFailed = false);

public sealed partial class WorkItem
{
    public WorkSnapshot Snapshot()
    {
        var attempts = new AttemptSnapshot[_attempts.Count];
        for (int i = 0; i < attempts.Length; i++)
        {
            ExecutionAttempt a = _attempts[i];
            attempts[i] = new(a.Id, a.WorkId, a.AgentId, a.Target, a.QueuedAt,
                a.Status, a.OwnerId, a.EnvironmentReference, a.Session, a.ClaimedAt,
                a.StartedAt, a.FinishedAt, a.CancellationRequestedAt, a.Failure, a.CleanupPending, a.CleanupFailed);
        }
        return new(1, Id, Objective, AgentId, Status, Attention, attempts,
            _history.ToArray(), _decisions.ToArray(), _results.ToArray(),
            _messages.ToArray(), _artifacts.ToArray());
    }

    public static WorkItem Restore(WorkSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Require(state.SchemaVersion == 1 && state.History.Length > 0 &&
            Enum.IsDefined(state.Status), WorkRule.InvalidValue);
        var work = new WorkItem(state.Id, state.Objective, state.History[0].OccurredAt)
        {
            AgentId = state.AgentId, Status = state.Status, Attention = state.Attention
        };
        work._history.Clear();
        for (int i = 0; i < state.History.Length; i++)
        {
            Require(state.History[i].Sequence == i + 1, WorkRule.InvalidValue);
            work._history.Add(state.History[i]);
        }
        foreach (AttemptSnapshot a in state.Attempts)
        {
            Require(a.WorkId == state.Id && a.Id != Guid.Empty && a.AgentId != Guid.Empty &&
                Enum.IsDefined(a.Status) && !work._attempts.Exists(x => x.Id == a.Id), WorkRule.InvalidValue);
            work._attempts.Add(new(a.Id, a.WorkId, a.AgentId, a.Target, a.QueuedAt)
            {
                Status = a.Status, OwnerId = a.OwnerId, EnvironmentReference = a.EnvironmentReference,
                Session = a.Session, ClaimedAt = a.ClaimedAt, StartedAt = a.StartedAt,
                FinishedAt = a.FinishedAt, CancellationRequestedAt = a.CancellationRequestedAt, Failure = a.Failure,
                CleanupPending = a.CleanupPending, CleanupFailed = a.CleanupFailed
            });
        }
        work._decisions.AddRange(state.Decisions);
        work._results.AddRange(state.Results);
        work._messages.AddRange(state.Messages);
        work._artifacts.AddRange(state.Artifacts);
        return work;
    }
}
