using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Goblin.Application.Work;
using Goblin.Contracts;
using Goblin.Core.Work;
using Api = Goblin.Web.Http.Contracts;

namespace Goblin.Web;

internal sealed class ChatGptWorkPage
{
    public required ChatGptWorkOverview[] Items { get; init; }
    public string? NextAfterWorkId { get; init; }
}

internal sealed class ChatGptWorkOverview
{
    public required string WorkId { get; init; }
    public required long Version { get; init; }
    public required string Objective { get; init; }
    public required WorkStatus Status { get; init; }
    public string? AgentId { get; init; }
    public Api.WorkAttention? Attention { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public string? CurrentAttemptId { get; init; }
    public ChatGptRepositoryRequest? RepositoryRequest { get; init; }
    public ChatGptRepositoryAuthorization? RepositoryAuthorization { get; init; }

    public static ChatGptWorkOverview From(WorkView view)
    {
        WorkSnapshot work = view.Work;
        return new()
        {
            WorkId = Id(work.Id),
            Version = view.Version,
            Objective = work.Objective,
            Status = work.Status,
            AgentId = work.AgentId is long agent ? Id(agent) : null,
            Attention = work.Attention is null ? null : Api.WorkAttention.From(work.Attention),
            CreatedAt = view.CreatedAt,
            UpdatedAt = view.UpdatedAt,
            CurrentAttemptId = work.Attempts.LastOrDefault() is { } attempt ? Id(attempt.Id) : null,
            RepositoryRequest = work.GitRepositoryRequest is { } request ? new()
            {
                Repositories = request.GitRepositories,
                RequestedModel = request.Target.RequestedModel,
                RequestedEffort = request.Target.RequestedEffort
            } : null,
            RepositoryAuthorization = work.GitRepositoryAuthorization is { } authorization ? new()
            {
                AuthorizationId = Id(authorization.Id),
                Repository = authorization.Target.GitRepository?.GitRepository,
                GitAuthorName = authorization.Target.GitRepository?.GitAuthorName,
                GitAuthorEmail = authorization.Target.GitRepository?.GitAuthorEmail,
                BaseBranch = authorization.Target.GitRepository?.Grant?.BaseBranch,
                Branch = authorization.Target.GitRepository?.Grant?.Branch,
                Push = authorization.Target.GitRepository?.Grant?.AllowPush ?? false,
                OpenPullRequest = authorization.Target.GitRepository?.Grant?.AllowPullRequest ?? false,
                EnableRepository = authorization.EnableGitRepository,
                Retry = authorization.Retry,
                Status = authorization.Status,
                RequestedAt = authorization.RequestedAt,
                DefaultBranch = authorization.DefaultBranch
            } : null
        };
    }

    internal static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
}

internal sealed class ChatGptRepositoryRequest
{
    public required string[] Repositories { get; init; }
    public string? RequestedModel { get; init; }
    public string? RequestedEffort { get; init; }
}

internal sealed class ChatGptRepositoryAuthorization
{
    public required string AuthorizationId { get; init; }
    public string? Repository { get; init; }
    public string? GitAuthorName { get; init; }
    public string? GitAuthorEmail { get; init; }
    public string? BaseBranch { get; init; }
    public string? Branch { get; init; }
    public required bool Push { get; init; }
    public required bool OpenPullRequest { get; init; }
    public required bool EnableRepository { get; init; }
    public required bool Retry { get; init; }
    public required GitRepositoryAuthorizationStatus Status { get; init; }
    public required DateTimeOffset RequestedAt { get; init; }
    public string? DefaultBranch { get; init; }
}

internal enum ChatGptWorkSection { History, Attempts, Decisions, Messages, Results, Artifacts }

internal sealed class ChatGptWorkSectionPage
{
    public required string WorkId { get; init; }
    public required long Version { get; init; }
    public required ChatGptWorkSection Section { get; init; }
    public required int Offset { get; init; }
    public required string ContentJson { get; init; }
    public int? NextOffset { get; init; }
}

internal sealed class ChatGptWorkResultPage
{
    public required string WorkId { get; init; }
    public required string AttemptId { get; init; }
    public required long Version { get; init; }
    public required int Offset { get; init; }
    public required string Text { get; init; }
    public int? NextOffset { get; init; }
    public required DateTimeOffset ProposedAt { get; init; }
    public DateTimeOffset? ApprovedAt { get; init; }
    public string? RequestedChanges { get; init; }
}

internal sealed class ChatGptCommandReply
{
    public required string CommandId { get; init; }
    public required ChatGptWorkOverview Work { get; init; }
}

internal sealed class ChatGptReservedIds
{
    public required string CommandId { get; init; }
    public string? WorkId { get; init; }
}

internal sealed class ChatGptAgentPage
{
    public required ChatGptAgent[] Items { get; init; }
    public int? NextOffset { get; init; }
}

internal sealed class ChatGptAgent
{
    public required string AgentId { get; init; }
    public required string Name { get; init; }
    public string? Model { get; init; }
    public required bool IsDefault { get; init; }
}

internal sealed class ChatGptRepositoryPage
{
    public required ChatGptRepository[] Items { get; init; }
    public int? NextOffset { get; init; }
}

internal sealed class ChatGptRepository
{
    public required string Name { get; init; }
    public required string DefaultBranch { get; init; }
    public required bool Enabled { get; init; }
}

internal sealed class ChatGptAvailableRepositoryPage
{
    public required ChatGptAvailableRepository[] Items { get; init; }
    public int? NextPage { get; init; }
}

internal sealed class ChatGptAvailableRepository
{
    public required string Name { get; init; }
    public required string DefaultBranch { get; init; }
    public required bool CanPush { get; init; }
}

internal sealed class ChatGptRepositorySuggestions
{
    public required string WorkId { get; init; }
    public required string[] Repositories { get; init; }
}

internal static class ChatGptWorkProjection
{
    private static readonly JsonSerializerOptions Json = new(ContractJson.Options) { Converters = { new LongJsonConverter() } };
    private const int ChunkSize = 6000;

    public static ChatGptWorkSectionPage Section(WorkView view, ChatGptWorkSection section, int offset)
    {
        WorkSnapshot work = view.Work;
        object items = section switch
        {
            ChatGptWorkSection.History => Array.ConvertAll(work.History, Api.WorkEvent.From),
            ChatGptWorkSection.Attempts => Array.ConvertAll(work.Attempts, ChatGptAttempt.From),
            ChatGptWorkSection.Decisions => Array.ConvertAll(work.Decisions, Api.WorkDecision.From),
            ChatGptWorkSection.Messages => Array.ConvertAll(work.Messages, Api.WorkMessage.From),
            ChatGptWorkSection.Results => Array.ConvertAll(work.Results, Api.WorkResult.From),
            ChatGptWorkSection.Artifacts => Array.ConvertAll(work.Artifacts, Api.WorkArtifact.From),
            _ => throw new PublicError("invalid_section", "Choose a supported Work section.")
        };
        string content = JsonSerializer.Serialize(items, Json);
        (string chunk, int? next) = Slice(content, offset);
        return new()
        {
            WorkId = ChatGptWorkOverview.Id(work.Id),
            Version = view.Version,
            Section = section,
            Offset = offset,
            ContentJson = chunk,
            NextOffset = next
        };
    }

    public static ChatGptWorkResultPage Result(WorkView view, long attemptId, int offset)
    {
        WorkResult result = view.Work.Results.SingleOrDefault(x => x.AttemptId == attemptId)
            ?? throw new PublicError("result_not_found", "This Work has no result for that attempt.", 404);
        (string chunk, int? next) = Slice(result.Text, offset);
        return new()
        {
            WorkId = ChatGptWorkOverview.Id(view.Work.Id),
            AttemptId = ChatGptWorkOverview.Id(attemptId),
            Version = view.Version,
            Offset = offset,
            Text = chunk,
            NextOffset = next,
            ProposedAt = result.ProposedAt,
            ApprovedAt = result.ApprovedAt,
            RequestedChanges = result.RequestedChanges
        };
    }

    private static (string, int?) Slice(string value, int offset)
    {
        if (offset < 0 || offset > value.Length) throw new PublicError("invalid_offset", "Use the nextOffset returned by the previous page.");
        int length = Math.Min(ChunkSize, value.Length - offset);
        int end = offset + length;
        // Keep a UTF-16 surrogate pair intact when paging plain result text.
        if (end < value.Length && end > offset && char.IsHighSurrogate(value[end - 1])) end--;
        return (value[offset..end], end < value.Length ? end : null);
    }
}

internal sealed class ChatGptAttempt
{
    public required string AttemptId { get; init; }
    public required string AgentId { get; init; }
    public required AttemptStatus Status { get; init; }
    public required DateTimeOffset QueuedAt { get; init; }
    public DateTimeOffset? ClaimedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public DateTimeOffset? CancellationRequestedAt { get; init; }
    public FailureKind? Failure { get; init; }
    public string? RequestedModel { get; init; }
    public string? RequestedEffort { get; init; }
    public string? ActualModel { get; init; }
    public string? Repository { get; init; }
    public required int TurnNumber { get; init; }
    public required int WorkspaceNumber { get; init; }
    public long? CheckpointId { get; init; }
    public required bool ReasoningOnly { get; init; }
    public required bool CleanupPending { get; init; }
    public required bool CleanupFailed { get; init; }
    public required ChatGptTurn[] PriorTurns { get; init; }

    public static ChatGptAttempt From(AttemptSnapshot attempt) => new()
    {
        AttemptId = ChatGptWorkOverview.Id(attempt.Id),
        AgentId = ChatGptWorkOverview.Id(attempt.AgentId),
        Status = attempt.Status,
        QueuedAt = attempt.QueuedAt,
        ClaimedAt = attempt.ClaimedAt,
        StartedAt = attempt.StartedAt,
        FinishedAt = attempt.FinishedAt,
        CancellationRequestedAt = attempt.CancellationRequestedAt,
        Failure = attempt.Failure,
        RequestedModel = attempt.Target.RequestedModel,
        RequestedEffort = attempt.Target.RequestedEffort,
        ActualModel = attempt.Session?.Model,
        Repository = attempt.Target.GitRepository?.GitRepository,
        TurnNumber = attempt.TurnNumber,
        WorkspaceNumber = attempt.WorkspaceNumber,
        CheckpointId = attempt.CheckpointId,
        ReasoningOnly = attempt.ReasoningOnly,
        CleanupPending = attempt.CleanupPending,
        CleanupFailed = attempt.CleanupFailed,
        PriorTurns = [.. attempt.PriorTurns.Select(ChatGptTurn.From)]
    };
}

internal sealed class ChatGptTurn
{
    public required int Number { get; init; }
    public required int WorkspaceNumber { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public long? CheckpointId { get; init; }
    public string? ActualModel { get; init; }

    public static ChatGptTurn From(ExecutionTurnRecord turn) => new()
    {
        Number = turn.Number,
        WorkspaceNumber = turn.WorkspaceNumber,
        StartedAt = turn.StartedAt,
        FinishedAt = turn.FinishedAt,
        CheckpointId = turn.CheckpointId,
        ActualModel = turn.Session?.Model
    };
}
