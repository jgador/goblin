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
