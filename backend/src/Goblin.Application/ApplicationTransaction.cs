using System.Threading;
using System.Threading.Tasks;
using Goblin.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Goblin.Application;

// One transaction gate for short application state changes across Work,
// conversations, connections, repository operations, and inspection sessions.
// Callers own commit/rollback and their outbox. Runtime/network I/O stays outside.
internal static class ApplicationTransaction
{
    internal static async Task<IDbContextTransaction> BeginAsync(GoblinDbContext db, CancellationToken token)
    {
        IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(token);
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(716352019)", token);
            return transaction;
        }
        catch { await transaction.DisposeAsync(); throw; }
    }
}
