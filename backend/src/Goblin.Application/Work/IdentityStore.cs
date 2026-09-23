using System;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Goblin.Application.Work;

public enum IdentityKind { Work, Command, Conversation, Message, Attempt, Event, Inspection, RepositoryOperation }
public sealed record IdentityRequest(IdentityKind[] Kinds);
public sealed record ReservedIdentities(long[] Ids);

// Reserve IDs before constructing core objects or a replayable browser command.
// Sequence gaps after cancellation or rollback are intentional; IDs are never reused.
public sealed class IdentityStore
{
    private readonly IDbContextFactory<GoblinDbContext> _dbFactory;

    public IdentityStore(IDbContextFactory<GoblinDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<ReservedIdentities> ReserveAsync(IdentityRequest request, CancellationToken token = default)
    {
        if (request.Kinds is null || request.Kinds.Length is < 1 or > 4 ||
            Array.Exists(request.Kinds, kind => kind is not (IdentityKind.Work or IdentityKind.Command or IdentityKind.Conversation or IdentityKind.Message or IdentityKind.Inspection)))
            throw new ApplicationFailure("invalid_command");
        await using GoblinDbContext db = await _dbFactory.CreateDbContextAsync(token);
        long[] ids = new long[request.Kinds.Length];
        for (int i = 0; i < ids.Length; i++) ids[i] = await NextAsync(db, request.Kinds[i], token);
        return new(ids);
    }

    public async Task<long> NextEventAsync(CancellationToken token = default)
    {
        await using GoblinDbContext db = await _dbFactory.CreateDbContextAsync(token);
        return await NextAsync(db, IdentityKind.Event, token);
    }

    internal static Task<long> NextAsync(GoblinDbContext db, IdentityKind kind, CancellationToken token = default)
    {
        string sequence = kind switch
        {
            IdentityKind.Work => "public.work_items_id_seq",
            IdentityKind.Command => "public.work_commands_id_seq",
            IdentityKind.Conversation => "public.conversations_id_seq",
            IdentityKind.Message => "public.conversation_messages_id_seq",
            IdentityKind.Attempt => "public.execution_attempts_id_seq",
            IdentityKind.Event => "public.work_event_ids",
            IdentityKind.Inspection => "public.workspace_sessions_id_seq",
            IdentityKind.RepositoryOperation => "public.repository_operations_id_seq",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return db.Database.SqlQuery<long>($"SELECT nextval({sequence}::regclass) AS \"Value\"").SingleAsync(token);
    }
}
