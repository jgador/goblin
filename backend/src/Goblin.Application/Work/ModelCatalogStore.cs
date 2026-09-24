using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(1);
    private readonly IDbContextFactory<GoblinDbContext> _dbFactory;
    private readonly IModelCatalogSource _source;
    private readonly Lock _gate = new();
    private readonly Dictionary<long, Task> _refreshes = [];

    public ModelCatalogStore(IDbContextFactory<GoblinDbContext> dbFactory, IModelCatalogSource source)
    {
        _dbFactory = dbFactory;
        _source = source;
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
        bool sameAccount = saved?.AuthGeneration == connection.AuthGeneration;
        RuntimeModel[] all = sameAccount && saved?.Catalog is not null ? Parse(saved.Catalog) : [];
        bool due = saved?.FetchedAt is null || saved.RefreshFailed || saved.FetchedAt.Value.Add(Lifetime) <= DateTime.UtcNow ||
            saved.ExecutableStamp != stamp;
        bool canRefresh = connection.Availability == "Available" &&
            (saved?.RetryAfter is null || saved.RetryAfter <= DateTime.UtcNow);
        if (canRefresh && (!sameAccount || due) && !IsRefreshing(connectionId)) ScheduleRefresh(connectionId);
        bool refreshing = IsRefreshing(connectionId);
        RuntimeModel[] ordered = Order(all, selected);
        return new([.. ordered.Take(limit)], ordered.Length > limit,
            all.FirstOrDefault(x => x.IsDefault)?.Model,
            saved?.FetchedAt is { } fetched && sameAccount ? new DateTimeOffset(fetched, TimeSpan.Zero) : null,
            all.Length > 0 && (due || saved?.RefreshFailed == true || connection.Availability != "Available"),
            refreshing, all.Length == 0 && !refreshing);
    }

    // Called after sign-in/account changes and when a status check notices a new
    // executable. Neither path waits for model discovery to answer the user.
    public void ScheduleRefresh(long connectionId, bool force = false)
    {
        lock (_gate)
        {
            if (_refreshes.ContainsKey(connectionId)) return;
            Task task = Task.Run(async () =>
            {
                try { await RefreshAsync(connectionId, force); }
                catch { /* Database recovery remains owned by the normal health path. */ }
            });
            _refreshes[connectionId] = task;
            _ = task.ContinueWith(_ =>
            {
                lock (_gate) _refreshes.Remove(connectionId);
            }, TaskScheduler.Default);
        }
    }

    public async Task ObserveExecutableAsync(long connectionId, CancellationToken token = default)
    {
        await using GoblinDbContext db = await _dbFactory.CreateDbContextAsync(token);
        Connection connection = await db.Connections.AsNoTracking().SingleAsync(x => x.Id == connectionId, token);
        if (connection.Availability != "Available") return;
        ConnectionModelCatalog? saved = await db.ConnectionModelCatalogs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ConnectionId == connectionId, token);
        if ((saved is null || saved.AuthGeneration != connection.AuthGeneration ||
            saved.ExecutableStamp != _source.ExecutableStamp()) &&
            (saved?.RetryAfter is null || saved.RetryAfter <= DateTime.UtcNow))
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
        RuntimeModel? choice = (model is null
            ? Parse(saved.Catalog).FirstOrDefault(x => x.IsDefault)
            : Parse(saved.Catalog).FirstOrDefault(x => x.Model == model)) ?? throw new ApplicationFailure("model_unavailable");
        if (effort is not null && !choice.SupportedReasoningEfforts.Contains(effort, StringComparer.Ordinal))
            throw new ApplicationFailure("reasoning_effort_unavailable");
    }

    private bool IsRefreshing(long connectionId)
    {
        lock (_gate) return _refreshes.ContainsKey(connectionId);
    }

    private async Task RefreshAsync(long connectionId, bool force)
    {
        long generation;
        string stamp = _source.ExecutableStamp();
        await using (GoblinDbContext db = await _dbFactory.CreateDbContextAsync())
        {
            Connection? connection = await db.Connections.AsNoTracking().SingleOrDefaultAsync(x => x.Id == connectionId);
            if (connection is null || connection.Runtime != _source.Runtime || connection.Availability != "Available") return;
            generation = connection.AuthGeneration;
            ConnectionModelCatalog? saved = await db.ConnectionModelCatalogs.AsNoTracking()
                .SingleOrDefaultAsync(x => x.ConnectionId == connectionId);
            if (!force && saved?.AuthGeneration == generation && !saved.RefreshFailed &&
                saved.ExecutableStamp == stamp && saved.FetchedAt?.Add(Lifetime) > DateTime.UtcNow) return;
            if (!force && saved?.RetryAfter > DateTime.UtcNow) return;
        }

        RuntimeModel[]? models = null;
        try { models = await _source.ListAsync(CancellationToken.None); }
        catch { /* A sanitized refresh state is persisted below. */ }

        await using GoblinDbContext write = await _dbFactory.CreateDbContextAsync();
        await using IDbContextTransaction transaction = await WorkStore.BeginAsync(write, default);
        Connection current = await write.Connections.SingleAsync(x => x.Id == connectionId);
        if (current.AuthGeneration != generation || current.Availability != "Available") return;
        ConnectionModelCatalog? row = await write.ConnectionModelCatalogs.SingleOrDefaultAsync(x => x.ConnectionId == connectionId);
        if (row is null)
        {
            row = new() { ConnectionId = connectionId, AuthGeneration = generation, ExecutableStamp = stamp };
            write.ConnectionModelCatalogs.Add(row);
        }
        if (models is null)
        {
            row.RetryAfter = DateTime.UtcNow.Add(FailureCooldown);
            row.RefreshFailed = true;
        }
        else
        {
            RuntimeModel[] previous = row.Catalog is null ? [] : Parse(row.Catalog);
            var previousNames = previous.Select(x => x.Model).ToHashSet(StringComparer.Ordinal);
            row.Catalog = JsonSerializer.Serialize(models.Select(x => x with
            {
                IsNew = previous.Length > 0 && !previousNames.Contains(x.Model)
            }).ToArray(), WorkStore.Json);
            row.ExecutableStamp = stamp;
            row.FetchedAt = DateTime.UtcNow;
            row.RetryAfter = null;
            row.RefreshFailed = false;
        }
        await write.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private static RuntimeModel[] Parse(string json) =>
        JsonSerializer.Deserialize<RuntimeModel[]>(json, WorkStore.Json) ?? [];

    private static RuntimeModel[] Order(RuntimeModel[] models, string? selected) =>
        [.. models.OrderByDescending(x => x.Model == selected)
            .ThenByDescending(x => x.IsDefault)
            .ThenByDescending(x => x.IsNew)];
}
