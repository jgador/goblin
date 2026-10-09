using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts.Conversations;
using Goblin.Core.Work;
using Goblin.Persistence;
using Goblin.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Goblin.Application.Work;

public sealed partial class ExternalConversationStore
{
    public Task<ExternalWorkUpdate[]> PendingUpdatesAsync(ExternalInstallation installation, CancellationToken token) =>
        PendingUpdatesAsync(installation, null, token);

    public async Task<bool> UpdateIsCurrentAsync(ExternalInstallation installation, ExternalWorkUpdate update, CancellationToken token) =>
        (await PendingUpdatesAsync(installation, update.ConversationId, token)).Any(current => current.WorkId == update.WorkId &&
            current.ChannelId == update.ChannelId && current.ThreadId == update.ThreadId && current.Content == update.Content);

    private async Task<ExternalWorkUpdate[]> PendingUpdatesAsync(ExternalInstallation installation, long? conversationId, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        var sources = await db.ExternalConversations.AsNoTracking()
            .Where(source => source.InstallationId == installation.Id && source.WorkspaceId == installation.WorkspaceId &&
                (conversationId == null || source.Id == conversationId) &&
                source.Conversation.Work != null &&
                (source.Conversation.Work.Status == nameof(WorkStatus.NeedsAttention) ||
                 source.Conversation.Work.Status == nameof(WorkStatus.Completed) ||
                 source.Conversation.Work.Status == nameof(WorkStatus.Cancelled)))
            .OrderBy(source => source.Id)
            .Select(source => new { Source = source, Work = source.Conversation.Work! }).ToArrayAsync(token);
        var updates = new List<ExternalWorkUpdate>();
        foreach (var item in sources)
        {
            ExternalConversation source = item.Source;
            string? user = await db.ExternalMessages.Where(message => message.InstallationId == installation.Id &&
                message.WorkspaceId == installation.WorkspaceId && message.ChannelId == source.ChannelId &&
                message.ThreadId == source.ThreadId && message.WorkId == item.Work.Id && message.State == nameof(ExternalMessageState.Accepted))
                .OrderBy(message => message.Id).Select(message => message.UserId).FirstOrDefaultAsync(token);
            if (user is null || !await AuthorizedAsync(db, installation.Id, installation.WorkspaceId, user, token)) continue;
            ExternalWorkUpdateContent? content = ExternalWorkUpdatePolicy.Pending(WorkStatePersistence.Snapshot(item.Work), source.NotifiedWorkSequence);
            if (content is not null) updates.Add(new(source.Id, item.Work.Id, source.ChannelId, source.ThreadId, content));
        }
        return [.. updates];
    }

    public async Task UpdateSentAsync(ExternalInstallation installation, ExternalWorkUpdate update, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        // A delayed acknowledgement must not move delivery backwards or mark a
        // different Work/thread/installation as notified.
        await db.ExternalConversations.Where(source => source.Id == update.ConversationId && source.InstallationId == installation.Id &&
            source.WorkspaceId == installation.WorkspaceId && source.ChannelId == update.ChannelId && source.ThreadId == update.ThreadId &&
            source.Conversation.WorkId == update.WorkId && (source.NotifiedWorkSequence == null || source.NotifiedWorkSequence < update.Content.Sequence))
            .ExecuteUpdateAsync(setters => setters.SetProperty(source => source.NotifiedWorkSequence, update.Content.Sequence), token);
    }
}
