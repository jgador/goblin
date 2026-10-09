using System;
using System.Text.Json.Serialization;
using Goblin.Core.Work;

namespace Goblin.Web.Http.Contracts;

public sealed class WorkAttention
{
    [JsonConstructor]
    public WorkAttention(AttentionReason reason, FailureKind? failure = null)
    {
        Reason = reason;
        Failure = failure;
    }

    [JsonPropertyName("reason")]
    public AttentionReason Reason { get; init; }

    [JsonPropertyName("failure")]
    public FailureKind? Failure { get; init; }

    public static WorkAttention From(Goblin.Core.Work.WorkAttention value) =>
        new(value.Reason, value.Failure);
}

public sealed class WorkEvent
{
    [JsonConstructor]
    public WorkEvent()
    {
    }

    [JsonPropertyName("sequence")]
    public long Sequence { get; init; }

    [JsonPropertyName("occurredAt")]
    public DateTimeOffset OccurredAt { get; init; }

    [JsonPropertyName("kind")]
    public WorkEventKind Kind { get; init; }

    [JsonPropertyName("attemptId")]
    public long? AttemptId { get; init; }

    [JsonPropertyName("agentId")]
    public long? AgentId { get; init; }

    [JsonPropertyName("decisionId")]
    public long? DecisionId { get; init; }

    [JsonPropertyName("failure")]
    public FailureKind? Failure { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    public static WorkEvent From(Goblin.Core.Work.WorkEvent value) =>
        new()
        {
            Sequence = value.Sequence,
            OccurredAt = value.OccurredAt,
            Kind = value.Kind,
            AttemptId = value.AttemptId,
            AgentId = value.AgentId,
            DecisionId = value.DecisionId,
            Failure = value.Failure,
            Text = value.Text
        };
}

public sealed class WorkDecision
{
    [JsonConstructor]
    public WorkDecision(long id, long attemptId, string question, DateTimeOffset requestedAt, string? answer = null,
        DateTimeOffset? answeredAt = null)
    {
        Id = id;
        AttemptId = attemptId;
        Question = question;
        RequestedAt = requestedAt;
        Answer = answer;
        AnsweredAt = answeredAt;
    }

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("attemptId")]
    public long AttemptId { get; init; }

    [JsonPropertyName("question")]
    public string Question { get; init; }

    [JsonPropertyName("requestedAt")]
    public DateTimeOffset RequestedAt { get; init; }

    [JsonPropertyName("answer")]
    public string? Answer { get; init; }

    [JsonPropertyName("answeredAt")]
    public DateTimeOffset? AnsweredAt { get; init; }

    public static WorkDecision From(Goblin.Core.Work.WorkDecision value) =>
        new(value.Id, value.AttemptId, value.Question, value.RequestedAt, value.Answer, value.AnsweredAt);
}

public sealed class WorkResult
{
    [JsonConstructor]
    public WorkResult(long attemptId, string text, DateTimeOffset proposedAt, DateTimeOffset? approvedAt = null,
        string? requestedChanges = null)
    {
        AttemptId = attemptId;
        Text = text;
        ProposedAt = proposedAt;
        ApprovedAt = approvedAt;
        RequestedChanges = requestedChanges;
    }

    [JsonPropertyName("attemptId")]
    public long AttemptId { get; init; }

    [JsonPropertyName("text")]
    public string Text { get; init; }

    [JsonPropertyName("proposedAt")]
    public DateTimeOffset ProposedAt { get; init; }

    [JsonPropertyName("approvedAt")]
    public DateTimeOffset? ApprovedAt { get; init; }

    [JsonPropertyName("requestedChanges")]
    public string? RequestedChanges { get; init; }

    public static WorkResult From(Goblin.Core.Work.WorkResult value) =>
        new(value.AttemptId, value.Text, value.ProposedAt, value.ApprovedAt, value.RequestedChanges);
}

public sealed class WorkMessage
{
    [JsonConstructor]
    public WorkMessage(long id, string text, DateTimeOffset createdAt)
    {
        Id = id;
        Text = text;
        CreatedAt = createdAt;
    }

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("text")]
    public string Text { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    public static WorkMessage From(Goblin.Core.Work.WorkMessage value) =>
        new(value.Id, value.Text, value.CreatedAt);
}

public sealed class WorkArtifact
{
    [JsonConstructor]
    public WorkArtifact(long attemptId, string reference, string name, DateTimeOffset createdAt)
    {
        AttemptId = attemptId;
        Reference = reference;
        Name = name;
        CreatedAt = createdAt;
    }

    [JsonPropertyName("attemptId")]
    public long AttemptId { get; init; }

    [JsonPropertyName("reference")]
    public string Reference { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    public static WorkArtifact From(Goblin.Core.Work.WorkArtifact value) =>
        new(value.AttemptId, value.Reference, value.Name, value.CreatedAt);
}

public sealed class ExecutionTarget
{
    [JsonConstructor]
    public ExecutionTarget(string runtime, long connectionId, string? requestedModel = null,
        GitRepositoryChange? gitRepository = null, string? requestedEffort = null)
    {
        Runtime = runtime;
        ConnectionId = connectionId;
        RequestedModel = requestedModel;
        GitRepository = gitRepository;
        RequestedEffort = requestedEffort;
    }

    [JsonPropertyName("runtime")]
    public string Runtime { get; init; }

    [JsonPropertyName("connectionId")]
    public long ConnectionId { get; init; }

    [JsonPropertyName("requestedModel")]
    public string? RequestedModel { get; init; }

    [JsonPropertyName("repository")]
    public GitRepositoryChange? GitRepository { get; init; }

    [JsonPropertyName("requestedEffort")]
    public string? RequestedEffort { get; init; }

    public static ExecutionTarget From(Goblin.Core.Work.ExecutionTarget value) =>
        new(value.Runtime, value.ConnectionId, value.RequestedModel,
            value.GitRepository is null ? null : GitRepositoryChange.From(value.GitRepository), value.RequestedEffort);
}

public sealed class GitRepositoryChange
{
    [JsonConstructor]
    public GitRepositoryChange(string gitRepository, string? gitAuthorName = null, string? gitAuthorEmail = null,
        GitRepositoryGrant? grant = null)
    {
        GitRepository = gitRepository;
        GitAuthorName = gitAuthorName;
        GitAuthorEmail = gitAuthorEmail;
        Grant = grant;
    }

    [JsonPropertyName("repository")]
    public string GitRepository { get; init; }

    [JsonPropertyName("gitAuthorName")]
    public string? GitAuthorName { get; init; }

    [JsonPropertyName("gitAuthorEmail")]
    public string? GitAuthorEmail { get; init; }

    [JsonPropertyName("requestedBy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestedBy { get; init; }

    [JsonPropertyName("grant")]
    public GitRepositoryGrant? Grant { get; init; }

    public static GitRepositoryChange From(Goblin.Core.Work.GitRepositoryChange value) =>
        new(value.GitRepository, value.GitAuthorName, value.GitAuthorEmail,
            value.Grant is null ? null : GitRepositoryGrant.From(value.Grant))
        { RequestedBy = value.RequestedBy };
}

public sealed class GitRepositoryGrant
{
    [JsonConstructor]
    public GitRepositoryGrant()
    {
    }

    [JsonPropertyName("connectionId")]
    public long ConnectionId { get; init; }

    [JsonPropertyName("generation")]
    public string Generation { get; init; } = null!;

    [JsonPropertyName("accountId")]
    public string AccountId { get; init; } = null!;

    [JsonPropertyName("login")]
    public string Login { get; init; } = null!;

    [JsonPropertyName("repositoryId")]
    public long GitRepositoryId { get; init; }

    [JsonPropertyName("baseBranch")]
    public string BaseBranch { get; init; } = null!;

    [JsonPropertyName("branch")]
    public string Branch { get; init; } = null!;

    [JsonPropertyName("policyVersion")]
    public int PolicyVersion { get; init; } = 2;

    [JsonPropertyName("allowPush")]
    public bool AllowPush { get; init; }

    [JsonPropertyName("allowPullRequest")]
    public bool AllowPullRequest { get; init; }

    public static GitRepositoryGrant From(Goblin.Core.Work.GitRepositoryGrant value) =>
        new()
        {
            ConnectionId = value.ConnectionId,
            Generation = value.Generation,
            AccountId = value.AccountId,
            Login = value.Login,
            GitRepositoryId = value.GitRepositoryId,
            BaseBranch = value.BaseBranch,
            Branch = value.Branch,
            PolicyVersion = value.PolicyVersion,
            AllowPush = value.AllowPush,
            AllowPullRequest = value.AllowPullRequest
        };

    public Goblin.Core.Work.GitRepositoryGrant ToCore() =>
        new()
        {
            ConnectionId = ConnectionId,
            Generation = Generation,
            AccountId = AccountId,
            Login = Login,
            GitRepositoryId = GitRepositoryId,
            BaseBranch = BaseBranch,
            Branch = Branch,
            PolicyVersion = PolicyVersion,
            AllowPush = AllowPush,
            AllowPullRequest = AllowPullRequest
        };
}

public sealed class ExecutionSession
{
    [JsonConstructor]
    public ExecutionSession(string? model = null, string? sessionReference = null, string? operationReference = null)
    {
        Model = model;
        SessionReference = sessionReference;
        OperationReference = operationReference;
    }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("sessionReference")]
    public string? SessionReference { get; init; }

    [JsonPropertyName("operationReference")]
    public string? OperationReference { get; init; }

    public static ExecutionSession From(Goblin.Core.Work.ExecutionSession value) =>
        new(value.Model, value.SessionReference, value.OperationReference);
}

public sealed class ExecutionTurnRecord
{
    [JsonConstructor]
    public ExecutionTurnRecord()
    {
    }

    [JsonPropertyName("number")]
    public int Number { get; init; }

    [JsonPropertyName("workspaceNumber")]
    public int WorkspaceNumber { get; init; }

    [JsonPropertyName("ownerId")]
    public long? OwnerId { get; init; }

    [JsonPropertyName("environmentReference")]
    public string? EnvironmentReference { get; init; }

    [JsonPropertyName("session")]
    public ExecutionSession? Session { get; init; }

    [JsonPropertyName("startedAt")]
    public DateTimeOffset? StartedAt { get; init; }

    [JsonPropertyName("finishedAt")]
    public DateTimeOffset? FinishedAt { get; init; }

    [JsonPropertyName("checkpointId")]
    public long? CheckpointId { get; init; }

    public static ExecutionTurnRecord From(Goblin.Core.Work.ExecutionTurnRecord value) =>
        new()
        {
            Number = value.Number,
            WorkspaceNumber = value.WorkspaceNumber,
            OwnerId = value.OwnerId,
            EnvironmentReference = value.EnvironmentReference,
            Session = value.Session is null ? null : ExecutionSession.From(value.Session),
            StartedAt = value.StartedAt,
            FinishedAt = value.FinishedAt,
            CheckpointId = value.CheckpointId
        };
}

public sealed class GitDeliveryIntent
{
    [JsonConstructor]
    public GitDeliveryIntent(string? baseBranch = null, bool push = false, bool openPullRequest = false)
    {
        BaseBranch = baseBranch;
        Push = push;
        OpenPullRequest = openPullRequest;
    }

    [JsonPropertyName("baseBranch")]
    public string? BaseBranch { get; init; }

    [JsonPropertyName("push")]
    public bool Push { get; init; }

    [JsonPropertyName("openPullRequest")]
    public bool OpenPullRequest { get; init; }

    public Goblin.Core.Work.GitDeliveryIntent ToCore() =>
        new(BaseBranch, Push, OpenPullRequest);
}
