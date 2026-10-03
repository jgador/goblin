using System;
using System.Text.Json.Serialization;
using Goblin.Application.Work;
using Goblin.Contracts;

namespace Goblin.Web.Http.Contracts;

public sealed class WorkCommand
{
    [JsonConstructor]
    public WorkCommand(long commandId, long workId, WorkAction action)
    {
        CommandId = commandId;
        WorkId = workId;
        Action = action;
    }

    [JsonPropertyName("commandId")]
    public long CommandId { get; init; }

    [JsonPropertyName("workId")]
    public long WorkId { get; init; }

    [JsonPropertyName("action")]
    public WorkAction Action { get; init; }

    [JsonPropertyName("expectedVersion")]
    public long? ExpectedVersion { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("agentId")]
    public long? AgentId { get; init; }

    [JsonPropertyName("attemptId")]
    public long? AttemptId { get; init; }

    [JsonPropertyName("decisionId")]
    public long? DecisionId { get; init; }

    [JsonPropertyName("repository")]
    public GitRepositoryChange? GitRepository { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("reasoningEffort")]
    public string? ReasoningEffort { get; init; }

    [JsonPropertyName("modelSelectionProvided")]
    public bool ModelSelectionProvided { get; init; }

    [JsonPropertyName("delivery")]
    public GitDeliveryIntent? Delivery { get; init; }

    [JsonPropertyName("authorizationId")]
    public long? AuthorizationId { get; init; }

    public Goblin.Application.Work.WorkCommand ToApplication() =>
        new(CommandId, WorkId, Action)
        {
            ExpectedVersion = ExpectedVersion,
            Text = Text,
            AgentId = AgentId,
            AttemptId = AttemptId,
            DecisionId = DecisionId,
            GitRepository = GitRepository?.ToCore(),
            Model = Model,
            ReasoningEffort = ReasoningEffort,
            ModelSelectionProvided = ModelSelectionProvided,
            Delivery = Delivery?.ToCore(),
            AuthorizationId = AuthorizationId
        };
}

public sealed class WorkView
{
    [JsonConstructor]
    public WorkView(long version, DateTimeOffset createdAt, DateTimeOffset updatedAt, WorkSnapshot work)
    {
        Version = version;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        Work = work;
    }

    [JsonPropertyName("version")]
    public long Version { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }

    [JsonPropertyName("work")]
    public WorkSnapshot Work { get; init; }

    public static WorkView From(Goblin.Application.Work.WorkView value) =>
        new(value.Version, value.CreatedAt, value.UpdatedAt, WorkSnapshot.From(value.Work));
}

public sealed class AgentView
{
    [JsonConstructor]
    public AgentView(long id, string name, long connectionId, string? model, bool isDefault)
    {
        Id = id;
        Name = name;
        ConnectionId = connectionId;
        Model = model;
        IsDefault = isDefault;
    }

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; }

    [JsonPropertyName("connectionId")]
    public long ConnectionId { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; init; }

    public static AgentView From(Goblin.Application.Work.AgentView value) =>
        new(value.Id, value.Name, value.ConnectionId, value.Model, value.IsDefault);
}

public sealed class ConnectionView
{
    [JsonConstructor]
    public ConnectionView(long id, string runtime, string name, ConnectionAvailability availability)
    {
        Id = id;
        Runtime = runtime;
        Name = name;
        Availability = availability;
    }

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("runtime")]
    public string Runtime { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; }

    [JsonPropertyName("availability")]
    public ConnectionAvailability Availability { get; init; }

    public static ConnectionView From(Goblin.Application.Work.ConnectionView value) =>
        new(value.Id, value.Runtime, value.Name, value.Availability);
}
