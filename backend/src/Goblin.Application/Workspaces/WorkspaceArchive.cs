using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.Repositories;
using Goblin.Application.Work;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;
using Goblin.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Goblin.Application.Workspaces;

// PostgreSQL owns checkpoint bytes and metadata in one commit. A failed upload
// cannot make a local workspace disposable. Archives are never extracted here.
public sealed class WorkspaceArchive : IWorkspaceArchive
{
    private readonly IDbContextFactory<GoblinDbContext> _factory;
    private readonly IServiceScopeFactory _scopes;
    private readonly WorkspaceLimits _limits;
    public WorkspaceArchive(IDbContextFactory<GoblinDbContext> factory, IServiceScopeFactory scopes, WorkspaceLimits limits)
    { _factory = factory; _scopes = scopes; _limits = limits; }

    public async Task<WorkspaceCheckpoint?> LatestAsync(long workId, string repository, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        WorkspaceCheckpoint? row = await db.WorkspaceCheckpoints.AsNoTracking().Where(x => x.WorkId == workId && x.Repository == repository)
            .OrderByDescending(x => x.CreatedAt).Select(x => new WorkspaceCheckpoint(x.Id, x.WorkId, x.AttemptId,
                x.TurnNumber, x.WorkspaceNumber, x.Repository, x.Branch, x.CommitSha, x.CreatedAt)).FirstOrDefaultAsync(token);
        return row;
    }
    public async Task<bool> VerifiedAsync(long id, long attemptId, int turnNumber, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        return await db.WorkspaceCheckpoints.AnyAsync(x => x.Id == id && x.AttemptId == attemptId && x.TurnNumber == turnNumber, token);
    }
    public async Task<bool> CanDiscardAsync(long attemptId, int workspaceNumber, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        Persistence.Entities.ExecutionAttempt? attempt = await db.ExecutionAttempts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == attemptId, token);
        if (attempt is null) return false;
        string? state = await db.WorkItems.Where(x => x.Id == attempt.WorkId).Select(x => x.State).SingleAsync(token);
        WorkSnapshot work = System.Text.Json.JsonSerializer.Deserialize<WorkSnapshot>(state!, WorkStore.Json)!;
        AttemptSnapshot execution = work.Attempts.Single(x => x.Id == attemptId);
        int latestTurn;
        if (execution.WorkspaceNumber == workspaceNumber)
        {
            if (attempt.Status is "Starting" or "Running" or "Uncertain" or "CancellationRequested" || attempt.CleanupPending || attempt.WorkspaceRetained) return false;
            latestTurn = execution.TurnNumber;
        }
        else latestTurn = execution.PriorTurns.Where(x => x.WorkspaceNumber == workspaceNumber).Select(x => x.Number).DefaultIfEmpty(0).Max();
        // An older checkpoint never authorizes discarding newer unsaved changes.
        return latestTurn > 0 && await db.WorkspaceCheckpoints.AnyAsync(x => x.AttemptId == attemptId && x.WorkspaceNumber == workspaceNumber && x.TurnNumber == latestTurn, token);
    }
    public async Task<WorkspaceCheckpoint> SaveAsync(long attemptId, int turn, string commit, Stream input,
        RepositoryBroker broker, CancellationToken token)
    {
        WorkSnapshot work = await broker.CurrentAsync(attemptId, token);
        AttemptSnapshot attempt = work.Attempts[^1];
        if (attempt.TurnNumber != turn || attempt.Status is not (AttemptStatus.Starting or AttemptStatus.Running)) throw new ApplicationFailure("workspace_changed");
        await broker.VerifyCheckpointAsync(work, commit, token);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[65536]; int count;
        while ((count = await input.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + count > _limits.MaxArchiveBytes) throw new ApplicationFailure("workspace_storage_full");
            await buffer.WriteAsync(chunk.AsMemory(0, count), token);
        }
        byte[] archive = buffer.ToArray();
        // Validate the container format and paths without trusting file extensions.
        Inspect(archive, null);
        string hash = Convert.ToHexString(SHA256.HashData(archive));
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        await using IDbContextTransaction transaction = await WorkStore.BeginAsync(db, token);
        Persistence.Entities.WorkspaceCheckpoint? previous = await db.WorkspaceCheckpoints.SingleOrDefaultAsync(x => x.AttemptId == attemptId && x.TurnNumber == turn, token);
        if (previous is not null)
        {
            if (previous.CommitSha != commit || previous.ArchiveSha256 != hash) throw new ApplicationFailure("workspace_changed");
            return View(previous);
        }
        Persistence.Entities.ExecutionAttempt owner = await db.ExecutionAttempts.SingleAsync(x => x.Id == attemptId, token);
        if (owner.TurnNumber != turn || owner.Status is not ("Starting" or "Running")) throw new ApplicationFailure("workspace_changed");
        long used = await db.WorkspaceCheckpoints.SumAsync(x => (long)x.Archive.Length, token);
        if (used + archive.Length > _limits.MaxStorageBytes) throw new ApplicationFailure("workspace_storage_full");
        var row = new Persistence.Entities.WorkspaceCheckpoint
        {
            WorkId = work.Id,
            AttemptId = attemptId,
            TurnNumber = turn,
            WorkspaceNumber = attempt.WorkspaceNumber,
            Repository = attempt.Target.Repository!.Repository,
            Branch = attempt.Target.Repository.Grant!.Branch,
            CommitSha = commit,
            ArchiveSha256 = hash,
            Archive = archive,
            CreatedAt = DateTime.UtcNow
        };
        db.WorkspaceCheckpoints.Add(row);
        await db.SaveChangesAsync(token); await transaction.CommitAsync(token);
        return View(row);
    }
    public async Task<WorkspaceCheckpoint[]> ListAsync(long workId, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        return await db.WorkspaceCheckpoints.AsNoTracking().Where(x => x.WorkId == workId).OrderByDescending(x => x.CreatedAt)
            .Select(x => new WorkspaceCheckpoint(x.Id, x.WorkId, x.AttemptId, x.TurnNumber, x.WorkspaceNumber, x.Repository, x.Branch, x.CommitSha, x.CreatedAt)).ToArrayAsync(token);
    }
    public async Task<byte[]> ReadAsync(long workId, long id, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        var row = await db.WorkspaceCheckpoints.AsNoTracking().Where(x => x.Id == id && x.WorkId == workId)
            .Select(x => new { x.Archive, x.ArchiveSha256 }).SingleOrDefaultAsync(token) ?? throw new ApplicationFailure("workspace_not_found");
        if (Convert.ToHexString(SHA256.HashData(row.Archive)) != row.ArchiveSha256) throw new ApplicationFailure("workspace_unavailable");
        return row.Archive;
    }
    private static WorkspaceCheckpoint View(Persistence.Entities.WorkspaceCheckpoint x) =>
        new(x.Id, x.WorkId, x.AttemptId, x.TurnNumber, x.WorkspaceNumber, x.Repository, x.Branch, x.CommitSha, x.CreatedAt);

    public static object Inspect(byte[] archive, string? requested)
    {
        using var gzip = new GZipStream(new MemoryStream(archive), CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        var files = new System.Collections.Generic.List<object>();
        long total = 0; int entries = 0;
        TarEntry? entry;
        while ((entry = tar.GetNextEntry()) is not null)
        {
            string name = entry.Name.StartsWith("./", StringComparison.Ordinal) ? entry.Name[2..] : entry.Name;
            if (++entries > 100000 || (total += entry.Length) > 4L * 1024 * 1024 * 1024 ||
                entry.Name.StartsWith('/') || entry.Name.Split('/').Contains("..")) throw new ApplicationFailure("workspace_archive_invalid");
            if (entry.EntryType == TarEntryType.SymbolicLink)
            {
                string target = Path.GetFullPath(Path.Combine("/workspace", Path.GetDirectoryName(name) ?? "", entry.LinkName));
                if (Path.IsPathRooted(entry.LinkName) || !target.StartsWith("/workspace/", StringComparison.Ordinal)) throw new ApplicationFailure("workspace_archive_invalid");
            }
            else if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory))
                throw new ApplicationFailure("workspace_archive_invalid");
            if (name.StartsWith("repository/.git/", StringComparison.Ordinal) || name == "repository/.git") continue;
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
            if (requested is null)
            {
                if (files.Count < 10000) files.Add(new { path = name, size = entry.Length });
            }
            else if (name == requested)
            {
                if (entry.Length > 1024 * 1024) throw new ApplicationFailure("workspace_file_too_large");
                using var reader = new StreamReader(entry.DataStream!);
                return new { path = name, text = reader.ReadToEnd() };
            }
        }
        if (requested is not null) throw new ApplicationFailure("workspace_not_found");
        return new { files = files.ToArray(), truncated = files.Count == 10000 };
    }
}
