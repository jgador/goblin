using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.Work;
using Goblin.Contracts;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;
using Goblin.Persistence;
using Goblin.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace Goblin.Application.GitRepositories;

public sealed class GitRepositoryBrokerOptions
{
    public GitRepositoryBrokerOptions(string directory)
    {
        Directory = directory;
    }

    public string Directory { get; init; }
}

public sealed class ExecuteGitRepositoryOperation
{
    public ExecuteGitRepositoryOperation(long id)
    {
        Id = id;
    }

    public long Id { get; init; }
}

public sealed class GitRepositoryOperationView
{
    public GitRepositoryOperationView(long id, GitRepositoryOperationState state, string? url)
    {
        Id = id;
        State = state;
        Url = url;
    }

    public long Id { get; init; }

    public GitRepositoryOperationState State { get; init; }

    public string? Url { get; init; }
}

public sealed class GitRepositoryBroker : IGitRepositoryBroker
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IDbContextFactory<GoblinDbContext> _factory;
    private readonly IGitRepositoryRemote _remote;
    private readonly IWorkspaceCheckpoints? _checkpoints;
    private readonly string _directory;
    private readonly byte[] _key;
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _active = new();

    public GitRepositoryBroker(IServiceScopeFactory scopes, IDbContextFactory<GoblinDbContext> factory,
        IGitRepositoryRemote remote, GitRepositoryBrokerOptions options, IWorkspaceCheckpoints? checkpoints = null)
    {
        _scopes = scopes; _factory = factory; _remote = remote; _checkpoints = checkpoints;
        _directory = Path.GetFullPath(options.Directory);
        Directory.CreateDirectory(_directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string key = Path.Combine(_directory, "capability-key");
        if (!File.Exists(key))
        {
            var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using FileStream file = new(key, fileOptions);
            file.Write(RandomNumberGenerator.GetBytes(32));
        }
        _key = File.ReadAllBytes(key);
    }

    private string DirectoryFor(long attemptId) => Path.Combine(_directory, attemptId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private string BundleFor(long attemptId, long id) => Path.Combine(DirectoryFor(attemptId), id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".bundle");

    private string Capability(WorkSnapshot work)
    {
        AttemptSnapshot attempt = work.Attempts[^1];
        return Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{work.Id}/{attempt.Id}/{attempt.Target.GitRepository!.Grant!.Generation}")));
    }

    private async Task<WorkSnapshot> WorkAsync(long attemptId, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        Persistence.Entities.ExecutionAttempt attempt = await db.ExecutionAttempts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == attemptId, token)
            ?? throw new ApplicationFailure("repository_operation_unavailable");
        using IServiceScope scope = _scopes.CreateScope();
        WorkSnapshot work = (await scope.ServiceProvider.GetRequiredService<WorkStore>().GetAsync(attempt.WorkId, token)).Work;
        if (work.Attempts[^1].Id != attemptId || work.Attempts[^1].Target.GitRepository?.Grant is null)
            throw new ApplicationFailure("repository_operation_unavailable");
        GitRepositoryGrant grant = work.Attempts[^1].Target.GitRepository!.Grant!;
        if (grant.PolicyVersion == 2 && (work.GitRepositoryAuthorization is not { Status: GitRepositoryAuthorizationStatus.Authorized } approved ||
            approved.Id != attemptId || approved.Target != work.Attempts[^1].Target) ||
            !await db.GithubRepositories.AnyAsync(x => x.Id == grant.GitRepositoryId && x.Enabled &&
                x.ConnectionId == grant.ConnectionId && x.Connection.Generation == grant.Generation &&
                x.Connection.AccountId == grant.AccountId && x.Connection.Availability == nameof(GitHubConnectionStatus.Connected), token))
            throw new ApplicationFailure("repository_operation_unavailable");
        return work;
    }

    public Task<WorkSnapshot> CurrentAsync(long attemptId, CancellationToken token) => WorkAsync(attemptId, token);

    public async Task VerifyCheckpointAsync(WorkSnapshot work, string commit, CancellationToken token)
    {
        AttemptSnapshot attempt = work.Attempts[^1];
        if (string.IsNullOrEmpty(commit) || commit.Length != 40 || !commit.All(Uri.IsHexDigit)) throw new ApplicationFailure("workspace_changed");
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        if (!await db.GitRepositoryOperations.AnyAsync(x => x.AttemptId == attempt.Id && (x.Kind == GitRepositoryOperationKind.Publish.WireValue() || x.Kind == GitRepositoryOperationKind.Checkpoint.WireValue()) && x.State == nameof(GitRepositoryOperationState.Succeeded) && x.CommitSha == commit, token) ||
            (attempt.Target.GitRepository!.Grant!.PolicyVersion == 1 || attempt.Target.GitRepository.Grant.AllowPush) && await _remote.ReconcileAsync(attempt.Target.GitRepository!, DirectoryFor(attempt.Id), GitRepositoryOperationKind.Publish, commit, token) is null)
            throw new ApplicationFailure("workspace_checkpoint_unconfirmed");
    }

    public async Task AuthorizeAsync(long attemptId, string capability, bool write, CancellationToken token)
    {
        WorkSnapshot work = await WorkAsync(attemptId, token);
        byte[] supplied;
        try { supplied = Convert.FromHexString(capability); } catch { throw new ApplicationFailure("repository_operation_unavailable"); }
        if (!CryptographicOperations.FixedTimeEquals(supplied, Convert.FromHexString(Capability(work))))
            throw new ApplicationFailure("repository_operation_unavailable");
        AttemptSnapshot attempt = work.Attempts[^1];
        if (write && attempt.Status is not (AttemptStatus.Starting or AttemptStatus.Running))
            throw new ApplicationFailure("repository_operation_unavailable");
    }

    public async Task<string> PrepareAsync(WorkSnapshot work, CancellationToken token)
    {
        AttemptSnapshot attempt = work.Attempts[^1];
        GitRepositoryChange gitRepository = attempt.Target.GitRepository!;
        if (gitRepository.Grant is null) throw new ApplicationFailure("repository_unavailable");
        gitRepository.Grant.Authorize(work.Id, attempt.Id, gitRepository.GitRepository, gitRepository.GitRepository, gitRepository.Grant.Branch, GitRepositoryOperationKind.Fetch);
        string? checkpoint = work.Attempts.SkipLast(1).LastOrDefault(x => x.Target.GitRepository?.GitRepository == gitRepository.GitRepository &&
            work.Artifacts.Any(a => a.AttemptId == x.Id))?.Target.GitRepository?.Grant?.Branch;
        Goblin.Contracts.Runtime.WorkspaceCheckpoint? saved = _checkpoints is null ? null : await _checkpoints.LatestAsync(work.Id, gitRepository.GitRepository, token);
        bool published = saved is not null && work.Attempts.Any(x => x.Id == saved.AttemptId &&
            (x.Target.GitRepository?.Grant?.PolicyVersion == 1 || x.Target.GitRepository?.Grant?.AllowPush == true));
        if (saved is not null && published) await _remote.PrepareCheckpointAsync(gitRepository, DirectoryFor(attempt.Id), saved, token);
        else await _remote.PrepareAsync(gitRepository, DirectoryFor(attempt.Id), work.Workspace is not null ? null : checkpoint, token);
        return Capability(work);
    }

    public string InputPath(long attemptId) => Path.Combine(DirectoryFor(attemptId), "input.bundle");

    public async Task<long> ReserveOperationIdAsync(long attemptId, CancellationToken token)
    {
        WorkSnapshot work = await WorkAsync(attemptId, token);
        if (work.Attempts[^1].Status is not (AttemptStatus.Starting or AttemptStatus.Running))
            throw new ApplicationFailure("repository_operation_unavailable");
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        return await IdentityStore.NextAsync(db, IdentityKind.GitRepositoryOperation, token);
    }

    public async Task<GitRepositoryOperationView> EnqueueAsync(long attemptId, long id, GitRepositoryOperationKind kind, Stream input, CancellationToken token)
    {
        if (id <= 0) throw new ApplicationFailure("repository_operation_unavailable");
        WorkSnapshot work = await WorkAsync(attemptId, token);
        GitRepositoryChange gitRepository = work.Attempts[^1].Target.GitRepository!;
        gitRepository.Grant!.Authorize(work.Id, attemptId, gitRepository.GitRepository, gitRepository.GitRepository, gitRepository.Grant.Branch, kind);
        string temporary = Path.Combine(DirectoryFor(attemptId), Guid.NewGuid().ToString("N") + ".upload");
        string fingerprint;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[65536]; long length = 0; int count;
                while ((count = await input.ReadAsync(buffer, token)) != 0)
                {
                    length += count;
                    if (length > 128 * 1024 * 1024) throw new ApplicationFailure("repository_operation_unavailable");
                    hash.AppendData(buffer, 0, count); await file.WriteAsync(buffer.AsMemory(0, count), token);
                }
            }
            fingerprint = kind.WireValue() + ":" + Convert.ToHexString(hash.GetHashAndReset());
            await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
            await using IDbContextTransaction transaction = await WorkStore.BeginAsync(db, token);
            GitRepositoryOperation? previous = await db.GitRepositoryOperations.SingleOrDefaultAsync(x => x.Id == id, token);
            if (previous is not null)
            {
                if (previous.AttemptId != attemptId || previous.Fingerprint != fingerprint) throw new ApplicationFailure("command_id_reused");
                return new(previous.Id, ContractValue.Parse<GitRepositoryOperationState>(previous.State), previous.ResultUrl);
            }
            Persistence.Entities.ExecutionAttempt attempt = await db.ExecutionAttempts.SingleAsync(x => x.Id == attemptId, token);
            if (attempt.Status is not (nameof(AttemptStatus.Starting) or nameof(AttemptStatus.Running)) || await db.GitRepositoryOperations.AnyAsync(x => x.AttemptId == attemptId &&
                (x.State == nameof(GitRepositoryOperationState.Queued) || x.State == nameof(GitRepositoryOperationState.Running) || x.State == nameof(GitRepositoryOperationState.Uncertain) || x.State == nameof(GitRepositoryOperationState.Failed)), token))
                throw new ApplicationFailure("repository_operation_unavailable");
            File.Move(temporary, BundleFor(attemptId, id), true);
            db.GitRepositoryOperations.Add(new() { Id = id, AttemptId = attemptId, Kind = kind.WireValue(), State = nameof(GitRepositoryOperationState.Queued), Fingerprint = fingerprint, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            using IServiceScope scope = _scopes.CreateScope();
            IDbContextOutbox outbox = scope.ServiceProvider.GetRequiredService<WorkOutboxFactory>().Create(db);
            await outbox.PublishAsync(new ExecuteGitRepositoryOperation(id));
            await outbox.SaveChangesAndFlushMessagesAsync(token);
            return new(id, GitRepositoryOperationState.Queued, null);
        }
        finally { File.Delete(temporary); }
    }

    public async Task<GitRepositoryOperationView> StatusAsync(long attemptId, long id, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        GitRepositoryOperation operation = await db.GitRepositoryOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.AttemptId == attemptId, token)
            ?? throw new ApplicationFailure("repository_operation_unavailable");
        return new(id, ContractValue.Parse<GitRepositoryOperationState>(operation.State), operation.ResultUrl);
    }

    public async Task ExecuteAsync(long id, CancellationToken token)
    {
        try { await ExecuteCoreAsync(id, token); }
        catch
        {
            // Preserve failed dispatch evidence through a database outage. Never replay it.
            await File.WriteAllTextAsync(Path.Combine(_directory, id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".failed"), "failed", CancellationToken.None);
            throw;
        }
    }

    private async Task ExecuteCoreAsync(long id, CancellationToken token)
    {
        long attemptId;
        await using (GoblinDbContext db = await _factory.CreateDbContextAsync(token))
        {
            await using IDbContextTransaction transaction = await WorkStore.BeginAsync(db, token);
            GitRepositoryOperation row = await db.GitRepositoryOperations.SingleAsync(x => x.Id == id, token);
            if (row.State != nameof(GitRepositoryOperationState.Queued)) return;
            row.State = nameof(GitRepositoryOperationState.Running); row.UpdatedAt = DateTime.UtcNow; attemptId = row.AttemptId;
            await db.SaveChangesAsync(token); await transaction.CommitAsync(token);
        }
        SemaphoreSlim gate = _locks.GetOrAdd(attemptId, _ => new(1));
        await gate.WaitAsync(token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        cancellation.CancelAfter(TimeSpan.FromMinutes(2));
        _active[attemptId] = cancellation;
        bool external = false;
        try
        {
            WorkSnapshot work = await WorkAsync(attemptId, cancellation.Token);
            if (work.Attempts[^1].Status is not (AttemptStatus.Starting or AttemptStatus.Running)) throw new ApplicationFailure("repository_operation_unavailable");
            GitRepositoryChange gitRepository = work.Attempts[^1].Target.GitRepository!;
            await using GoblinDbContext db = await _factory.CreateDbContextAsync(cancellation.Token);
            GitRepositoryOperation row = await db.GitRepositoryOperations.SingleAsync(x => x.Id == id, cancellation.Token);
            if (row.State != nameof(GitRepositoryOperationState.Running)) throw new ApplicationFailure("repository_operation_unavailable");
            gitRepository.Grant!.Authorize(work.Id, attemptId, gitRepository.GitRepository, gitRepository.GitRepository, gitRepository.Grant.Branch, GitRepositoryOperationNames.Parse(row.Kind));
            string commit = row.Kind == "fetch" ? "read" : await _remote.InspectBundleAsync(gitRepository, DirectoryFor(attemptId), BundleFor(attemptId, id), cancellation.Token);
            row.CommitSha = commit; row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellation.Token);
            if (row.Kind == GitRepositoryOperationKind.Checkpoint.WireValue())
            {
                row.State = nameof(GitRepositoryOperationState.Succeeded);
                await db.SaveChangesAsync(cancellation.Token);
                return;
            }
            await File.WriteAllLinesAsync(Path.Combine(_directory, id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".external"),
                [File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim(), File.ReadAllText("/proc/uptime").Split(' ')[0]], cancellation.Token);
            external = true;
            GitRepositoryOperationResult result = await _remote.ExecuteAsync(gitRepository, DirectoryFor(attemptId), GitRepositoryOperationNames.Parse(row.Kind), commit, cancellation.Token);
            row.State = nameof(GitRepositoryOperationState.Succeeded); row.ResultUrl = result.Url; row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellation.Token);
        }
        catch
        {
            await using GoblinDbContext db = await _factory.CreateDbContextAsync();
            await db.GitRepositoryOperations.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, external ? nameof(GitRepositoryOperationState.Uncertain) : nameof(GitRepositoryOperationState.Failed)).SetProperty(x => x.UpdatedAt, DateTime.UtcNow));
        }
        finally { _active.TryRemove(attemptId, out _); gate.Release(); }
    }

    public async Task<ExecutionObservation?> ObserveAsync(WorkSnapshot work, CancellationToken token)
    {
        AttemptSnapshot attempt = work.Attempts[^1];
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        GitRepositoryOperation[] rows = await db.GitRepositoryOperations.Where(x => x.AttemptId == attempt.Id && x.State != nameof(GitRepositoryOperationState.Succeeded)).ToArrayAsync(token);
        if (rows.Length == 0) return null;
        foreach (GitRepositoryOperation? row in rows)
        {
            string evidence = Path.Combine(_directory, row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".failed");
            if (File.Exists(evidence))
            {
                row.State = row.CommitSha is null ? nameof(GitRepositoryOperationState.Failed) : nameof(GitRepositoryOperationState.Uncertain);
                await db.SaveChangesAsync(token);
                File.Delete(evidence);
            }
            if (_active.ContainsKey(attempt.Id) || row.State == nameof(GitRepositoryOperationState.Queued)) return new(ObservationKind.Pending);
            await db.Entry(row).ReloadAsync(token);
            if (row.State == nameof(GitRepositoryOperationState.Succeeded)) continue;
            if (row.Kind == GitRepositoryOperationKind.Checkpoint.WireValue() && row.State is nameof(GitRepositoryOperationState.Running) or nameof(GitRepositoryOperationState.Uncertain))
            {
                row.State = nameof(GitRepositoryOperationState.Failed); await db.SaveChangesAsync(token); continue;
            }
            if (row.State is nameof(GitRepositoryOperationState.Running) or nameof(GitRepositoryOperationState.Uncertain))
            {
                row.State = nameof(GitRepositoryOperationState.Uncertain);
                GitRepositoryOperationResult? result = null;
                try
                {
                    if (row.CommitSha is not null)
                        result = await _remote.ReconcileAsync(attempt.Target.GitRepository!, DirectoryFor(attempt.Id), GitRepositoryOperationNames.Parse(row.Kind), row.CommitSha, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch
                {
                    // Revoked access must not prevent proving the publisher stopped.
                    // Failure retains the uncertain history; it does not assert that
                    // GitHub was unchanged or automatically repeat the operation.
                }
                if (result is not null) { row.State = nameof(GitRepositoryOperationState.Succeeded); row.ResultUrl = result.Url; }
                // Each external subprocess has a 120s watchdog; the entire operation also has
                // a 120s limit. After a controller crash no child can survive this bound.
                else if (ExternalProcessStopped(row.Id)) row.State = nameof(GitRepositoryOperationState.Failed);
                await db.SaveChangesAsync(token);
            }
        }
        return rows.All(x => x.State == nameof(GitRepositoryOperationState.Succeeded)) ? null : new(ObservationKind.Uncertain, Failure: FailureKind.ExecutionFailed);
    }

    private bool ExternalProcessStopped(long id)
    {
        string marker = Path.Combine(_directory, id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".external");
        if (!File.Exists(marker)) return true; // The external launch was never authorized.
        if (!OperatingSystem.IsLinux()) return false;
        string[] lifetime = File.ReadAllLines(marker);
        if (lifetime.Length != 2) return false;
        if (lifetime[0] != File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim()) return true;
        return double.TryParse(lifetime[1], System.Globalization.CultureInfo.InvariantCulture, out double started) &&
            double.TryParse(File.ReadAllText("/proc/uptime").Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture, out double now) && now - started > 300;
    }

    public async Task StopAsync(WorkSnapshot work, CancellationToken token)
    {
        long attemptId = work.Attempts[^1].Id;
        if (_active.TryGetValue(attemptId, out CancellationTokenSource? cancellation)) cancellation.Cancel();
        SemaphoreSlim gate = _locks.GetOrAdd(attemptId, _ => new(1));
        await gate.WaitAsync(token);
        try
        {
            await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
            await db.GitRepositoryOperations.Where(x => x.AttemptId == attemptId && x.State == nameof(GitRepositoryOperationState.Queued))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, nameof(GitRepositoryOperationState.Failed)), token);
            if ((await ObserveAsync(work, token))?.Kind == ObservationKind.Uncertain &&
                await db.GitRepositoryOperations.AnyAsync(x => x.AttemptId == attemptId && (x.State == nameof(GitRepositoryOperationState.Running) || x.State == nameof(GitRepositoryOperationState.Uncertain)), token))
                throw new ApplicationFailure("repository_operation_unavailable");
        }
        finally { gate.Release(); }
    }

    public async Task ReleaseAsync(WorkSnapshot work, CancellationToken token)
    {
        await StopAsync(work, token);
        // Called only after sandbox termination is confirmed. Published GitHub
        // checkpoints and the retained sandbox PVC own recovery; discard duplicates.
        string directory = DirectoryFor(work.Attempts[^1].Id);
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}

public static class ExecuteGitRepositoryOperationHandler
{
    public static Task Handle(ExecuteGitRepositoryOperation command, GitRepositoryBroker broker, CancellationToken token) => broker.ExecuteAsync(command.Id, token);
}
