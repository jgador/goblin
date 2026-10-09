using System;
using System.Threading;
using System.Threading.Tasks;

namespace Goblin.Contracts.Conversations;

// Transport identities are evidence, never local permission by themselves.
public sealed class ExternalInstallation
{
    public ExternalInstallation(string id, string workspaceId, string appId, string botUserId)
    {
        Id = id;
        WorkspaceId = workspaceId;
        AppId = appId;
        BotUserId = botUserId;
    }

    public string Id { get; init; }

    public string WorkspaceId { get; init; }

    public string AppId { get; init; }

    public string BotUserId { get; init; }
}

public sealed class ExternalMessage
{
    public ExternalMessage()
    {
    }

    public ExternalInstallation Installation { get; init; } = null!;
    public string EventId { get; init; } = null!;
    public string UserId { get; init; } = null!;
    public string ChannelId { get; init; } = null!;
    public string ThreadId { get; init; } = null!;
    public string MessageId { get; init; } = null!;
    public string Text { get; init; } = null!;
    public bool Direct { get; init; }
}

public sealed class ExternalReply
{
    public ExternalReply(long id, string channelId, string threadId, long? workId, ExternalReplyKind kind)
    {
        Id = id;
        ChannelId = channelId;
        ThreadId = threadId;
        WorkId = workId;
        Kind = kind;
    }

    public long Id { get; init; }

    public string ChannelId { get; init; }

    public string ThreadId { get; init; }

    public long? WorkId { get; init; }

    public ExternalReplyKind Kind { get; init; }

    public string? Notice { get; init; }
}

public enum ExternalReplyKind
{
    WorkSaved,
    LinkConfirmationRequired,
    AccessRequired,
    CommandRejected
}

public interface IExternalConversations
{
    Task AcceptAsync(ExternalMessage message, CancellationToken token);

    Task<ExternalReply?> ProcessNextAsync(ExternalInstallation installation, CancellationToken token);

    Task<ExternalQuestion?> NextQuestionAsync(ExternalInstallation installation, CancellationToken token);

    Task QuestionSentAsync(ExternalInstallation installation, ExternalQuestion question, CancellationToken token);

    Task<ExternalWorkUpdate[]> PendingUpdatesAsync(ExternalInstallation installation, CancellationToken token);

    Task<bool> UpdateIsCurrentAsync(ExternalInstallation installation, ExternalWorkUpdate update, CancellationToken token);

    Task UpdateSentAsync(ExternalInstallation installation, ExternalWorkUpdate update, CancellationToken token);
}

public enum ExternalWorkUpdateKind
{
    ResultReady,
    Completed,
    Failed,
    Uncertain,
    CleanupFailed,
    Cancelled
}

public sealed record ExternalWorkUpdateContent(long Sequence, ExternalWorkUpdateKind Kind, string Text);

public sealed class ExternalWorkUpdate
{
    public ExternalWorkUpdate(long conversationId, long workId, string channelId, string threadId, ExternalWorkUpdateContent content)
    {
        ConversationId = conversationId;
        WorkId = workId;
        ChannelId = channelId;
        ThreadId = threadId;
        Content = content;
    }

    public long ConversationId { get; }
    public long WorkId { get; }
    public string ChannelId { get; }
    public string ThreadId { get; }
    public ExternalWorkUpdateContent Content { get; }
}

public sealed class ExternalQuestion
{
    public ExternalQuestion(long conversationId, long workId, long decisionId, string channelId, string threadId, string text)
    {
        ConversationId = conversationId;
        WorkId = workId;
        DecisionId = decisionId;
        ChannelId = channelId;
        ThreadId = threadId;
        Text = text;
    }

    public long ConversationId { get; }
    public long WorkId { get; }
    public long DecisionId { get; }
    public string ChannelId { get; }
    public string ThreadId { get; }
    public string Text { get; }
}

public enum SlackConnectionStatus
{
    Disconnected,
    Connecting,
    Connected,
    Unavailable
}

public enum SlackSetupStatus
{
    Idle,
    Preparing,
    AwaitingAuthorization,
    Installing,
    NeedsAttention,
    Complete
}

public sealed record SlackConnectionView(SlackConnectionStatus Status, string? Workspace, string? WorkspaceId,
    string? AppId, string? BotUserId, string? Notice);

public sealed record SlackSetupView(SlackSetupStatus Status, string? Command, DateTimeOffset? ExpiresAt,
    string? AppId, string? Notice);
