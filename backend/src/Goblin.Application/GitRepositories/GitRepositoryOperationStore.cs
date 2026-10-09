using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.Work;
using Goblin.Contracts;
using Goblin.Core.Work;
using Goblin.Persistence;
using Goblin.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace Goblin.Application.GitRepositories;

internal sealed class GitRepositoryOperationSnapshot
{
    internal GitRepositoryOperationSnapshot() { }

    internal required long Id { get; init; }
    internal required long AttemptId { get; init; }
    internal required GitRepositoryOperationKind Kind { get; init; }
    internal required GitRepositoryOperationState State { get; init; }
    internal string? Commit { get; init; }
    internal string? Url { get; init; }
}

// Owns durable operation admission, dispatch and history. Runtime/network calls
// use snapshots and never retain a database context or tracked entity.
public sealed class GitRepositoryOperationStore
{
    private readonly IDbContextFactory<GoblinDbContext> _factory;
    private readonly IServiceScopeFactory _scopes;

    public GitRepositoryOperationStore(IDbContextFactory<GoblinDbContext> factory, IServiceScopeFactory scopes)
    {
        _factory = factory;
        _scopes = scopes;
    }

    internal async Task<long> ReserveIdAsync(CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        return await IdentitySequence.NextAsync(db, IdentityKind.GitRepositoryOperation, token);
    }

    internal async Task<GitRepositoryOperationView> EnqueueAsync(GitRepositoryOperationUpload upload, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        await using IDbContextTransaction transaction = await ApplicationTransaction.BeginAsync(db, token);
        GitRepositoryOperation? previous = await db.GitRepositoryOperations.SingleOrDefaultAsync(x => x.Id == upload.Id, token);
        if (previous is not null)
        {
            if (previous.AttemptId != upload.AttemptId || previous.Fingerprint != upload.Fingerprint)
                throw new ApplicationFailure("command_id_reused");
            return View(previous);
        }
        Persistence.Entities.ExecutionAttempt attempt = await db.ExecutionAttempts.SingleAsync(x => x.Id == upload.AttemptId, token);
        if (!GitRepositoryAttemptPolicy.AllowsOperations(attempt.Status) ||
            await db.GitRepositoryOperations.AnyAsync(x => x.AttemptId == upload.AttemptId &&
                (x.State == nameof(GitRepositoryOperationState.Queued) || x.State == nameof(GitRepositoryOperationState.Running) ||
                    x.State == nameof(GitRepositoryOperationState.Uncertain) || x.State == nameof(GitRepositoryOperationState.Failed)), token))
            throw new ApplicationFailure("repository_operation_unavailable");
        // Preserve ordering: duplicates cannot overwrite the retained bundle,
        // and no committed dispatch may refer to an upload that is still temporary.
        File.Move(upload.UploadPath, upload.BundlePath, true);
        db.GitRepositoryOperations.Add(new()
        {
            Id = upload.Id,
            AttemptId = upload.AttemptId,
            Kind = upload.Kind.WireValue(),
            State = nameof(GitRepositoryOperationState.Queued),
            Fingerprint = upload.Fingerprint,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        using IServiceScope scope = _scopes.CreateScope();
        IDbContextOutbox outbox = scope.ServiceProvider.GetRequiredService<WorkOutboxFactory>().Create(db);
        await outbox.PublishAsync(new ExecuteGitRepositoryOperation(upload.Id));
        await outbox.SaveChangesAndFlushMessagesAsync(token);
        return new(upload.Id, GitRepositoryOperationState.Queued, null);
    }

    internal async Task<GitRepositoryOperationView> StatusAsync(long attemptId, long id, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        GitRepositoryOperation row = await db.GitRepositoryOperations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.AttemptId == attemptId, token)
            ?? throw new ApplicationFailure("repository_operation_unavailable");
        return View(row);
    }

    internal async Task<GitRepositoryOperationSnapshot?> TryStartAsync(long id, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        await using IDbContextTransaction transaction = await ApplicationTransaction.BeginAsync(db, token);
        GitRepositoryOperation row = await db.GitRepositoryOperations.SingleAsync(x => x.Id == id, token);
        if (row.State != nameof(GitRepositoryOperationState.Queued)) return null;
        row.State = nameof(GitRepositoryOperationState.Running);
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return Snapshot(row);
    }

    internal async Task<GitRepositoryOperationSnapshot> RequireRunningAsync(long id, CancellationToken token)
    {
        GitRepositoryOperationSnapshot operation = await ReadAsync(id, token);
        if (operation.State != GitRepositoryOperationState.Running)
            throw new ApplicationFailure("repository_operation_unavailable");
        return operation;
    }

    internal async Task RecordInspectedCommitAsync(long id, string commit, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        GitRepositoryOperation row = await db.GitRepositoryOperations.SingleAsync(x => x.Id == id, token);
        row.CommitSha = commit;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(token);
    }

    internal async Task CompleteAsync(long id, string? url, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        GitRepositoryOperation row = await db.GitRepositoryOperations.SingleAsync(x => x.Id == id, token);
        row.State = nameof(GitRepositoryOperationState.Succeeded);
        row.ResultUrl = url;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(token);
    }

    internal Task FailBeforeLaunchAsync(long id, CancellationToken token) =>
        RecordFailureAsync(id, GitRepositoryOperationState.Failed, token);

    internal Task LoseExternalResponseAsync(long id, CancellationToken token) =>
        RecordFailureAsync(id, GitRepositoryOperationState.Uncertain, token);

    private async Task RecordFailureAsync(long id, GitRepositoryOperationState state, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        await db.GitRepositoryOperations.Where(x => x.Id == id).ExecuteUpdateAsync(s =>
            s.SetProperty(x => x.State, state.ToString()).SetProperty(x => x.UpdatedAt, DateTime.UtcNow), token);
    }

    internal async Task<GitRepositoryOperationSnapshot[]> IncompleteAsync(long attemptId, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        GitRepositoryOperation[] rows = await db.GitRepositoryOperations.AsNoTracking()
            .Where(x => x.AttemptId == attemptId && x.State != nameof(GitRepositoryOperationState.Succeeded)).ToArrayAsync(token);
        return [.. rows.Select(Snapshot)];
    }

    internal async Task<GitRepositoryOperationSnapshot> ReadAsync(long id, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        return Snapshot(await db.GitRepositoryOperations.AsNoTracking().SingleAsync(x => x.Id == id, token));
    }

    internal async Task RecoverDispatchFailureAsync(long id, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        GitRepositoryOperation row = await db.GitRepositoryOperations.SingleAsync(x => x.Id == id, token);
        row.State = row.CommitSha is null ? nameof(GitRepositoryOperationState.Failed) : nameof(GitRepositoryOperationState.Uncertain);
        await db.SaveChangesAsync(token);
    }

    internal async Task RecordReconciliationAsync(long id, GitRepositoryOperationState state, string? url, CancellationToken token)
    {
        if (state is not (GitRepositoryOperationState.Succeeded or GitRepositoryOperationState.Failed or GitRepositoryOperationState.Uncertain))
            throw new ArgumentOutOfRangeException(nameof(state));
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        GitRepositoryOperation row = await db.GitRepositoryOperations.SingleAsync(x => x.Id == id, token);
        row.State = state.ToString();
        row.ResultUrl = url;
        await db.SaveChangesAsync(token);
    }

    internal async Task FailQueuedAsync(long attemptId, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        await db.GitRepositoryOperations.Where(x => x.AttemptId == attemptId && x.State == nameof(GitRepositoryOperationState.Queued))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, nameof(GitRepositoryOperationState.Failed)), token);
    }

    internal async Task<bool> HasUnresolvedExecutionAsync(long attemptId, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        return await db.GitRepositoryOperations.AnyAsync(x => x.AttemptId == attemptId &&
            (x.State == nameof(GitRepositoryOperationState.Running) || x.State == nameof(GitRepositoryOperationState.Uncertain)), token);
    }

    internal async Task<bool> HasCheckpointAsync(long attemptId, string commit, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        return await db.GitRepositoryOperations.AnyAsync(x => x.AttemptId == attemptId &&
            (x.Kind == GitRepositoryOperationKind.Publish.WireValue() || x.Kind == GitRepositoryOperationKind.Checkpoint.WireValue()) &&
            x.State == nameof(GitRepositoryOperationState.Succeeded) && x.CommitSha == commit, token);
    }

    private static GitRepositoryOperationView View(GitRepositoryOperation row) =>
        new(row.Id, ContractValue.Parse<GitRepositoryOperationState>(row.State), row.ResultUrl);

    private static GitRepositoryOperationSnapshot Snapshot(GitRepositoryOperation row) => new()
    {
        Id = row.Id,
        AttemptId = row.AttemptId,
        Kind = GitRepositoryOperationNames.Parse(row.Kind),
        State = ContractValue.Parse<GitRepositoryOperationState>(row.State),
        Commit = row.CommitSha,
        Url = row.ResultUrl
    };
}
