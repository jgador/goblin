using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Core.Work;
using Goblin.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Goblin.Application.Work;

public sealed class ConversationMessageSource
{
    public ConversationMessageSource(string provider, string workspaceId, string userId, string channelId,
        string threadId, string messageId)
    {
        Provider = provider;
        WorkspaceId = workspaceId;
        UserId = userId;
        ChannelId = channelId;
        ThreadId = threadId;
        MessageId = messageId;
    }

    public string Provider { get; init; }

    public string WorkspaceId { get; init; }

    public string UserId { get; init; }

    public string ChannelId { get; init; }

    public string ThreadId { get; init; }

    public string MessageId { get; init; }
}

public sealed class ConversationMessageView
{
    public ConversationMessageView(long id, string text, DateTime createdAt,
        ConversationMessageSource? source = null)
    {
        Id = id;
        Text = text;
        CreatedAt = createdAt;
        Source = source;
    }

    public long Id { get; init; }

    public string Text { get; init; }

    public DateTime CreatedAt { get; init; }

    public ConversationMessageSource? Source { get; init; }
}

public sealed class ConversationView
{
    public ConversationView(long id, string title, long? workId, ConversationMessageView[] messages)
    {
        Id = id;
        Title = title;
        WorkId = workId;
        Messages = messages;
    }

    public long Id { get; init; }

    public string Title { get; init; }

    public long? WorkId { get; init; }

    public ConversationMessageView[] Messages { get; init; }
}

public sealed class ConversationCommand
{
    public ConversationCommand(long conversationId, long messageId, string? text, long? workId = null)
    {
        ConversationId = conversationId;
        MessageId = messageId;
        Text = text;
        WorkId = workId;
    }

    public long ConversationId { get; init; }

    public long MessageId { get; init; }

    public string? Text { get; init; }

    public long? WorkId { get; init; }
}

public sealed class ConversationStore
{
    private readonly IDbContextFactory<GoblinDbContext> _dbFactory;

    public ConversationStore(IDbContextFactory<GoblinDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<ConversationView[]> ListAsync(CancellationToken token = default)
    {
        await using GoblinDbContext db = await _dbFactory.CreateDbContextAsync(token);
        return await ListAsync(db, token);
    }

    private static async Task<ConversationView[]> ListAsync(GoblinDbContext db, CancellationToken token)
    {
        Persistence.Entities.Conversation[] conversations = await db.Conversations.AsNoTracking().OrderByDescending(x => x.CreatedAt).ToArrayAsync(token);
        Persistence.Entities.ConversationMessage[] messages = await db.ConversationMessages.AsNoTracking().OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToArrayAsync(token);
        Persistence.Entities.ExternalMessage[] sources = await db.ExternalMessages.AsNoTracking().Where(x => x.ConversationMessageId != null).ToArrayAsync(token);
        return [.. conversations.Select(c => new ConversationView(c.Id, c.Title, c.WorkId,
            [.. messages.Where(m => m.ConversationId == c.Id).Select(m => new ConversationMessageView(m.Id, m.Body, m.CreatedAt,
                sources.FirstOrDefault(s => s.ConversationMessageId == m.Id) is { } source
                    ? new("Slack", source.WorkspaceId, source.UserId, source.ChannelId, source.ThreadId, source.MessageId) : null))]))];
    }

    public async Task<ConversationView> ApplyAsync(ConversationCommand command)
    {
        if (command.ConversationId <= 0 || command.MessageId <= 0 || command.Text?.Length > 4000)
            throw new ApplicationFailure("invalid_command");
        await using GoblinDbContext db = await _dbFactory.CreateDbContextAsync();
        await using IDbContextTransaction transaction = await ApplicationTransaction.BeginAsync(db, default);
        Persistence.Entities.Conversation? conversation = await db.Conversations.SingleOrDefaultAsync(x => x.Id == command.ConversationId);
        if (conversation is null)
        {
            if (string.IsNullOrWhiteSpace(command.Text)) throw new ApplicationFailure("invalid_command");
            conversation = new() { Id = command.ConversationId, Title = command.Text[..Math.Min(command.Text.Length, 80)], CreatedAt = DateTime.UtcNow };
            db.Conversations.Add(conversation);
        }
        Persistence.Entities.ConversationMessage? existing = await db.ConversationMessages.SingleOrDefaultAsync(x => x.Id == command.MessageId);
        if (existing is not null && (existing.ConversationId != conversation.Id || existing.Body != command.Text))
            throw new ApplicationFailure("command_id_reused");
        if (existing is null && !string.IsNullOrWhiteSpace(command.Text))
            db.ConversationMessages.Add(new() { Id = command.MessageId, ConversationId = conversation.Id, Body = command.Text, CreatedAt = DateTime.UtcNow });

        if (command.WorkId is { } workId && conversation.WorkId is null)
        {
            if (workId <= 0 || await db.WorkItems.AnyAsync(x => x.Id == workId)) throw new ApplicationFailure("work_already_exists");
            await db.SaveChangesAsync();
            Persistence.Entities.ConversationMessage[] messages = await db.ConversationMessages.Where(x => x.ConversationId == conversation.Id)
                .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToArrayAsync();
            var work = new WorkItem(workId, messages[0].Body, DateTimeOffset.UtcNow);
            work.Assign(WorkStore.DefaultAgentId, DateTimeOffset.UtcNow);
            foreach (Persistence.Entities.ConversationMessage? message in messages.Skip(1))
                work.AddContext(await IdentitySequence.NextAsync(db, IdentityKind.Event), message.Body, message.CreatedAt);
            db.WorkItems.Add(WorkStatePersistence.Create(work, DateTimeOffset.UtcNow));
            conversation.WorkId = workId;
        }
        else if (conversation.WorkId is { } linked && existing is null && !string.IsNullOrWhiteSpace(command.Text))
        {
            Persistence.Entities.WorkItem row = await db.WorkItems.SingleAsync(x => x.Id == linked);
            WorkItem work = WorkStatePersistence.Restore(row);
            work.AddContext(await IdentitySequence.NextAsync(db, IdentityKind.Event), command.Text, DateTimeOffset.UtcNow);
            WorkStatePersistence.Update(row, work, DateTimeOffset.UtcNow);
        }
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return (await ListAsync(db, default)).Single(x => x.Id == conversation.Id);
    }
}
