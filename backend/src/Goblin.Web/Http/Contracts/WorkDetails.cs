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
    public WorkEvent(long sequence, DateTimeOffset occurredAt, WorkEventKind kind, long? attemptId = null,
        long? agentId = null, long? decisionId = null, FailureKind? failure = null, string? text = null)
    {
        Sequence = sequence;
        OccurredAt = occurredAt;
        Kind = kind;
        AttemptId = attemptId;
        AgentId = agentId;
        DecisionId = decisionId;
        Failure = failure;
        Text = text;
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
        new(value.Sequence, value.OccurredAt, value.Kind, value.AttemptId, value.AgentId, value.DecisionId,
            value.Failure, value.Text);
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
        RepositoryChange? repository = null, string? requestedEffort = null)
    {
        Runtime = runtime;
        ConnectionId = connectionId;
        RequestedModel = requestedModel;
        Repository = repository;
        RequestedEffort = requestedEffort;
    }

    [JsonPropertyName("runtime")]
    public string Runtime { get; init; }

    [JsonPropertyName("connectionId")]
    public long ConnectionId { get; init; }

    [JsonPropertyName("requestedModel")]
    public string? RequestedModel { get; init; }

    [JsonPropertyName("repository")]
    public RepositoryChange? Repository { get; init; }

    [JsonPropertyName("requestedEffort")]
    public string? RequestedEffort { get; init; }

    public static ExecutionTarget From(Goblin.Core.Work.ExecutionTarget value) =>
        new(value.Runtime, value.ConnectionId, value.RequestedModel,
            value.Repository is null ? null : RepositoryChange.From(value.Repository), value.RequestedEffort);
}

public sealed class RepositoryChange
{
    [JsonConstructor]
    public RepositoryChange(string repository, string gitAuthorName, string gitAuthorEmail,
        RepositoryGrant? grant = null)
    {
        Repository = repository;
        GitAuthorName = gitAuthorName;
        GitAuthorEmail = gitAuthorEmail;
        Grant = grant;
    }

    [JsonPropertyName("repository")]
    public string Repository { get; init; }

    [JsonPropertyName("gitAuthorName")]
    public string GitAuthorName { get; init; }

    [JsonPropertyName("gitAuthorEmail")]
    public string GitAuthorEmail { get; init; }

    [JsonPropertyName("grant")]
    public RepositoryGrant? Grant { get; init; }

    public static RepositoryChange From(Goblin.Core.Work.RepositoryChange value) =>
        new(value.Repository, value.GitAuthorName, value.GitAuthorEmail,
            value.Grant is null ? null : RepositoryGrant.From(value.Grant));

    public Goblin.Core.Work.RepositoryChange ToCore() =>
        new(Repository, GitAuthorName, GitAuthorEmail, Grant?.ToCore());
}

public sealed class RepositoryGrant
{
    [JsonConstructor]
    public RepositoryGrant(long connectionId, string generation, string accountId, string login, long repositoryId,
        string baseBranch, string branch, int policyVersion = 2, bool allowPush = false, bool allowPullRequest = false)
    {
        ConnectionId = connectionId;
        Generation = generation;
        AccountId = accountId;
        Login = login;
        RepositoryId = repositoryId;
        BaseBranch = baseBranch;
        Branch = branch;
        PolicyVersion = policyVersion;
        AllowPush = allowPush;
        AllowPullRequest = allowPullRequest;
    }

    [JsonPropertyName("connectionId")]
    public long ConnectionId { get; init; }

    [JsonPropertyName("generation")]
    public string Generation { get; init; }

    [JsonPropertyName("accountId")]
    public string AccountId { get; init; }

    [JsonPropertyName("login")]
    public string Login { get; init; }

    [JsonPropertyName("repositoryId")]
    public long RepositoryId { get; init; }

    [JsonPropertyName("baseBranch")]
    public string BaseBranch { get; init; }

    [JsonPropertyName("branch")]
    public string Branch { get; init; }

    [JsonPropertyName("policyVersion")]
    public int PolicyVersion { get; init; }

    [JsonPropertyName("allowPush")]
    public bool AllowPush { get; init; }

    [JsonPropertyName("allowPullRequest")]
    public bool AllowPullRequest { get; init; }

    public static RepositoryGrant From(Goblin.Core.Work.RepositoryGrant value) =>
        new(value.ConnectionId, value.Generation, value.AccountId, value.Login, value.RepositoryId, value.BaseBranch,
            value.Branch, value.PolicyVersion, value.AllowPush, value.AllowPullRequest);

    public Goblin.Core.Work.RepositoryGrant ToCore() =>
        new(ConnectionId, Generation, AccountId, Login, RepositoryId, BaseBranch, Branch, PolicyVersion, AllowPush,
            AllowPullRequest);
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
    public ExecutionTurnRecord(int number, int workspaceNumber, long? ownerId, string? environmentReference,
        ExecutionSession? session, DateTimeOffset? startedAt, DateTimeOffset? finishedAt, long? checkpointId)
    {
        Number = number;
        WorkspaceNumber = workspaceNumber;
        OwnerId = ownerId;
        EnvironmentReference = environmentReference;
        Session = session;
        StartedAt = startedAt;
        FinishedAt = finishedAt;
        CheckpointId = checkpointId;
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
        new(value.Number, value.WorkspaceNumber, value.OwnerId, value.EnvironmentReference,
            value.Session is null ? null : ExecutionSession.From(value.Session), value.StartedAt, value.FinishedAt,
            value.CheckpointId);
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
