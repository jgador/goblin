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
    public WorkspaceCheckpoint()
    {
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
    public string GitRepository { get; init; } = null!;

    [JsonPropertyName("branch")]
    public string Branch { get; init; } = null!;

    [JsonPropertyName("commitSha")]
    public string CommitSha { get; init; } = null!;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    public static WorkspaceCheckpoint From(Goblin.Contracts.Runtime.WorkspaceCheckpoint value) =>
        new()
        {
            Id = value.Id,
            WorkId = value.WorkId,
            AttemptId = value.AttemptId,
            TurnNumber = value.TurnNumber,
            WorkspaceNumber = value.WorkspaceNumber,
            GitRepository = value.GitRepository,
            Branch = value.Branch,
            CommitSha = value.CommitSha,
            CreatedAt = value.CreatedAt
        };
}
