using System;
using System.Text.Json.Serialization;
using Goblin.Core.Work;

namespace Goblin.Web.Http.Contracts;

public sealed class WorkSnapshot
{
    [JsonConstructor]
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

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("objective")]
    public string Objective { get; init; }

    [JsonPropertyName("agentId")]
    public long? AgentId { get; init; }

    [JsonPropertyName("status")]
    public WorkStatus Status { get; init; }

    [JsonPropertyName("attention")]
    public WorkAttention? Attention { get; init; }

    [JsonPropertyName("attempts")]
    public AttemptSnapshot[] Attempts { get; init; }

    [JsonPropertyName("history")]
    public WorkEvent[] History { get; init; }

    [JsonPropertyName("decisions")]
    public WorkDecision[] Decisions { get; init; }

    [JsonPropertyName("results")]
    public WorkResult[] Results { get; init; }

    [JsonPropertyName("messages")]
    public WorkMessage[] Messages { get; init; }

    [JsonPropertyName("artifacts")]
    public WorkArtifact[] Artifacts { get; init; }

    [JsonPropertyName("workspace")]
    public WorkWorkspace? Workspace { get; init; }

    [JsonPropertyName("repositoryRequest")]
    public WorkRepositoryRequest? RepositoryRequest { get; init; }

    [JsonPropertyName("repositoryAuthorization")]
    public RepositoryAuthorization? RepositoryAuthorization { get; init; }

    public static WorkSnapshot From(Goblin.Core.Work.WorkSnapshot value) =>
        new(value.SchemaVersion, value.Id, value.Objective, value.AgentId, value.Status,
            value.Attention is null ? null : WorkAttention.From(value.Attention),
            Array.ConvertAll(value.Attempts, AttemptSnapshot.From), Array.ConvertAll(value.History, WorkEvent.From),
            Array.ConvertAll(value.Decisions, WorkDecision.From), Array.ConvertAll(value.Results, WorkResult.From),
            Array.ConvertAll(value.Messages, WorkMessage.From), Array.ConvertAll(value.Artifacts, WorkArtifact.From))
        {
            Workspace = value.Workspace is null ? null : WorkWorkspace.From(value.Workspace),
            RepositoryRequest = value.RepositoryRequest is null ? null : WorkRepositoryRequest.From(value.RepositoryRequest),
            RepositoryAuthorization = value.RepositoryAuthorization is null ? null : RepositoryAuthorization.From(value.RepositoryAuthorization)
        };
}

public sealed class AttemptSnapshot
{
    [JsonConstructor]
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

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("workId")]
    public long WorkId { get; init; }

    [JsonPropertyName("agentId")]
    public long AgentId { get; init; }

    [JsonPropertyName("target")]
    public ExecutionTarget Target { get; init; }

    [JsonPropertyName("queuedAt")]
    public DateTimeOffset QueuedAt { get; init; }

    [JsonPropertyName("status")]
    public AttemptStatus Status { get; init; }

    [JsonPropertyName("ownerId")]
    public long? OwnerId { get; init; }

    [JsonPropertyName("environmentReference")]
    public string? EnvironmentReference { get; init; }

    [JsonPropertyName("session")]
    public ExecutionSession? Session { get; init; }

    [JsonPropertyName("claimedAt")]
    public DateTimeOffset? ClaimedAt { get; init; }

    [JsonPropertyName("startedAt")]
    public DateTimeOffset? StartedAt { get; init; }

    [JsonPropertyName("finishedAt")]
    public DateTimeOffset? FinishedAt { get; init; }

    [JsonPropertyName("cancellationRequestedAt")]
    public DateTimeOffset? CancellationRequestedAt { get; init; }

    [JsonPropertyName("failure")]
    public FailureKind? Failure { get; init; }

    [JsonPropertyName("cleanupPending")]
    public bool CleanupPending { get; init; }

    [JsonPropertyName("cleanupFailed")]
    public bool CleanupFailed { get; init; }

    [JsonPropertyName("reasoningOnly")]
    public bool ReasoningOnly { get; init; }

    [JsonPropertyName("turnNumber")]
    public int TurnNumber { get; init; } = 1;

    [JsonPropertyName("workspaceNumber")]
    public int WorkspaceNumber { get; init; } = 1;

    [JsonPropertyName("releaseWorkspace")]
    public bool ReleaseWorkspace { get; init; } = true;

    [JsonPropertyName("checkpointId")]
    public long? CheckpointId { get; init; }

    [JsonPropertyName("priorTurns")]
    public ExecutionTurnRecord[] PriorTurns { get; init; } = [];

    public static AttemptSnapshot From(Goblin.Core.Work.AttemptSnapshot value) =>
        new(value.Id, value.WorkId, value.AgentId, ExecutionTarget.From(value.Target), value.QueuedAt, value.Status,
            value.OwnerId, value.EnvironmentReference,
            value.Session is null ? null : ExecutionSession.From(value.Session), value.ClaimedAt, value.StartedAt,
            value.FinishedAt, value.CancellationRequestedAt, value.Failure, value.CleanupPending, value.CleanupFailed)
        {
            ReasoningOnly = value.ReasoningOnly,
            TurnNumber = value.TurnNumber,
            WorkspaceNumber = value.WorkspaceNumber,
            ReleaseWorkspace = value.ReleaseWorkspace,
            CheckpointId = value.CheckpointId,
            PriorTurns = Array.ConvertAll(value.PriorTurns, ExecutionTurnRecord.From)
        };
}

public sealed class WorkWorkspace
{
    [JsonConstructor]
    public WorkWorkspace(string repository, string environmentReference, long attemptId, int workspaceNumber,
        int turnNumber)
    {
        Repository = repository;
        EnvironmentReference = environmentReference;
        AttemptId = attemptId;
        WorkspaceNumber = workspaceNumber;
        TurnNumber = turnNumber;
    }

    [JsonPropertyName("repository")]
    public string Repository { get; init; }

    [JsonPropertyName("environmentReference")]
    public string EnvironmentReference { get; init; }

    [JsonPropertyName("attemptId")]
    public long AttemptId { get; init; }

    [JsonPropertyName("workspaceNumber")]
    public int WorkspaceNumber { get; init; }

    [JsonPropertyName("turnNumber")]
    public int TurnNumber { get; init; }

    public static WorkWorkspace From(Goblin.Core.Work.WorkWorkspace value) =>
        new(value.Repository, value.EnvironmentReference, value.AttemptId, value.WorkspaceNumber, value.TurnNumber);
}

public sealed class WorkRepositoryRequest
{
    [JsonConstructor]
    public WorkRepositoryRequest(ExecutionTarget target, string[] repositories)
    {
        Target = target;
        Repositories = repositories;
    }

    [JsonPropertyName("target")]
    public ExecutionTarget Target { get; init; }

    [JsonPropertyName("repositories")]
    public string[] Repositories { get; init; }

    public static WorkRepositoryRequest From(Goblin.Core.Work.WorkRepositoryRequest value) =>
        new(ExecutionTarget.From(value.Target), value.Repositories);
}

public sealed class RepositoryAuthorization
{
    [JsonConstructor]
    public RepositoryAuthorization(long id, ExecutionTarget target, bool enableRepository, bool retry,
        DateTimeOffset requestedAt, RepositoryAuthorizationStatus status = RepositoryAuthorizationStatus.Pending,
        DateTimeOffset? answeredAt = null, string? defaultBranch = null)
    {
        Id = id;
        Target = target;
        EnableRepository = enableRepository;
        Retry = retry;
        RequestedAt = requestedAt;
        Status = status;
        AnsweredAt = answeredAt;
        DefaultBranch = defaultBranch;
    }

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("target")]
    public ExecutionTarget Target { get; init; }

    [JsonPropertyName("enableRepository")]
    public bool EnableRepository { get; init; }

    [JsonPropertyName("retry")]
    public bool Retry { get; init; }

    [JsonPropertyName("requestedAt")]
    public DateTimeOffset RequestedAt { get; init; }

    [JsonPropertyName("status")]
    public RepositoryAuthorizationStatus Status { get; init; }

    [JsonPropertyName("answeredAt")]
    public DateTimeOffset? AnsweredAt { get; init; }

    [JsonPropertyName("defaultBranch")]
    public string? DefaultBranch { get; init; }

    public static RepositoryAuthorization From(Goblin.Core.Work.RepositoryAuthorization value) =>
        new(value.Id, ExecutionTarget.From(value.Target), value.EnableRepository, value.Retry, value.RequestedAt,
            value.Status, value.AnsweredAt, value.DefaultBranch);
}
