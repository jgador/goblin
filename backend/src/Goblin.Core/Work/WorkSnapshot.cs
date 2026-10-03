using System;

namespace Goblin.Core.Work;

// A versioned, Goblin-owned persistence contract. Infrastructure chooses its
// encoding. Restoring never emits events or executes side effects.
public sealed class WorkSnapshot
{
    public WorkSnapshot(int schemaVersion, long id, string objective, long? agentId, WorkStatus status,
        WorkAttention? attention, AttemptSnapshot[] attempts, WorkEvent[] history, WorkDecision[] decisions,
        WorkResult[] results, WorkMessage[] messages, WorkArtifact[] artifacts)
    {
        SchemaVersion = schemaVersion;
        Id = id;
        Objective = objective;
        AgentId = agentId;
        Status = status;
        Attention = attention;
        Attempts = attempts;
        History = history;
        Decisions = decisions;
        Results = results;
        Messages = messages;
        Artifacts = artifacts;
    }

    public int SchemaVersion { get; init; }

    public long Id { get; init; }

    public string Objective { get; init; }

    public long? AgentId { get; init; }

    public WorkStatus Status { get; init; }

    public WorkAttention? Attention { get; init; }

    public AttemptSnapshot[] Attempts { get; init; }

    public WorkEvent[] History { get; init; }

    public WorkDecision[] Decisions { get; init; }

    public WorkResult[] Results { get; init; }

    public WorkMessage[] Messages { get; init; }

    public WorkArtifact[] Artifacts { get; init; }

    public WorkWorkspace? Workspace { get; init; }
    public WorkRepositoryRequest? RepositoryRequest { get; init; }
    public RepositoryAuthorization? RepositoryAuthorization { get; init; }
}

public sealed class AttemptSnapshot
{
    public AttemptSnapshot(long id, long workId, long agentId, ExecutionTarget target, DateTimeOffset queuedAt,
        AttemptStatus status, long? ownerId, string? environmentReference, ExecutionSession? session,
        DateTimeOffset? claimedAt, DateTimeOffset? startedAt, DateTimeOffset? finishedAt,
        DateTimeOffset? cancellationRequestedAt, FailureKind? failure, bool cleanupPending = false,
        bool cleanupFailed = false)
    {
        Id = id;
        WorkId = workId;
        AgentId = agentId;
        Target = target;
        QueuedAt = queuedAt;
        Status = status;
        OwnerId = ownerId;
        EnvironmentReference = environmentReference;
        Session = session;
        ClaimedAt = claimedAt;
        StartedAt = startedAt;
        FinishedAt = finishedAt;
        CancellationRequestedAt = cancellationRequestedAt;
        Failure = failure;
        CleanupPending = cleanupPending;
        CleanupFailed = cleanupFailed;
    }

    public long Id { get; init; }

    public long WorkId { get; init; }

    public long AgentId { get; init; }

    public ExecutionTarget Target { get; init; }

    public DateTimeOffset QueuedAt { get; init; }

    public AttemptStatus Status { get; init; }

    public long? OwnerId { get; init; }

    public string? EnvironmentReference { get; init; }

    public ExecutionSession? Session { get; init; }

    public DateTimeOffset? ClaimedAt { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }

    public DateTimeOffset? CancellationRequestedAt { get; init; }

    public FailureKind? Failure { get; init; }

    public bool CleanupPending { get; init; }

    public bool CleanupFailed { get; init; }

    public bool ReasoningOnly { get; init; }
    public int TurnNumber { get; init; } = 1;
    public int WorkspaceNumber { get; init; } = 1;
    public bool ReleaseWorkspace { get; init; } = true;
    public long? CheckpointId { get; init; }
    public ExecutionTurnRecord[] PriorTurns { get; init; } = [];
}

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
                a.StartedAt, a.FinishedAt, a.CancellationRequestedAt, a.Failure, a.CleanupPending, a.CleanupFailed)
            {
                ReasoningOnly = a.ReasoningOnly,
                TurnNumber = a.TurnNumber,
                WorkspaceNumber = a.WorkspaceNumber,
                ReleaseWorkspace = a.ReleaseWorkspace,
                CheckpointId = a.CheckpointId,
                PriorTurns = a.PriorTurns
            };
        }
        return new(2, Id, Objective, AgentId, Status, Attention, attempts,
            [.. _history], [.. _decisions], [.. _results],
            [.. _messages], [.. _artifacts])
        { Workspace = Workspace, RepositoryRequest = RepositoryRequest, RepositoryAuthorization = RepositoryAuthorization };
    }

    public static WorkItem Restore(WorkSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Require(state.SchemaVersion == 2 && state.History.Length > 0 &&
            (state.AgentId is null or > 0) && Enum.IsDefined(state.Status), WorkRule.InvalidValue);
        var work = new WorkItem(state.Id, state.Objective, state.History[0].OccurredAt)
        {
            AgentId = state.AgentId,
            Status = state.Status,
            Attention = state.Attention,
            Workspace = state.Workspace,
            RepositoryRequest = state.RepositoryRequest,
            RepositoryAuthorization = state.RepositoryAuthorization
        };
        work._history.Clear();
        for (int i = 0; i < state.History.Length; i++)
        {
            Require(state.History[i].Sequence == i + 1, WorkRule.InvalidValue);
            work._history.Add(state.History[i]);
        }
        foreach (AttemptSnapshot a in state.Attempts)
        {
            Require(a.WorkId == state.Id && a.Id > 0 && a.AgentId > 0 &&
                (a.OwnerId is null or > 0) && Enum.IsDefined(a.Status) &&
                !work._attempts.Exists(x => x.Id == a.Id), WorkRule.InvalidValue);
            work._attempts.Add(new(a.Id, a.WorkId, a.AgentId, a.Target, a.QueuedAt)
            {
                Status = a.Status,
                OwnerId = a.OwnerId,
                EnvironmentReference = a.EnvironmentReference,
                Session = a.Session,
                ClaimedAt = a.ClaimedAt,
                StartedAt = a.StartedAt,
                FinishedAt = a.FinishedAt,
                CancellationRequestedAt = a.CancellationRequestedAt,
                Failure = a.Failure,
                CleanupPending = a.CleanupPending,
                CleanupFailed = a.CleanupFailed,
                ReasoningOnly = a.ReasoningOnly,
                TurnNumber = a.TurnNumber,
                WorkspaceNumber = a.WorkspaceNumber,
                ReleaseWorkspace = a.ReleaseWorkspace,
                CheckpointId = a.CheckpointId,
                PriorTurns = a.PriorTurns
            });
        }
        work._decisions.AddRange(state.Decisions);
        work._results.AddRange(state.Results);
        work._messages.AddRange(state.Messages);
        work._artifacts.AddRange(state.Artifacts);
        return work;
    }
}
