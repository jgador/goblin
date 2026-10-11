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
            IdentityKind.Work => "public.work_item_id_seq",
            IdentityKind.Command => "public.work_command_id_seq",
            IdentityKind.Conversation => "public.conversation_id_seq",
            IdentityKind.Message => "public.conversation_message_id_seq",
            IdentityKind.Attempt => "public.execution_attempt_id_seq",
            IdentityKind.Event => "public.work_event_id_seq",
            IdentityKind.Inspection => "public.workspace_session_id_seq",
            IdentityKind.GitRepositoryOperation => "public.repository_operation_id_seq",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return db.Database.SqlQuery<long>($"SELECT nextval({sequence}::regclass) AS \"Value\"").SingleAsync(token);
    }
}
