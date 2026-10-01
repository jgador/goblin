using System;
using System.Text.Json.Serialization;
using Goblin.Contracts.Conversations;

namespace Goblin.Web.Http.Contracts;

public sealed class SlackConnectionView
{
    [JsonConstructor]
    public SlackConnectionView(SlackConnectionStatus status, string? workspace, string? workspaceId, string? appId, string? botUserId, string? notice)
    {
        Status = status;
        Workspace = workspace;
        WorkspaceId = workspaceId;
        AppId = appId;
        BotUserId = botUserId;
        Notice = notice;
    }

    [JsonPropertyName("status")]
    public SlackConnectionStatus Status { get; init; }

    [JsonPropertyName("workspace")]
    public string? Workspace { get; init; }

    [JsonPropertyName("workspaceId")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("appId")]
    public string? AppId { get; init; }

    [JsonPropertyName("botUserId")]
    public string? BotUserId { get; init; }

    [JsonPropertyName("notice")]
    public string? Notice { get; init; }

    public static SlackConnectionView From(Goblin.Contracts.Conversations.SlackConnectionView value) =>
        new(value.Status, value.Workspace, value.WorkspaceId, value.AppId, value.BotUserId, value.Notice);
}

public sealed class SlackSetupView
{
    [JsonConstructor]
    public SlackSetupView(SlackSetupStatus status, string? command, DateTimeOffset? expiresAt, string? appId, string? notice)
    {
        Status = status;
        Command = command;
        ExpiresAt = expiresAt;
        AppId = appId;
        Notice = notice;
    }

    [JsonPropertyName("status")]
    public SlackSetupStatus Status { get; init; }

    [JsonPropertyName("command")]
    public string? Command { get; init; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; init; }

    [JsonPropertyName("appId")]
    public string? AppId { get; init; }

    [JsonPropertyName("notice")]
    public string? Notice { get; init; }

    public static SlackSetupView From(Goblin.Contracts.Conversations.SlackSetupView value) =>
        new(value.Status, value.Command, value.ExpiresAt, value.AppId, value.Notice);
}

public sealed class SlackIdentityView
{
    [JsonConstructor]
    public SlackIdentityView(long id, string userId)
    {
        Id = id;
        UserId = userId;
    }

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("userId")]
    public string UserId { get; init; }

    public static SlackIdentityView From(Goblin.Application.Work.ExternalIdentityView value) =>
        new(value.Id, value.UserId);
}

public sealed class SlackLinkView
{
    [JsonConstructor]
    public SlackLinkView(long id, string? userId, DateTime expiresAt)
    {
        Id = id;
        UserId = userId;
        ExpiresAt = expiresAt;
    }

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("userId")]
    public string? UserId { get; init; }

    [JsonPropertyName("expiresAt")]
    public DateTime ExpiresAt { get; init; }

    public static SlackLinkView From(Goblin.Application.Work.ExternalLinkView value) =>
        new(value.Id, value.UserId, value.ExpiresAt);
}

public sealed class SlackLinkCode
{
    [JsonConstructor]
    public SlackLinkCode(long id, string code, DateTime expiresAt)
    {
        Id = id;
        Code = code;
        ExpiresAt = expiresAt;
    }

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("code")]
    public string Code { get; init; }

    [JsonPropertyName("expiresAt")]
    public DateTime ExpiresAt { get; init; }

    public static SlackLinkCode From(Goblin.Application.Work.ExternalLinkCode value) =>
        new(value.Id, value.Code, value.ExpiresAt);
}

public sealed class SlackState
{
    [JsonConstructor]
    public SlackState(bool available, SlackConnectionView? connection, SlackSetupView? setup, SlackIdentityView[] identities, SlackLinkView? link)
    {
        Available = available;
        Connection = connection;
        Setup = setup;
        Identities = identities;
        Link = link;
    }

    [JsonPropertyName("available")]
    public bool Available { get; init; }

    [JsonPropertyName("connection")]
    public SlackConnectionView? Connection { get; init; }

    [JsonPropertyName("setup")]
    public SlackSetupView? Setup { get; init; }

    [JsonPropertyName("identities")]
    public SlackIdentityView[] Identities { get; init; }

    [JsonPropertyName("link")]
    public SlackLinkView? Link { get; init; }
}

public sealed class ConversationMessageSource
{
    [JsonConstructor]
    public ConversationMessageSource(string provider, string workspaceId, string userId, string channelId, string threadId, string messageId)
    {
        Provider = provider;
        WorkspaceId = workspaceId;
        UserId = userId;
        ChannelId = channelId;
        ThreadId = threadId;
        MessageId = messageId;
    }

    [JsonPropertyName("provider")]
    public string Provider { get; init; }

    [JsonPropertyName("workspaceId")]
    public string WorkspaceId { get; init; }

    [JsonPropertyName("userId")]
    public string UserId { get; init; }

    [JsonPropertyName("channelId")]
    public string ChannelId { get; init; }

    [JsonPropertyName("threadId")]
    public string ThreadId { get; init; }

    [JsonPropertyName("messageId")]
    public string MessageId { get; init; }

    public static ConversationMessageSource From(Goblin.Application.Work.ConversationMessageSource value) =>
        new(value.Provider, value.WorkspaceId, value.UserId, value.ChannelId, value.ThreadId, value.MessageId);
}
