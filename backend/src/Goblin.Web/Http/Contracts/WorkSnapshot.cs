using System;
using System.Text.Json.Serialization;
using Goblin.Core.Work;

namespace Goblin.Web.Http.Contracts;

public sealed class WorkSnapshot
{
    [JsonConstructor]
    public WorkSnapshot()
    {
    }

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("objective")]
    public string Objective { get; init; } = null!;

    [JsonPropertyName("agentId")]
    public long? AgentId { get; init; }

    [JsonPropertyName("status")]
    public WorkStatus Status { get; init; }

    [JsonPropertyName("attention")]
    public WorkAttention? Attention { get; init; }

    [JsonPropertyName("attempts")]
    public AttemptSnapshot[] Attempts { get; init; } = null!;

    [JsonPropertyName("history")]
    public WorkEvent[] History { get; init; } = null!;

    [JsonPropertyName("decisions")]
    public WorkDecision[] Decisions { get; init; } = null!;

    [JsonPropertyName("results")]
    public WorkResult[] Results { get; init; } = null!;

    [JsonPropertyName("messages")]
    public WorkMessage[] Messages { get; init; } = null!;

    [JsonPropertyName("artifacts")]
    public WorkArtifact[] Artifacts { get; init; } = null!;

    [JsonPropertyName("workspace")]
    public WorkWorkspace? Workspace { get; init; }

    [JsonPropertyName("repositoryRequest")]
    public WorkGitRepositoryRequest? GitRepositoryRequest { get; init; }

    [JsonPropertyName("repositoryAuthorization")]
    public GitRepositoryAuthorization? GitRepositoryAuthorization { get; init; }

    public static WorkSnapshot From(Goblin.Core.Work.WorkSnapshot value) =>
        new()
        {
            SchemaVersion = value.SchemaVersion,
            Id = value.Id,
            Objective = value.Objective,
            AgentId = value.AgentId,
            Status = value.Status,
            Attention = value.Attention is null ? null : WorkAttention.From(value.Attention),
            Attempts = Array.ConvertAll(value.Attempts, AttemptSnapshot.From),
            History = Array.ConvertAll(value.History, WorkEvent.From),
            Decisions = Array.ConvertAll(value.Decisions, WorkDecision.From),
            Results = Array.ConvertAll(value.Results, WorkResult.From),
            Messages = Array.ConvertAll(value.Messages, WorkMessage.From),
            Artifacts = Array.ConvertAll(value.Artifacts, WorkArtifact.From),
            Workspace = value.Workspace is null ? null : WorkWorkspace.From(value.Workspace),
            GitRepositoryRequest = value.GitRepositoryRequest is null ? null : WorkGitRepositoryRequest.From(value.GitRepositoryRequest),
            GitRepositoryAuthorization = value.GitRepositoryAuthorization is null ? null : GitRepositoryAuthorization.From(value.GitRepositoryAuthorization)
        };
}

public sealed class AttemptSnapshot
{
    [JsonConstructor]
    public AttemptSnapshot()
    {
    }

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("workId")]
    public long WorkId { get; init; }

    [JsonPropertyName("agentId")]
    public long AgentId { get; init; }

    [JsonPropertyName("target")]
    public ExecutionTarget Target { get; init; } = null!;

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
        new()
        {
            Id = value.Id,
            WorkId = value.WorkId,
            AgentId = value.AgentId,
            Target = ExecutionTarget.From(value.Target),
            QueuedAt = value.QueuedAt,
            Status = value.Status,
            OwnerId = value.OwnerId,
            EnvironmentReference = value.EnvironmentReference,
            Session = value.Session is null ? null : ExecutionSession.From(value.Session),
            ClaimedAt = value.ClaimedAt,
            StartedAt = value.StartedAt,
            FinishedAt = value.FinishedAt,
            CancellationRequestedAt = value.CancellationRequestedAt,
            Failure = value.Failure,
            CleanupPending = value.CleanupPending,
            CleanupFailed = value.CleanupFailed,
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
    public WorkWorkspace(string gitRepository, string environmentReference, long attemptId, int workspaceNumber,
        int turnNumber)
    {
        GitRepository = gitRepository;
        EnvironmentReference = environmentReference;
        AttemptId = attemptId;
        WorkspaceNumber = workspaceNumber;
        TurnNumber = turnNumber;
    }

    [JsonPropertyName("repository")]
    public string GitRepository { get; init; }

    [JsonPropertyName("environmentReference")]
    public string EnvironmentReference { get; init; }

    [JsonPropertyName("attemptId")]
    public long AttemptId { get; init; }

    [JsonPropertyName("workspaceNumber")]
    public int WorkspaceNumber { get; init; }

    [JsonPropertyName("turnNumber")]
    public int TurnNumber { get; init; }

    public static WorkWorkspace From(Goblin.Core.Work.WorkWorkspace value) =>
        new(value.GitRepository, value.EnvironmentReference, value.AttemptId, value.WorkspaceNumber, value.TurnNumber);
}

public sealed class WorkGitRepositoryRequest
{
    [JsonConstructor]
    public WorkGitRepositoryRequest(ExecutionTarget target, string[] gitRepositories)
    {
        Target = target;
        GitRepositories = gitRepositories;
    }

    [JsonPropertyName("target")]
    public ExecutionTarget Target { get; init; }

    [JsonPropertyName("repositories")]
    public string[] GitRepositories { get; init; }

    public static WorkGitRepositoryRequest From(Goblin.Core.Work.WorkGitRepositoryRequest value) =>
        new(ExecutionTarget.From(value.Target), value.GitRepositories);
}

public sealed class GitRepositoryAuthorization
{
    [JsonConstructor]
    public GitRepositoryAuthorization()
    {
    }

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("target")]
    public ExecutionTarget Target { get; init; } = null!;

    [JsonPropertyName("enableRepository")]
    public bool EnableGitRepository { get; init; }

    [JsonPropertyName("retry")]
    public bool Retry { get; init; }

    [JsonPropertyName("requestedAt")]
    public DateTimeOffset RequestedAt { get; init; }

    [JsonPropertyName("status")]
    public GitRepositoryAuthorizationStatus Status { get; init; } = GitRepositoryAuthorizationStatus.Pending;

    [JsonPropertyName("answeredAt")]
    public DateTimeOffset? AnsweredAt { get; init; }

    [JsonPropertyName("defaultBranch")]
    public string? DefaultBranch { get; init; }

    public static GitRepositoryAuthorization From(Goblin.Core.Work.GitRepositoryAuthorization value) =>
        new()
        {
            Id = value.Id,
            Target = ExecutionTarget.From(value.Target),
            EnableGitRepository = value.EnableGitRepository,
            Retry = value.Retry,
            RequestedAt = value.RequestedAt,
            Status = value.Status,
            AnsweredAt = value.AnsweredAt,
            DefaultBranch = value.DefaultBranch
        };
}
