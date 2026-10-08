using System;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.Work;
using Goblin.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Goblin.Application;

// Owns the durable sequence mapping shared by application use cases.
// Sequence gaps after cancellation or rollback are intentional; IDs are never reused.
internal static class IdentitySequence
{
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
            IdentityKind.GitRepositoryOperation => "public.repository_operations_id_seq",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return db.Database.SqlQuery<long>($"SELECT nextval({sequence}::regclass) AS \"Value\"").SingleAsync(token);
    }
}
