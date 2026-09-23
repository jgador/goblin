using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.Work;
using Goblin.Contracts.Runtime;
using Goblin.Core.Repositories;
using Goblin.Core.Work;
using Goblin.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Row = Goblin.Persistence.Entities.RepositorySetupMemory;

namespace Goblin.Application.Repositories;

public sealed class RepositorySetupStore
{
    private readonly IDbContextFactory<GoblinDbContext> _factory;
    public RepositorySetupStore(IDbContextFactory<GoblinDbContext> factory) => _factory = factory;

    public async Task<RepositorySetupMemory[]> ReadAsync(long attemptId, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        WorkSnapshot work = await CurrentAsync(db, attemptId, token);
        RepositoryGrant grant = work.Attempts[^1].Target.Repository!.Grant!;
        // Collapse repeated verification of the same inputs before applying the
        // bound, so frequent runs do not crowd older branch variants out.
        var rows = await db.RepositorySetupMemories.FromSqlInterpolated($"""
            SELECT DISTINCT ON (topic, environment, fingerprint) *
            FROM public.repository_setup_memories
            WHERE repository_id = {grant.RepositoryId} AND github_connection_id = {grant.ConnectionId} AND account_id = {grant.AccountId}
            ORDER BY topic, environment, fingerprint, verified_at DESC
            """).AsNoTracking().OrderByDescending(x => x.VerifiedAt).Take(64)
            .Select(x => new
            {
                x.Id,
                x.WorkId,
                x.AttemptId,
                x.TurnNumber,
                x.Environment,
                x.VerifiedAt,
                x.Observation,
                x.Checkpoint.Branch,
                Commit = x.Checkpoint.CommitSha
            }).ToArrayAsync(token);
        return [.. rows.Select(x => new RepositorySetupMemory(x.Id, x.WorkId, x.AttemptId, x.TurnNumber,
            x.Branch, x.Commit, x.Environment, x.VerifiedAt,
            JsonSerializer.Deserialize<VerifiedRepositorySetup>(x.Observation, WorkStore.Json)!))];
    }

    public async Task SaveAsync(long attemptId, SetupMemoryWrite request, CancellationToken token)
    {
        if (!RepositorySetupRules.SafeText(request.Environment, 1000) || request.TurnNumber <= 0 ||
            request.Observations is not { Length: > 0 and <= RepositorySetupRules.MaxObservations } ||
            !request.Observations.All(RepositorySetupRules.Valid) ||
            request.Observations.Select(x => x.Setup.Topic).Distinct(StringComparer.Ordinal).Count() != request.Observations.Length ||
            JsonSerializer.SerializeToUtf8Bytes(request, WorkStore.Json).Length > RepositorySetupRules.MaxPayloadBytes)
            throw new ApplicationFailure("repository_setup_invalid");
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        await using IDbContextTransaction transaction = await WorkStore.BeginAsync(db, token);
        WorkSnapshot work = await CurrentAsync(db, attemptId, token);
        AttemptSnapshot attempt = work.Attempts[^1];
        RepositoryGrant grant = attempt.Target.Repository!.Grant!;
        if (attempt.TurnNumber != request.TurnNumber || !await db.WorkspaceCheckpoints.AnyAsync(x =>
            x.Id == request.CheckpointId && x.WorkId == work.Id && x.AttemptId == attemptId &&
            x.TurnNumber == request.TurnNumber && x.WorkspaceNumber == attempt.WorkspaceNumber &&
            x.Repository == attempt.Target.Repository.Repository && x.Branch == grant.Branch, token))
            throw new ApplicationFailure("repository_setup_unconfirmed");
        Row[] previous = await db.RepositorySetupMemories.Where(x => x.AttemptId == attemptId && x.TurnNumber == request.TurnNumber).ToArrayAsync(token);
        if (previous.Length > 0)
        {
            if (previous.Length != request.Observations.Length || request.Observations.Any(observation => !previous.Any(row =>
                row.Topic == observation.Setup.Topic && row.CheckpointId == request.CheckpointId && row.Environment == request.Environment &&
                JsonSerializer.Serialize(JsonSerializer.Deserialize<VerifiedRepositorySetup>(row.Observation, WorkStore.Json), WorkStore.Json) ==
                JsonSerializer.Serialize(observation, WorkStore.Json))))
                throw new ApplicationFailure("repository_setup_changed");
            return;
        }
        foreach (VerifiedRepositorySetup observation in request.Observations)
        {
            string fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            { observation.ConfigurationHash, Files = observation.Files.OrderBy(x => x.Path, StringComparer.Ordinal) }, WorkStore.Json)));
            db.RepositorySetupMemories.Add(new()
            {
                RepositoryId = grant.RepositoryId,
                GithubConnectionId = grant.ConnectionId,
                AccountId = grant.AccountId,
                CheckpointId = request.CheckpointId,
                WorkId = work.Id,
                AttemptId = attemptId,
                TurnNumber = request.TurnNumber,
                Topic = observation.Setup.Topic,
                Environment = request.Environment,
                Fingerprint = fingerprint,
                Observation = JsonSerializer.Serialize(observation, WorkStore.Json),
                VerifiedAt = DateTime.UtcNow
            });
        }
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    public async Task SaveAsync(long attemptId, Stream input, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[4096]; int count;
        while ((count = await input.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + count > RepositorySetupRules.MaxPayloadBytes) throw new ApplicationFailure("repository_setup_invalid");
            await buffer.WriteAsync(chunk.AsMemory(0, count), token);
        }
        SetupMemoryWrite request;
        try { request = JsonSerializer.Deserialize<SetupMemoryWrite>(buffer.ToArray(), WorkStore.Json) ?? throw new JsonException(); }
        catch (JsonException) { throw new ApplicationFailure("repository_setup_invalid"); }
        await SaveAsync(attemptId, request, token);
    }

    private static async Task<WorkSnapshot> CurrentAsync(GoblinDbContext db, long attemptId, CancellationToken token)
    {
        var owner = await db.ExecutionAttempts.AsNoTracking().Where(x => x.Id == attemptId)
            .Select(x => new { x.Work.State, x.Status, x.TurnNumber }).SingleOrDefaultAsync(token);
        if (owner is null || owner.Status is not ("Starting" or "Running") || owner.State is null)
            throw new ApplicationFailure("repository_setup_unavailable");
        WorkSnapshot work = JsonSerializer.Deserialize<WorkSnapshot>(owner.State, WorkStore.Json)!;
        AttemptSnapshot attempt = work.Attempts[^1];
        RepositoryGrant? grant = attempt.Target.Repository?.Grant;
        if (attempt.Id != attemptId || attempt.TurnNumber != owner.TurnNumber || grant is null ||
            !await db.GithubRepositories.AnyAsync(x => x.Id == grant.RepositoryId && x.ConnectionId == grant.ConnectionId &&
                x.Name == attempt.Target.Repository!.Repository && x.Enabled && x.Connection.AccountId == grant.AccountId &&
                x.Connection.Generation == grant.Generation && x.Connection.Availability == "Connected", token))
            throw new ApplicationFailure("repository_setup_unavailable");
        return work;
    }
}
