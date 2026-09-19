using System;
using Goblin.Core.Work;

namespace Goblin.Application.Work;

public enum WorkAction { Create, Assign, Execute, Retry, Cancel, Answer, RequestChanges, Approve, AddContext, Reconcile }
public sealed record WorkCommand(Guid CommandId, Guid WorkId, WorkAction Action,
    long? ExpectedVersion = null, string? Text = null, Guid? AgentId = null,
    Guid? AttemptId = null, Guid? DecisionId = null, RepositoryChange? Repository = null);
public sealed record WorkView(long Version, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, WorkSnapshot Work);
public sealed record AgentView(Guid Id, string Name, Guid ConnectionId, string? Model);
public sealed record ConnectionView(Guid Id, string Runtime, string Name, string Availability);
public sealed record DispatchWork(Guid WorkId, Guid AttemptId);
public sealed record ReconcileWork(Guid WorkId, Guid AttemptId);
public sealed class ApplicationFailure(string code) : Exception(code)
{
    public string Code { get; } = code;
}
