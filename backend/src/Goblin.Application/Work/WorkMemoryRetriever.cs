using System;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Core.Work;
using Goblin.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Goblin.Application.Work;

// A bounded, local retrieval layer over approved Work. No model service,
// external index, or runtime session is needed to remember an outcome.
public sealed class WorkMemoryRetriever
{
    private readonly IDbContextFactory<GoblinDbContext> _factory;

    public WorkMemoryRetriever(IDbContextFactory<GoblinDbContext> factory) => _factory = factory;

    public async Task<WorkMemory[]> SearchAsync(WorkSnapshot current, CancellationToken token = default)
    {
        string[] terms = Regex.Matches(current.Objective, @"[\p{L}\p{N}]{3,}")
            .Select(x => x.Value.ToLowerInvariant()).Distinct().Take(8).ToArray();
        if (terms.Length == 0) return [];
        string query = string.Join(" OR ", terms);
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        // The GIN index in 0001_initial.sql keeps this bounded as history grows.
        // Only completed Work can supply a memory; the result is checked again
        // below for an explicit approval in its durable snapshot.
        var candidates = await db.WorkItems.FromSqlInterpolated($"""
            SELECT * FROM public.work_items
            WHERE status = 'Completed' AND id <> {current.Id}
              AND to_tsvector('english', objective) @@ websearch_to_tsquery('english', {query})
            ORDER BY ts_rank(to_tsvector('english', objective), websearch_to_tsquery('english', {query})) DESC,
                     updated_at DESC, id DESC
            LIMIT 12
            """).AsNoTracking().ToArrayAsync(token);
        return [.. candidates.Select(row =>
        {
            if (row.State is null) return null;
            WorkSnapshot? source = JsonSerializer.Deserialize<WorkSnapshot>(row.State, WorkStore.Json);
            WorkResult? approved = source?.Results.LastOrDefault(x => x.ApprovedAt is not null);
            if (approved is null) return null;
            return new WorkMemory(row.Id, Clip(row.Objective, 500), Clip(approved.Text, 1800));
        }).Where(x => x is not null).Take(3).Cast<WorkMemory>()];
    }

    private static string Clip(string value, int limit) => value.Length <= limit ? value : value[..limit];
}
