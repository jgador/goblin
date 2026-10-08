using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.Runtime;
using Goblin.Contracts;
using Goblin.Contracts.Runtime;
using Goblin.Persistence;
using Goblin.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Goblin.Application.Work;

// The controller owns discovery; PostgreSQL keeps the last complete response
// across controller restarts. Refresh failures never replace successful data.
public sealed class ModelCatalogStore
{
    private readonly IDbContextFactory<GoblinDbContext> _dbFactory;
    private readonly IModelCatalogSource _source;
    private readonly ModelCatalogRefreshCoordinator _refreshes;

    public ModelCatalogStore(IDbContextFactory<GoblinDbContext> dbFactory, IModelCatalogSource source,
        ModelCatalogRefreshCoordinator refreshes)
    {
        _dbFactory = dbFactory;
        _source = source;
        _refreshes = refreshes;
    }

    public async Task<ModelCatalogView> GetAsync(long connectionId, int limit, string? selected,
        CancellationToken token = default)
    {
        if (limit is not (3 or 10) || selected?.Length > 128) throw new ApplicationFailure("invalid_model_selection");
        await using GoblinDbContext db = await _dbFactory.CreateDbContextAsync(token);
        Connection connection = await db.Connections.AsNoTracking().SingleOrDefaultAsync(x => x.Id == connectionId, token)
            ?? throw new ApplicationFailure("connection_not_found");
        if (connection.Runtime != _source.Runtime) throw new ApplicationFailure("models_unavailable");
        ConnectionModelCatalog? saved = await db.ConnectionModelCatalogs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ConnectionId == connectionId, token);
        string stamp = _source.ExecutableStamp();
        bool sameAccount = ModelCatalogPolicy.SameAccount(saved?.AuthGeneration, connection.AuthGeneration);
        RuntimeModel[] all = sameAccount ? ModelCatalogPolicy.Read(saved?.Catalog) : [];
        bool due = ModelCatalogPolicy.Due(saved?.FetchedAt, saved?.RefreshFailed == true,
            saved?.ExecutableStamp, stamp, DateTime.UtcNow);
        bool canRefresh = connection.Availability == "Available" &&
            ModelCatalogPolicy.CanAttempt(saved?.RetryAfter, DateTime.UtcNow);
        if (canRefresh && (!sameAccount || due)) ScheduleRefresh(connectionId);
        bool refreshing = _refreshes.IsRefreshing(connectionId);
        RuntimeModel[] ordered = ModelCatalogPolicy.Order(all, selected);
        return new([.. ordered.Take(limit)], ordered.Length > limit,
            all.FirstOrDefault(x => x.IsDefault)?.Model,
            saved?.FetchedAt is { } fetched && sameAccount ? new DateTimeOffset(fetched, TimeSpan.Zero) : null,
            all.Length > 0 && (due || saved?.RefreshFailed == true || connection.Availability != "Available"),
            refreshing, all.Length == 0 && !refreshing);
    }

    // Called after sign-in/account changes and when a status check notices a new
    // executable. Neither path waits for model discovery to answer the user.
    public void ScheduleRefresh(long connectionId, bool force = false) =>
        _refreshes.Schedule(connectionId, token => RefreshAsync(connectionId, force, token));

    public async Task ObserveExecutableAsync(long connectionId, CancellationToken token = default)
    {
        await using GoblinDbContext db = await _dbFactory.CreateDbContextAsync(token);
        Connection connection = await db.Connections.AsNoTracking().SingleAsync(x => x.Id == connectionId, token);
        if (connection.Availability != "Available") return;
        ConnectionModelCatalog? saved = await db.ConnectionModelCatalogs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ConnectionId == connectionId, token);
        if ((!ModelCatalogPolicy.SameAccount(saved?.AuthGeneration, connection.AuthGeneration) ||
            saved?.ExecutableStamp != _source.ExecutableStamp()) &&
            ModelCatalogPolicy.CanAttempt(saved?.RetryAfter, DateTime.UtcNow))
            ScheduleRefresh(connectionId);
    }

    public static async Task ValidateSelectionAsync(GoblinDbContext db, Connection connection,
        string? model, string? effort, CancellationToken token)
    {
        if (model is null && effort is null) return;
        ConnectionModelCatalog? saved = await db.ConnectionModelCatalogs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ConnectionId == connection.Id, token);
        if (saved?.Catalog is null || saved.AuthGeneration != connection.AuthGeneration)
            throw new ApplicationFailure("models_unavailable");
        ModelCatalogPolicy.ValidateSelection(ModelCatalogPolicy.Read(saved.Catalog), model, effort);
    }

    private async Task RefreshAsync(long connectionId, bool force, CancellationToken token)
    {
        long generation;
        string stamp = _source.ExecutableStamp();
        await using (GoblinDbContext db = await _dbFactory.CreateDbContextAsync(token))
        {
            Connection? connection = await db.Connections.AsNoTracking().SingleOrDefaultAsync(x => x.Id == connectionId, token);
            if (connection is null || connection.Runtime != _source.Runtime || connection.Availability != "Available") return;
            generation = connection.AuthGeneration;
            ConnectionModelCatalog? saved = await db.ConnectionModelCatalogs.AsNoTracking()
                .SingleOrDefaultAsync(x => x.ConnectionId == connectionId, token);
            if (!force && ModelCatalogPolicy.Fresh(saved?.AuthGeneration, generation, saved?.FetchedAt,
                saved?.RefreshFailed == true, saved?.ExecutableStamp, stamp, DateTime.UtcNow)) return;
            if (!force && !ModelCatalogPolicy.CanAttempt(saved?.RetryAfter, DateTime.UtcNow)) return;
        }

        RuntimeModel[]? models = null;
        try { models = await _source.ListAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { /* A sanitized refresh state is persisted below. */ }

        token.ThrowIfCancellationRequested();
        await using GoblinDbContext write = await _dbFactory.CreateDbContextAsync(token);
        await using IDbContextTransaction transaction = await ApplicationTransaction.BeginAsync(write, token);
        Connection current = await write.Connections.SingleAsync(x => x.Id == connectionId, token);
        if (current.AuthGeneration != generation || current.Availability != "Available") return;
        ConnectionModelCatalog? row = await write.ConnectionModelCatalogs.SingleOrDefaultAsync(x => x.ConnectionId == connectionId, token);
        if (row is null)
        {
            row = new() { ConnectionId = connectionId, AuthGeneration = generation, ExecutableStamp = stamp };
            write.ConnectionModelCatalogs.Add(row);
        }
        if (models is null)
        {
            row.RetryAfter = ModelCatalogPolicy.RetryAfter(DateTime.UtcNow);
            row.RefreshFailed = true;
        }
        else
        {
            RuntimeModel[] previous = ModelCatalogPolicy.Read(row.Catalog);
            row.Catalog = JsonSerializer.Serialize(ModelCatalogPolicy.MarkNew(models, previous), ContractJson.Options);
            row.ExecutableStamp = stamp;
            row.FetchedAt = DateTime.UtcNow;
            row.RetryAfter = null;
            row.RefreshFailed = false;
        }
        await write.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

}
