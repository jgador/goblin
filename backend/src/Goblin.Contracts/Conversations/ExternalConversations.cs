using System;
using System.Threading;
using System.Threading.Tasks;

namespace Goblin.Contracts.Conversations;

// Transport identities are evidence, never local permission by themselves.
public sealed record ExternalInstallation(string Id, string WorkspaceId, string AppId, string BotUserId);

public sealed record ExternalMessage(ExternalInstallation Installation, string EventId, string UserId,
    string ChannelId, string ThreadId, string MessageId, string Text, bool Direct);

public sealed record ExternalReply(long Id, string ChannelId, string ThreadId, long? WorkId, ExternalReplyKind Kind);

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
