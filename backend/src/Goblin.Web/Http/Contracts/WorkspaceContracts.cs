using System;
using System.Text.Json.Serialization;
using Goblin.Core.Work;

namespace Goblin.Web.Http.Contracts;

public sealed class WorkspaceView
{
    [JsonConstructor]
    public WorkspaceView(WorkspaceCheckpoint[] checkpoints, InspectionView[] sessions, bool terminalAvailable)
    {
        Checkpoints = checkpoints;
        Sessions = sessions;
        TerminalAvailable = terminalAvailable;
    }

    [JsonPropertyName("checkpoints")]
    public WorkspaceCheckpoint[] Checkpoints { get; init; }

    [JsonPropertyName("sessions")]
    public InspectionView[] Sessions { get; init; }

    [JsonPropertyName("terminalAvailable")]
    public bool TerminalAvailable { get; init; }
}

public sealed class InspectionView
{
    [JsonConstructor]
    public InspectionView(long id, long workId, long attemptId, InspectionState state)
    {
        Id = id;
        WorkId = workId;
        AttemptId = attemptId;
        State = state;
    }

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("workId")]
    public long WorkId { get; init; }

    [JsonPropertyName("attemptId")]
    public long AttemptId { get; init; }

    [JsonPropertyName("state")]
    public InspectionState State { get; init; }

    public static InspectionView From(Goblin.Application.Workspaces.InspectionView value) =>
        new(value.Id, value.WorkId, value.AttemptId, value.State);
}

public sealed class WorkspaceCheckpoint
{
    [JsonConstructor]
    public WorkspaceCheckpoint(long id, long workId, long attemptId, int turnNumber, int workspaceNumber,
        string repository, string branch, string commitSha, DateTimeOffset createdAt)
    {
        Id = id;
        WorkId = workId;
        AttemptId = attemptId;
        TurnNumber = turnNumber;
        WorkspaceNumber = workspaceNumber;
        Repository = repository;
        Branch = branch;
        CommitSha = commitSha;
        CreatedAt = createdAt;
    }

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("workId")]
    public long WorkId { get; init; }

    [JsonPropertyName("attemptId")]
    public long AttemptId { get; init; }

    [JsonPropertyName("turnNumber")]
    public int TurnNumber { get; init; }

    [JsonPropertyName("workspaceNumber")]
    public int WorkspaceNumber { get; init; }

    [JsonPropertyName("repository")]
    public string Repository { get; init; }

    [JsonPropertyName("branch")]
    public string Branch { get; init; }

    [JsonPropertyName("commitSha")]
    public string CommitSha { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    public static WorkspaceCheckpoint From(Goblin.Contracts.Runtime.WorkspaceCheckpoint value) =>
        new(value.Id, value.WorkId, value.AttemptId, value.TurnNumber, value.WorkspaceNumber, value.Repository,
            value.Branch, value.CommitSha, value.CreatedAt);
}
