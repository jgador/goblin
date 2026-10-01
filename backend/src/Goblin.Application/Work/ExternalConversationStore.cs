using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts.Conversations;
using Goblin.Core.Work;
using Goblin.Persistence;
using Goblin.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Wolverine.EntityFrameworkCore;
using ExternalMessage = Goblin.Contracts.Conversations.ExternalMessage;
using ExternalMessageRow = Goblin.Persistence.Entities.ExternalMessage;

namespace Goblin.Application.Work;

internal enum LocalActor
{
    Owner
}

internal enum ExternalMessageState
{
    Pending,
    PendingLink,
    Accepted,
    Rejected
}

public sealed record ExternalLinkView(long Id, string? UserId, DateTime ExpiresAt);

public sealed record ExternalLinkCode(long Id, string Code, DateTime ExpiresAt);

public sealed record ExternalIdentityView(long Id, string UserId);

public sealed class ExternalConversationStore
{
    private readonly IDbContextFactory<GoblinDbContext> _factory;
    private readonly WorkStore _work;
    private readonly WorkOutboxFactory _outboxes;

    public ExternalConversationStore(IDbContextFactory<GoblinDbContext> factory, WorkStore work, WorkOutboxFactory outboxes)
    {
        _factory = factory;
        _work = work;
        _outboxes = outboxes;
    }

    public async Task<ExternalLinkCode> StartLinkAsync(ExternalInstallation installation, string localSession, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        await using IDbContextTransaction transaction = await WorkStore.BeginAsync(db, token);
        await db.ExternalLinkRequests.Where(x => x.SessionId == localSession).ExecuteDeleteAsync(token);
        string code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var link = new ExternalLinkRequest
        {
            InstallationId = installation.Id,
            WorkspaceId = installation.WorkspaceId,
            SessionId = localSession,
            CodeHash = Hash(code),
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        };
        db.ExternalLinkRequests.Add(link);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new(link.Id, code, link.ExpiresAt);
    }

    public async Task<ExternalLinkView?> LinkAsync(ExternalInstallation installation, string session, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        return await db.ExternalLinkRequests.Where(x => x.InstallationId == installation.Id && x.SessionId == session &&
            !x.Consumed && x.ExpiresAt > DateTime.UtcNow).Select(x => new ExternalLinkView(x.Id, x.UserId, x.ExpiresAt)).SingleOrDefaultAsync(token);
    }

    public async Task ConfirmLinkAsync(ExternalInstallation installation, string session, long id, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        await using IDbContextTransaction transaction = await WorkStore.BeginAsync(db, token);
        ExternalLinkRequest request = await db.ExternalLinkRequests.SingleOrDefaultAsync(x => x.Id == id && x.InstallationId == installation.Id &&
            x.SessionId == session && !x.Consumed && x.ExpiresAt > DateTime.UtcNow && x.UserId != null, token)
            ?? throw new ApplicationFailure("external_link_expired");
        ExternalIdentity? identity = await db.ExternalIdentities.SingleOrDefaultAsync(x => x.InstallationId == installation.Id &&
            x.WorkspaceId == installation.WorkspaceId && x.UserId == request.UserId, token);
        if (identity is null) db.ExternalIdentities.Add(new()
        {
            InstallationId = installation.Id,
            WorkspaceId = installation.WorkspaceId,
            UserId = request.UserId!,
            LocalActor = nameof(LocalActor.Owner),
            Enabled = true
        });
        else identity.Enabled = true;
        request.Consumed = true;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    public async Task<ExternalIdentityView[]> IdentitiesAsync(ExternalInstallation installation, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        return await db.ExternalIdentities.Where(x => x.InstallationId == installation.Id && x.Enabled)
            .Select(x => new ExternalIdentityView(x.Id, x.UserId)).ToArrayAsync(token);
    }

    public async Task RevokeAsync(ExternalInstallation installation, long id, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        await using IDbContextTransaction transaction = await WorkStore.BeginAsync(db, token);
        await db.ExternalIdentities.Where(x => x.Id == id && x.InstallationId == installation.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.Enabled, false), token);
        await transaction.CommitAsync(token);
    }

    public async Task AcceptAsync(ExternalMessage message, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        await using IDbContextTransaction transaction = await WorkStore.BeginAsync(db, token);
        if (await db.ExternalMessages.AnyAsync(x => x.InstallationId == message.Installation.Id &&
            (x.EventId == message.EventId || x.WorkspaceId == message.Installation.WorkspaceId && x.ChannelId == message.ChannelId && x.MessageId == message.MessageId), token)) return;
        bool linking = message.Direct && message.Text.StartsWith("link ", StringComparison.Ordinal);
        bool validProof = false;
        if (linking)
        {
            string hash = Hash(message.Text[5..].Trim());
            ExternalLinkRequest? request = await db.ExternalLinkRequests.SingleOrDefaultAsync(x => x.InstallationId == message.Installation.Id &&
                x.WorkspaceId == message.Installation.WorkspaceId && x.CodeHash == hash && !x.Consumed && x.ExpiresAt > DateTime.UtcNow, token);
            if (request is not null && request.UserId is null) request.UserId = message.UserId;
            validProof = request?.UserId == message.UserId;
        }
        bool authorized = !linking && await AuthorizedAsync(db, message.Installation.Id, message.Installation.WorkspaceId, message.UserId, token);
        db.ExternalMessages.Add(new()
        {
            InstallationId = message.Installation.Id,
            WorkspaceId = message.Installation.WorkspaceId,
            AppId = message.Installation.AppId,
            EventId = message.EventId,
            UserId = message.UserId,
            ChannelId = message.ChannelId,
            ThreadId = message.ThreadId,
            MessageId = message.MessageId,
            Body = authorized ? message.Text : null,
            State = validProof ? nameof(ExternalMessageState.PendingLink) : nameof(ExternalMessageState.Pending),
            ReceivedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    public async Task<ExternalReply?> ProcessNextAsync(ExternalInstallation installation, CancellationToken token)
    {
        long? rejectedId = null;
        try
        {
            await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
            await using IDbContextTransaction transaction = await WorkStore.BeginAsync(db, token);
            ExternalMessageRow? message = await NextAsync(db, installation.Id, token);
            if (message is null) return null;
            rejectedId = message.Id;
            if (message.State == nameof(ExternalMessageState.PendingLink))
            {
                message.State = nameof(ExternalMessageState.Accepted);
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
                return new(message.Id, message.ChannelId, message.ThreadId, null, ExternalReplyKind.LinkConfirmationRequired);
            }
            if (message.Body is null || !await AuthorizedAsync(db, installation.Id, message.WorkspaceId, message.UserId, token))
            {
                message.State = nameof(ExternalMessageState.Rejected); message.Body = null;
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
                return new(message.Id, message.ChannelId, message.ThreadId, null, ExternalReplyKind.AccessRequired);
            }
            IDbContextOutbox outbox = _outboxes.Create(db);
            ExternalConversation? source = await db.ExternalConversations.SingleOrDefaultAsync(x => x.InstallationId == installation.Id &&
                x.WorkspaceId == message.WorkspaceId && x.ChannelId == message.ChannelId && x.ThreadId == message.ThreadId, token);
            Conversation conversation;
            WorkView view;
            if (source is null)
            {
                long workId = await IdentityStore.NextAsync(db, IdentityKind.Work, token);
                view = await _work.ApplyConversationCommandAsync(db, outbox, new(await IdentityStore.NextAsync(db, IdentityKind.Command, token),
                    workId, WorkAction.Create, Text: message.Body), token);
                conversation = new()
                {
                    Id = await IdentityStore.NextAsync(db, IdentityKind.Conversation, token),
                    WorkId = workId,
                    Title = message.Body[..Math.Min(80, message.Body.Length)],
                    CreatedAt = message.ReceivedAt
                };
                db.Conversations.Add(conversation);
                db.ExternalConversations.Add(new()
                {
                    InstallationId = installation.Id,
                    WorkspaceId = message.WorkspaceId,
                    ChannelId = message.ChannelId,
                    ThreadId = message.ThreadId,
                    ConversationId = conversation.Id
                });
                view = await _work.ApplyConversationCommandAsync(db, outbox, new(await IdentityStore.NextAsync(db, IdentityKind.Command, token),
                    workId, WorkAction.Execute, view.Version), token);
            }
            else
            {
                conversation = await db.Conversations.SingleAsync(x => x.Id == source.ConversationId, token);
                Persistence.Entities.WorkItem row = await db.WorkItems.SingleAsync(x => x.Id == conversation.WorkId, token);
                WorkSnapshot snapshot = System.Text.Json.JsonSerializer.Deserialize<WorkSnapshot>(row.State!, WorkStore.Json)!;
                WorkDecision? decision = snapshot.Decisions.LastOrDefault(x => x.AnsweredAt is null);
                // Only an existing question may consume a conversational answer.
                // Repository approval, retry, completion and cancellation stay in the local UI.
                WorkAction action = snapshot.Attention?.Reason == AttentionReason.InputRequired && decision is not null
                    ? WorkAction.Answer : WorkAction.AddContext;
                view = await _work.ApplyConversationCommandAsync(db, outbox, new(await IdentityStore.NextAsync(db, IdentityKind.Command, token),
                    row.Id, action, row.Version, message.Body, DecisionId: action == WorkAction.Answer ? decision!.Id : null), token);
            }
            long messageId = await IdentityStore.NextAsync(db, IdentityKind.Message, token);
            db.ConversationMessages.Add(new() { Id = messageId, ConversationId = conversation.Id, Body = message.Body, CreatedAt = message.ReceivedAt });
            message.ConversationMessageId = messageId; message.WorkId = view.Work.Id; message.State = nameof(ExternalMessageState.Accepted);
            await outbox.SaveChangesAndFlushMessagesAsync(token);
            return new(message.Id, message.ChannelId, message.ThreadId, view.Work.Id, ExternalReplyKind.WorkSaved);
        }
        catch (Exception error) when (rejectedId is not null && error is ApplicationFailure or WorkRuleException)
        {
            await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
            await using IDbContextTransaction transaction = await WorkStore.BeginAsync(db, token);
            ExternalMessageRow? message = await db.ExternalMessages.SingleOrDefaultAsync(x => x.Id == rejectedId && x.State == nameof(ExternalMessageState.Pending), token);
            if (message is null) return null;
            message.State = nameof(ExternalMessageState.Rejected);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return new(message.Id, message.ChannelId, message.ThreadId, null, ExternalReplyKind.CommandRejected);
        }
    }

    private static Task<ExternalMessageRow?> NextAsync(GoblinDbContext db, string installation, CancellationToken token) =>
        db.ExternalMessages.Where(x => x.InstallationId == installation &&
            (x.State == nameof(ExternalMessageState.Pending) || x.State == nameof(ExternalMessageState.PendingLink))).OrderBy(x => x.Id).FirstOrDefaultAsync(token);

    private static Task<bool> AuthorizedAsync(GoblinDbContext db, string installation, string workspace, string user, CancellationToken token) =>
        db.ExternalIdentities.AnyAsync(x => x.InstallationId == installation && x.WorkspaceId == workspace && x.UserId == user &&
            x.Enabled && x.LocalActor == nameof(LocalActor.Owner), token);

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
