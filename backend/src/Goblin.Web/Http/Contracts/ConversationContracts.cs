using System;
using System.Text.Json.Serialization;

namespace Goblin.Web.Http.Contracts;

public sealed class ConversationCommand
{
    [JsonConstructor]
    public ConversationCommand(long conversationId, long messageId, string? text, long? workId = null)
    {
        ConversationId = conversationId;
        MessageId = messageId;
        Text = text;
        WorkId = workId;
    }

    [JsonPropertyName("conversationId")]
    public long ConversationId { get; init; }

    [JsonPropertyName("messageId")]
    public long MessageId { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("workId")]
    public long? WorkId { get; init; }

    public Goblin.Application.Work.ConversationCommand ToApplication() =>
        new(ConversationId, MessageId, Text, WorkId);
}

public sealed class ConversationView
{
    [JsonConstructor]
    public ConversationView(long id, string title, long? workId, ConversationMessageView[] messages)
    {
        Id = id;
        Title = title;
        WorkId = workId;
        Messages = messages;
    }

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; }

    [JsonPropertyName("workId")]
    public long? WorkId { get; init; }

    [JsonPropertyName("messages")]
    public ConversationMessageView[] Messages { get; init; }

    public static ConversationView From(Goblin.Application.Work.ConversationView value) =>
        new(value.Id, value.Title, value.WorkId, Array.ConvertAll(value.Messages, ConversationMessageView.From));
}

public sealed class ConversationMessageView
{
    [JsonConstructor]
    public ConversationMessageView(long id, string text, DateTime createdAt, ConversationMessageSource? source = null)
    {
        Id = id;
        Text = text;
        CreatedAt = createdAt;
        Source = source;
    }

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("text")]
    public string Text { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; init; }

    [JsonPropertyName("source")]
    public ConversationMessageSource? Source { get; init; }

    public static ConversationMessageView From(Goblin.Application.Work.ConversationMessageView value) =>
        new(value.Id, value.Text, value.CreatedAt, value.Source is null ? null : ConversationMessageSource.From(value.Source));
}
