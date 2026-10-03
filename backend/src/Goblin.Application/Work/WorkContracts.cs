using System;
using Goblin.Contracts;
using Goblin.Core.Work;

namespace Goblin.Application.Work;

public enum WorkAction
{
    Create,
    Assign,
    Execute,
    Retry,
    Cancel,
    Answer,
    RequestChanges,
    Approve,
    AddContext,
    Reconcile,
    PrepareRepository,
    AuthorizeRepository,
    DenyRepository
}

public sealed class WorkCommand
{
    public WorkCommand(long commandId, long workId, WorkAction action, long? expectedVersion = null,
        string? text = null, long? agentId = null, long? attemptId = null, long? decisionId = null,
        RepositoryChange? repository = null, string? model = null, string? reasoningEffort = null,
        bool modelSelectionProvided = false, GitDeliveryIntent? delivery = null, long? authorizationId = null)
    {
        CommandId = commandId;
        WorkId = workId;
        Action = action;
        ExpectedVersion = expectedVersion;
        Text = text;
        AgentId = agentId;
        AttemptId = attemptId;
        DecisionId = decisionId;
        Repository = repository;
        Model = model;
        ReasoningEffort = reasoningEffort;
        ModelSelectionProvided = modelSelectionProvided;
        Delivery = delivery;
        AuthorizationId = authorizationId;
    }

    public long CommandId { get; init; }

    public long WorkId { get; init; }

    public WorkAction Action { get; init; }

    public long? ExpectedVersion { get; init; }

    public string? Text { get; init; }

    public long? AgentId { get; init; }

    public long? AttemptId { get; init; }

    public long? DecisionId { get; init; }

    public RepositoryChange? Repository { get; init; }

    public string? Model { get; init; }

    public string? ReasoningEffort { get; init; }

    public bool ModelSelectionProvided { get; init; }

    public GitDeliveryIntent? Delivery { get; init; }

    public long? AuthorizationId { get; init; }
}

public sealed class WorkView
{
    public WorkView(long version, DateTimeOffset createdAt, DateTimeOffset updatedAt, WorkSnapshot work)
    {
        Version = version;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        Work = work;
    }

    public long Version { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public WorkSnapshot Work { get; init; }
}

public sealed class AgentView
{
    public AgentView(long id, string name, long connectionId, string? model)
    {
        Id = id;
        Name = name;
        ConnectionId = connectionId;
        Model = model;
    }

    public long Id { get; init; }

    public string Name { get; init; }

    public long ConnectionId { get; init; }

    public string? Model { get; init; }

    public bool IsDefault => Id == WorkStore.DefaultAgentId;
}

public sealed class ConnectionView
{
    public ConnectionView(long id, string runtime, string name, ConnectionAvailability availability)
    {
        Id = id;
        Runtime = runtime;
        Name = name;
        Availability = availability;
    }

    public long Id { get; init; }

    public string Runtime { get; init; }

    public string Name { get; init; }

    public ConnectionAvailability Availability { get; init; }
}

public sealed class DispatchWork
{
    public DispatchWork(long workId, long attemptId, int turnNumber = 1)
    {
        WorkId = workId;
        AttemptId = attemptId;
        TurnNumber = turnNumber;
    }

    public long WorkId { get; init; }

    public long AttemptId { get; init; }

    public int TurnNumber { get; init; }
}

public sealed class ReconcileWork
{
    public ReconcileWork(long workId, long attemptId, int turnNumber = 0)
    {
        WorkId = workId;
        AttemptId = attemptId;
        TurnNumber = turnNumber;
    }

    public long WorkId { get; init; }

    public long AttemptId { get; init; }

    public int TurnNumber { get; init; }
}

public sealed class ApplicationFailure : Exception
{
    public ApplicationFailure(string code) : base(code) => Code = code;

    public string Code { get; }
}
