using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Core.Work;
using Goblin.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Goblin.Application.Work;

public sealed record ConversationMessageView(Guid Id, string Text, DateTime CreatedAt);
public sealed record ConversationView(Guid Id, string Title, Guid? WorkId, ConversationMessageView[] Messages);
public sealed record ConversationCommand(Guid ConversationId, Guid MessageId, string? Text, Guid? WorkId = null);

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
        return [.. conversations.Select(c => new ConversationView(c.Id, c.Title, c.WorkId,
            [.. messages.Where(m => m.ConversationId == c.Id).Select(m => new ConversationMessageView(m.Id, m.Body, m.CreatedAt))]))];
    }

    public async Task<ConversationView> ApplyAsync(ConversationCommand command)
    {
        if (command.ConversationId == Guid.Empty || command.MessageId == Guid.Empty || command.Text?.Length > 4000)
            throw new ApplicationFailure("invalid_command");
        await using GoblinDbContext db = await _dbFactory.CreateDbContextAsync();
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(716352019)");
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
            if (workId == Guid.Empty || await db.WorkItems.AnyAsync(x => x.Id == workId)) throw new ApplicationFailure("work_already_exists");
            await db.SaveChangesAsync();
            Persistence.Entities.ConversationMessage[] messages = await db.ConversationMessages.Where(x => x.ConversationId == conversation.Id)
                .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToArrayAsync();
            var work = new WorkItem(workId, messages[0].Body, DateTimeOffset.UtcNow);
            foreach (Persistence.Entities.ConversationMessage? message in messages.Skip(1)) work.AddContext(message.Id, message.Body, message.CreatedAt);
            db.WorkItems.Add(new()
            {
                Id = workId,
                Objective = work.Objective,
                Status = work.Status.ToString(),
                Version = 1,
                State = JsonSerializer.Serialize(work.Snapshot(), WorkStore.Json),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            conversation.WorkId = workId;
        }
        else if (conversation.WorkId is { } linked && existing is null && !string.IsNullOrWhiteSpace(command.Text))
        {
            Persistence.Entities.WorkItem row = await db.WorkItems.SingleAsync(x => x.Id == linked);
            var work = WorkItem.Restore(JsonSerializer.Deserialize<WorkSnapshot>(row.State!, WorkStore.Json)!);
            work.AddContext(command.MessageId, command.Text, DateTimeOffset.UtcNow);
            row.State = JsonSerializer.Serialize(work.Snapshot(), WorkStore.Json);
            row.Version++;
            row.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return (await ListAsync(db, default)).Single(x => x.Id == conversation.Id);
    }
}
