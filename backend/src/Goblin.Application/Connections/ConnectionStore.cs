using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.Runtime;
using Goblin.Application.Work;
using Goblin.Contracts;
using Goblin.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Row = Goblin.Persistence.Entities.Connection;

namespace Goblin.Application.Connections;

// Owns connection state and its reservations. Work claims read the same durable
// rows under the shared transaction lock before contacting an execution host.
public sealed class ConnectionStore
{
    private readonly IDbContextFactory<GoblinDbContext> _dbFactory;

    public ConnectionStore(IDbContextFactory<GoblinDbContext> dbFactory) => _dbFactory = dbFactory;

    public const long DefaultConnectionId = 1;

    public async Task<ConnectionView[]> ListAsync(CancellationToken token = default)
    {
        await using GoblinDbContext db = await _dbFactory.CreateDbContextAsync(token);
        return await db.Connections.AsNoTracking().OrderBy(x => x.Name)
            .Select(x => new ConnectionView(x.Id, x.Runtime, x.Name, ContractValue.Parse<ConnectionAvailability>(x.Availability))).ToArrayAsync(token);
    }

    public async Task BeginChangeAsync(long id, CancellationToken token = default)
    {
        await using GoblinDbContext db = await _dbFactory.CreateDbContextAsync(token);
        await using IDbContextTransaction transaction = await ApplicationTransaction.BeginAsync(db, token);
        if (await ResourceReservations.Attempts(db.ExecutionAttempts).AnyAsync(x => x.ConnectionId == id, token))
            throw new ApplicationFailure("connection_in_use");
        Row row = await db.Connections.SingleAsync(x => x.Id == id, token);
        if (row.Availability == nameof(ConnectionAvailability.Changing)) throw new ApplicationFailure("connection_in_use");
        row.Availability = nameof(ConnectionAvailability.Changing);
        row.ChangedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    public Task<bool> CompleteChangeAsync(long id, ConnectionAvailability availability, string? accountSignature,
        CancellationToken token = default) => UpdateAsync(id, availability, UpdateKind.CompleteChange, accountSignature, token);

    public Task<bool> ObserveAccountAsync(long id, ConnectionAvailability availability, string? accountSignature,
        CancellationToken token = default) => UpdateAsync(id, availability, UpdateKind.Account, accountSignature, token);

    public Task ObserveAvailabilityAsync(long id, ConnectionAvailability availability, CancellationToken token = default) =>
        UpdateAsync(id, availability, UpdateKind.Availability, null, token);

    private async Task<bool> UpdateAsync(long id, ConnectionAvailability availability, UpdateKind kind,
        string? accountSignature, CancellationToken token)
    {
        if (!Enum.IsDefined(availability)) throw new ApplicationFailure("invalid_command");
        await using GoblinDbContext db = await _dbFactory.CreateDbContextAsync(token);
        await using IDbContextTransaction transaction = await ApplicationTransaction.BeginAsync(db, token);
        Row row = await db.Connections.SingleAsync(x => x.Id == id, token);
        if (row.Availability is nameof(ConnectionAvailability.Changing) or nameof(ConnectionAvailability.Verifying) &&
            kind != UpdateKind.CompleteChange) return false;
        bool changedAccount = kind == UpdateKind.CompleteChange ||
            kind == UpdateKind.Account && row.AccountSignature != accountSignature;
        if (changedAccount)
        {
            row.AuthGeneration++;
            row.AccountSignature = accountSignature;
            await db.ConnectionModelCatalogs.Where(x => x.ConnectionId == id).ExecuteDeleteAsync(token);
        }
        row.Availability = availability.ToString();
        row.ChangedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return changedAccount;
    }

    public async Task BeginVerificationAsync(long id, CancellationToken token = default)
    {
        await using GoblinDbContext db = await _dbFactory.CreateDbContextAsync(token);
        await using IDbContextTransaction transaction = await ApplicationTransaction.BeginAsync(db, token);
        Row row = await db.Connections.SingleAsync(x => x.Id == id, token);
        if (row.Availability == nameof(ConnectionAvailability.Verifying)) throw new ApplicationFailure("prompt_in_progress");
        if (row.Availability == nameof(ConnectionAvailability.Changing) || await ResourceReservations.Attempts(db.ExecutionAttempts).AnyAsync(x => x.ConnectionId == id, token))
            throw new ApplicationFailure("connection_in_use");
        row.Availability = nameof(ConnectionAvailability.Verifying);
        row.ChangedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    public async Task EndVerificationAsync(long id, bool available)
    {
        await using GoblinDbContext db = await _dbFactory.CreateDbContextAsync();
        await using IDbContextTransaction transaction = await ApplicationTransaction.BeginAsync(db, default);
        Row row = await db.Connections.SingleAsync(x => x.Id == id);
        // A waiting account change owns the reservation until its operation ends.
        if (row.Availability != nameof(ConnectionAvailability.Verifying)) return;
        row.Availability = available ? nameof(ConnectionAvailability.Available) : nameof(ConnectionAvailability.Unavailable);
        row.ChangedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    // The single controller's verification cannot survive its restart;
    // durable execution attempts can and retain their own reservations.
    public async Task RecoverReservationsAsync(CancellationToken token)
    {
        await using GoblinDbContext db = await _dbFactory.CreateDbContextAsync(token);
        await using IDbContextTransaction transaction = await ApplicationTransaction.BeginAsync(db, token);
        await db.Connections.Where(x => x.Availability == nameof(ConnectionAvailability.Changing) || x.Availability == nameof(ConnectionAvailability.Verifying))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Availability, nameof(ConnectionAvailability.Unavailable)).SetProperty(x => x.ChangedAt, DateTime.UtcNow), token);
        await transaction.CommitAsync(token);
    }

    private enum UpdateKind { Availability, Account, CompleteChange }
}
