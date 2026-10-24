using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts;
using Goblin.Core.Work;
using Goblin.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Goblin.Application.ChatGpt;

public sealed class ChatGptWorkReader
{
    private readonly IDbContextFactory<GoblinDbContext> _factory;

    public ChatGptWorkReader(IDbContextFactory<GoblinDbContext> factory) => _factory = factory;

    public async Task<SharedWorkPage> ListAsync(ChatGptReader reader, long afterWorkId, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        // Each read applies authorization and projection in one database statement.
        // Pagination is over authorized rows only and exposes no total/private IDs.
        SharedWorkRow[] rows = await db.Database.SqlQuery<SharedWorkRow>($"""
            SELECT w.id::text AS "WorkId", left(w.objective, 500) AS "Objective",
                w.status AS "Status", w.updated_at AS "UpdatedAt"
            FROM public.work_item w
            WHERE w.id > {afterWorkId} AND statement_timestamp() < {reader.ExpiresAt.UtcDateTime}
              AND EXISTS (SELECT 1 FROM public.chatgpt_work_grant g
                WHERE g.work_id = w.id AND g.issuer = {reader.Issuer} AND g.subject = {reader.Subject}
                  AND g.resource = {reader.Resource} AND g.revoked_at IS NULL AND g.expires_at > statement_timestamp())
            ORDER BY w.id LIMIT 26
            """).ToArrayAsync(token);
        return new SharedWorkPage
        {
            Items = [.. rows.Take(25).Select(Project)],
            NextAfterWorkId = rows.Length > 25 ? rows[24].WorkId : null
        };
    }

    public async Task<SharedWork> StatusAsync(ChatGptReader reader, long workId, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        SharedWorkRow row = await db.Database.SqlQuery<SharedWorkRow>($"""
            SELECT w.id::text AS "WorkId", left(w.objective, 500) AS "Objective",
                w.status AS "Status", w.updated_at AS "UpdatedAt"
            FROM public.work_item w WHERE w.id = {workId} AND statement_timestamp() < {reader.ExpiresAt.UtcDateTime}
              AND EXISTS (SELECT 1 FROM public.chatgpt_work_grant g
                WHERE g.work_id = w.id AND g.issuer = {reader.Issuer} AND g.subject = {reader.Subject}
                  AND g.resource = {reader.Resource} AND g.revoked_at IS NULL AND g.expires_at > statement_timestamp())
            """).SingleOrDefaultAsync(token) ?? throw new ChatGptAccessDenied();
        return Project(row);
    }

    public async Task<SharedWorkAttention> AttentionAsync(ChatGptReader reader, long workId, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        // Only core-owned attention codes leave the instance. Questions, history,
        // repository provenance, sessions, and upstream diagnostics are excluded.
        SharedAttentionRow row = await db.Database.SqlQuery<SharedAttentionRow>($"""
            SELECT w.id::text AS "WorkId", w.state->'attention'->>'reason' AS "Reason",
                w.state->'attention'->>'failure' AS "Failure"
            FROM public.work_item w WHERE w.id = {workId} AND statement_timestamp() < {reader.ExpiresAt.UtcDateTime}
              AND EXISTS (SELECT 1 FROM public.chatgpt_work_grant g
                WHERE g.work_id = w.id AND g.issuer = {reader.Issuer} AND g.subject = {reader.Subject}
                  AND g.resource = {reader.Resource} AND g.revoked_at IS NULL AND g.expires_at > statement_timestamp())
            """).SingleOrDefaultAsync(token) ?? throw new ChatGptAccessDenied();
        return new SharedWorkAttention
        {
            WorkId = row.WorkId,
            Reason = ParseCode<AttentionReason>(row.Reason),
            Failure = ParseCode<FailureKind>(row.Failure)
        };
    }

    public async Task<SharedWorkResult> ResultAsync(ChatGptReader reader, long workId, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        return await db.Database.SqlQuery<SharedWorkResult>($"""
            SELECT w.id::text AS "WorkId", left(r->>'text', 8000) AS "Text",
                length(r->>'text') > 8000 AS "Truncated"
            FROM public.work_item w JOIN public.chatgpt_work_grant g ON g.work_id = w.id
            CROSS JOIN LATERAL jsonb_array_elements(w.state->'results') r
            WHERE w.id = {workId} AND statement_timestamp() < {reader.ExpiresAt.UtcDateTime}
              AND g.issuer = {reader.Issuer} AND g.subject = {reader.Subject} AND g.resource = {reader.Resource}
              AND g.revoked_at IS NULL AND g.expires_at > statement_timestamp()
              AND r->>'attemptId' = g.result_attempt_id::text AND r->>'approvedAt' IS NOT NULL
            """).SingleOrDefaultAsync(token) ?? throw new ChatGptAccessDenied();
    }

    private static SharedWork Project(SharedWorkRow row) => new()
    {
        WorkId = row.WorkId,
        Objective = row.Objective,
        Status = ContractValue.Parse<WorkStatus>(row.Status),
        UpdatedAt = row.UpdatedAt
    };

    private static T? ParseCode<T>(string? value) where T : struct, Enum => value is null ? null :
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value), ContractJson.Options);

    private sealed class SharedWorkRow
    {
        public string WorkId { get; init; } = "";
        public string Objective { get; init; } = "";
        public string Status { get; init; } = "";
        public DateTimeOffset UpdatedAt { get; init; }
    }

    private sealed class SharedAttentionRow
    {
        public string WorkId { get; init; } = "";
        public string? Reason { get; init; }
        public string? Failure { get; init; }
    }
}
