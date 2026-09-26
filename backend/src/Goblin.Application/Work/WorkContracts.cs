using System;
using Goblin.Core.Work;

namespace Goblin.Application.Work;

public enum WorkAction { Create, Assign, Execute, Retry, Cancel, Answer, RequestChanges, Approve, AddContext, Reconcile, PrepareRepository, AuthorizeRepository, DenyRepository }
public sealed record WorkCommand(long CommandId, long WorkId, WorkAction Action,
    long? ExpectedVersion = null, string? Text = null, long? AgentId = null,
    long? AttemptId = null, long? DecisionId = null, RepositoryChange? Repository = null,
    string? Model = null, string? ReasoningEffort = null, bool ModelSelectionProvided = false,
    GitDeliveryIntent? Delivery = null, long? AuthorizationId = null);
public sealed record WorkView(long Version, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, WorkSnapshot Work);
public sealed record AgentView(long Id, string Name, long ConnectionId, string? Model);
public sealed record ConnectionView(long Id, string Runtime, string Name, string Availability);
public sealed record DispatchWork(long WorkId, long AttemptId, int TurnNumber = 1);
public sealed record ReconcileWork(long WorkId, long AttemptId, int TurnNumber = 0);
public sealed class ApplicationFailure : Exception
{
    public ApplicationFailure(string code) : base(code) => Code = code;

    public string Code { get; }
}
