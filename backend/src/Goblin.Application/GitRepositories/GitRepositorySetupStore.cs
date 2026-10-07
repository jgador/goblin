using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.Work;
using Goblin.Contracts;
using Goblin.Contracts.Runtime;
using Goblin.Core.GitRepositories;
using Goblin.Core.Work;
using Goblin.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Row = Goblin.Persistence.Entities.GitRepositorySetupMemory;

namespace Goblin.Application.GitRepositories;

public sealed class GitRepositorySetupStore
{
    private readonly IDbContextFactory<GoblinDbContext> _factory;

    public GitRepositorySetupStore(IDbContextFactory<GoblinDbContext> factory) => _factory = factory;

    public async Task<GitRepositorySetupMemory[]> ReadAsync(long attemptId, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        WorkSnapshot work = await CurrentAsync(db, attemptId, token);
        GitRepositoryGrant grant = work.Attempts[^1].Target.GitRepository!.Grant!;
        // Collapse repeated verification of the same inputs before applying the
        // bound, so frequent runs do not crowd older branch variants out.
        var rows = await db.GitRepositorySetupMemories.FromSqlInterpolated($"""
            SELECT DISTINCT ON (topic, environment, fingerprint) *
            FROM public.repository_setup_memories
            WHERE repository_id = {grant.GitRepositoryId} AND github_connection_id = {grant.ConnectionId} AND account_id = {grant.AccountId}
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
        return [.. rows.Select(x => new GitRepositorySetupMemory
        {
            Id = x.Id,
            WorkId = x.WorkId,
            AttemptId = x.AttemptId,
            TurnNumber = x.TurnNumber,
            Branch = x.Branch,
            Commit = x.Commit,
            Environment = x.Environment,
            VerifiedAt = x.VerifiedAt,
            Observation = JsonSerializer.Deserialize<VerifiedGitRepositorySetup>(x.Observation, ContractJson.Options)!
        })];
    }

    public async Task SaveAsync(long attemptId, SetupMemoryWrite request, CancellationToken token)
    {
        if (!GitRepositorySetupRules.SafeText(request.Environment, 1000) || request.TurnNumber <= 0 ||
            request.Observations is not { Length: > 0 and <= GitRepositorySetupRules.MaxObservations } ||
            !request.Observations.All(GitRepositorySetupRules.Valid) ||
            request.Observations.Select(x => x.Setup.Topic).Distinct(StringComparer.Ordinal).Count() != request.Observations.Length ||
            JsonSerializer.SerializeToUtf8Bytes(request, ContractJson.Options).Length > GitRepositorySetupRules.MaxPayloadBytes)
            throw new ApplicationFailure("repository_setup_invalid");
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        await using IDbContextTransaction transaction = await ApplicationTransaction.BeginAsync(db, token);
        WorkSnapshot work = await CurrentAsync(db, attemptId, token);
        AttemptSnapshot attempt = work.Attempts[^1];
        GitRepositoryGrant grant = attempt.Target.GitRepository!.Grant!;
        if (attempt.TurnNumber != request.TurnNumber || !await db.WorkspaceCheckpoints.AnyAsync(x =>
            x.Id == request.CheckpointId && x.WorkId == work.Id && x.AttemptId == attemptId &&
            x.TurnNumber == request.TurnNumber && x.WorkspaceNumber == attempt.WorkspaceNumber &&
            x.GitRepository == attempt.Target.GitRepository.GitRepository && x.Branch == grant.Branch, token))
            throw new ApplicationFailure("repository_setup_unconfirmed");
        Row[] previous = await db.GitRepositorySetupMemories.Where(x => x.AttemptId == attemptId && x.TurnNumber == request.TurnNumber).ToArrayAsync(token);
        if (previous.Length > 0)
        {
            if (previous.Length != request.Observations.Length || request.Observations.Any(observation => !previous.Any(row =>
                row.Topic == observation.Setup.Topic && row.CheckpointId == request.CheckpointId && row.Environment == request.Environment &&
                JsonSerializer.Serialize(JsonSerializer.Deserialize<VerifiedGitRepositorySetup>(row.Observation, ContractJson.Options), ContractJson.Options) ==
                JsonSerializer.Serialize(observation, ContractJson.Options))))
                throw new ApplicationFailure("repository_setup_changed");
            return;
        }
        foreach (VerifiedGitRepositorySetup observation in request.Observations)
        {
            string fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            { observation.ConfigurationHash, Files = observation.Files.OrderBy(x => x.Path, StringComparer.Ordinal) }, ContractJson.Options)));
            db.GitRepositorySetupMemories.Add(new()
            {
                GitRepositoryId = grant.GitRepositoryId,
                GithubConnectionId = grant.ConnectionId,
                AccountId = grant.AccountId,
                CheckpointId = request.CheckpointId,
                WorkId = work.Id,
                AttemptId = attemptId,
                TurnNumber = request.TurnNumber,
                Topic = observation.Setup.Topic,
                Environment = request.Environment,
                Fingerprint = fingerprint,
                Observation = JsonSerializer.Serialize(observation, ContractJson.Options),
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
            if (buffer.Length + count > GitRepositorySetupRules.MaxPayloadBytes) throw new ApplicationFailure("repository_setup_invalid");
            await buffer.WriteAsync(chunk.AsMemory(0, count), token);
        }
        SetupMemoryWrite request;
        try { request = JsonSerializer.Deserialize<SetupMemoryWrite>(buffer.ToArray(), ContractJson.Options) ?? throw new JsonException(); }
        catch (JsonException) { throw new ApplicationFailure("repository_setup_invalid"); }
        await SaveAsync(attemptId, request, token);
    }

    private static async Task<WorkSnapshot> CurrentAsync(GoblinDbContext db, long attemptId, CancellationToken token)
    {
        var owner = await db.ExecutionAttempts.AsNoTracking().Where(x => x.Id == attemptId)
            .Select(x => new { x.Work.State, x.Status, x.TurnNumber }).SingleOrDefaultAsync(token);
        if (owner is null || owner.Status is not ("Starting" or "Running") || owner.State is null)
            throw new ApplicationFailure("repository_setup_unavailable");
        WorkSnapshot work = JsonSerializer.Deserialize<WorkSnapshot>(owner.State, ContractJson.Options)!;
        AttemptSnapshot attempt = work.Attempts[^1];
        GitRepositoryGrant? grant = attempt.Target.GitRepository?.Grant;
        if (attempt.Id != attemptId || attempt.TurnNumber != owner.TurnNumber || grant is null ||
            !await db.GithubRepositories.AnyAsync(x => x.Id == grant.GitRepositoryId && x.ConnectionId == grant.ConnectionId &&
                x.Name == attempt.Target.GitRepository!.GitRepository && x.Enabled && x.Connection.AccountId == grant.AccountId &&
                x.Connection.Generation == grant.Generation && x.Connection.Availability == "Connected", token))
            throw new ApplicationFailure("repository_setup_unavailable");
        return work;
    }
}
