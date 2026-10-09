using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.Work;
using Goblin.Contracts;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;
using Goblin.Persistence;
using Microsoft.EntityFrameworkCore;

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
    private readonly IDbContextFactory<GoblinDbContext> _factory;
    private readonly GitRepositoryOperationStore _operations;
    private readonly IGitRepositoryRemote _remote;
    private readonly IWorkspaceCheckpoints? _checkpoints;
    private readonly string _directory;
    private readonly GitRepositoryCapability _capability;
    private readonly GitRepositoryOperationEvidence _evidence;
    private readonly GitRepositoryOperationUploadStager _uploads;
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _active = new();

    public GitRepositoryBroker(IDbContextFactory<GoblinDbContext> factory, GitRepositoryOperationStore operations,
        IGitRepositoryRemote remote, GitRepositoryBrokerOptions options, IWorkspaceCheckpoints? checkpoints = null)
    {
        _factory = factory; _operations = operations; _remote = remote; _checkpoints = checkpoints;
        _directory = Path.GetFullPath(options.Directory);
        Directory.CreateDirectory(_directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _capability = new(Path.Combine(_directory, "capability-key"));
        _evidence = new(_directory);
        _uploads = new(_directory);
    }

    private string Capability(WorkSnapshot work)
    {
        AttemptSnapshot attempt = work.Attempts[^1];
        return _capability.Issue(work.Id, attempt.Id, attempt.Target.GitRepository!.Grant!.Generation);
    }

    private async Task<WorkSnapshot> WorkAsync(long attemptId, CancellationToken token)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        GitRepositoryWorkOwner owner = await GitRepositoryWorkQuery.ForAttempt(db.ExecutionAttempts.AsNoTracking(), attemptId)
            .SingleOrDefaultAsync(token)
            ?? throw new ApplicationFailure("repository_operation_unavailable");
        WorkSnapshot work = owner.Snapshot();
        await GitRepositoryAccess.RequireOperationAsync(db, work, attemptId, token);
        return work;
    }

    public Task<WorkSnapshot> CurrentAsync(long attemptId, CancellationToken token) => WorkAsync(attemptId, token);

    public async Task VerifyCheckpointAsync(WorkSnapshot work, string commit, CancellationToken token)
    {
        AttemptSnapshot attempt = work.Attempts[^1];
        if (string.IsNullOrEmpty(commit) || commit.Length != 40 || !commit.All(Uri.IsHexDigit)) throw new ApplicationFailure("workspace_changed");
        if (!await _operations.HasCheckpointAsync(attempt.Id, commit, token) ||
            attempt.Target.GitRepository!.Grant!.PublishesChanges() && await _remote.ReconcileAsync(attempt.Target.GitRepository!, _uploads.DirectoryFor(attempt.Id), GitRepositoryOperationKind.Publish, commit, token) is null)
            throw new ApplicationFailure("workspace_checkpoint_unconfirmed");
    }

    public async Task AuthorizeAsync(long attemptId, string capability, bool write, CancellationToken token)
    {
        WorkSnapshot work = await WorkAsync(attemptId, token);
        AttemptSnapshot attempt = work.Attempts[^1];
        if (!_capability.IsValid(capability, work.Id, attempt.Id, attempt.Target.GitRepository!.Grant!.Generation))
            throw new ApplicationFailure("repository_operation_unavailable");
        if (write && !GitRepositoryAttemptPolicy.AllowsOperations(attempt.Status))
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
            x.Target.GitRepository?.Grant?.PublishesChanges() == true);
        if (saved is not null && published) await _remote.PrepareCheckpointAsync(gitRepository, _uploads.DirectoryFor(attempt.Id), saved, token);
        else await _remote.PrepareAsync(gitRepository, _uploads.DirectoryFor(attempt.Id), work.Workspace is not null ? null : checkpoint, token);
        return Capability(work);
    }

    public string InputPath(long attemptId) => Path.Combine(_uploads.DirectoryFor(attemptId), "input.bundle");

    public async Task<long> ReserveOperationIdAsync(long attemptId, CancellationToken token)
    {
        WorkSnapshot work = await WorkAsync(attemptId, token);
        if (!GitRepositoryAttemptPolicy.AllowsOperations(work.Attempts[^1].Status))
            throw new ApplicationFailure("repository_operation_unavailable");
        return await _operations.ReserveIdAsync(token);
    }

    public async Task<GitRepositoryOperationView> EnqueueAsync(long attemptId, long id, GitRepositoryOperationKind kind, Stream input, CancellationToken token)
    {
        if (id <= 0) throw new ApplicationFailure("repository_operation_unavailable");
        WorkSnapshot work = await WorkAsync(attemptId, token);
        GitRepositoryChange gitRepository = work.Attempts[^1].Target.GitRepository!;
        gitRepository.Grant!.Authorize(work.Id, attemptId, gitRepository.GitRepository, gitRepository.GitRepository, gitRepository.Grant.Branch, kind);
        using GitRepositoryOperationUpload upload = await _uploads.StageAsync(attemptId, id, kind, input, token);
        return await _operations.EnqueueAsync(upload, token);
    }

    public Task<GitRepositoryOperationView> StatusAsync(long attemptId, long id, CancellationToken token) =>
        _operations.StatusAsync(attemptId, id, token);

    public async Task ExecuteAsync(long id, CancellationToken token)
    {
        try { await ExecuteCoreAsync(id, token); }
        catch
        {
            // Preserve failed dispatch evidence through a database outage. Never replay it.
            await _evidence.RecordDispatchFailureAsync(id);
            throw;
        }
    }

    private async Task ExecuteCoreAsync(long id, CancellationToken token)
    {
        GitRepositoryOperationSnapshot? started = await _operations.TryStartAsync(id, token);
        if (started is null) return;
        long attemptId = started.AttemptId;
        SemaphoreSlim gate = _locks.GetOrAdd(attemptId, _ => new(1));
        await gate.WaitAsync(token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        cancellation.CancelAfter(TimeSpan.FromMinutes(2));
        _active[attemptId] = cancellation;
        bool external = false;
        try
        {
            WorkSnapshot work = await WorkAsync(attemptId, cancellation.Token);
            if (!GitRepositoryAttemptPolicy.AllowsOperations(work.Attempts[^1].Status)) throw new ApplicationFailure("repository_operation_unavailable");
            GitRepositoryChange gitRepository = work.Attempts[^1].Target.GitRepository!;
            GitRepositoryOperationSnapshot operation = await _operations.RequireRunningAsync(id, cancellation.Token);
            gitRepository.Grant!.Authorize(work.Id, attemptId, gitRepository.GitRepository, gitRepository.GitRepository, gitRepository.Grant.Branch, operation.Kind);
            string commit = operation.Kind == GitRepositoryOperationKind.Fetch ? "read" : await _remote.InspectBundleAsync(gitRepository,
                _uploads.DirectoryFor(attemptId), _uploads.BundleFor(attemptId, id), cancellation.Token);
            await _operations.RecordInspectedCommitAsync(id, commit, cancellation.Token);
            if (operation.Kind == GitRepositoryOperationKind.Checkpoint)
            {
                await _operations.CompleteAsync(id, null, cancellation.Token);
                return;
            }
            await _evidence.RecordExternalLaunchAsync(id, cancellation.Token);
            external = true;
            GitRepositoryOperationResult result = await _remote.ExecuteAsync(gitRepository, _uploads.DirectoryFor(attemptId), operation.Kind, commit, cancellation.Token);
            await _operations.CompleteAsync(id, result.Url, cancellation.Token);
        }
        catch
        {
            if (external) await _operations.LoseExternalResponseAsync(id, CancellationToken.None);
            else await _operations.FailBeforeLaunchAsync(id, CancellationToken.None);
        }
        finally { _active.TryRemove(attemptId, out _); gate.Release(); }
    }

    public async Task<ExecutionObservation?> ObserveAsync(WorkSnapshot work, CancellationToken token)
    {
        AttemptSnapshot attempt = work.Attempts[^1];
        GitRepositoryOperationSnapshot[] rows = await _operations.IncompleteAsync(attempt.Id, token);
        if (rows.Length == 0) return null;
        bool allSucceeded = true;
        foreach (GitRepositoryOperationSnapshot initial in rows)
        {
            GitRepositoryOperationSnapshot row = initial;
            if (_evidence.HasDispatchFailure(row.Id))
            {
                await _operations.RecoverDispatchFailureAsync(row.Id, token);
                _evidence.ClearDispatchFailure(row.Id);
                row = await _operations.ReadAsync(row.Id, token);
            }
            if (_active.ContainsKey(attempt.Id) || row.State == GitRepositoryOperationState.Queued) return new(ObservationKind.Pending);
            row = await _operations.ReadAsync(row.Id, token);
            if (row.State == GitRepositoryOperationState.Succeeded) continue;
            if (row.Kind == GitRepositoryOperationKind.Checkpoint && row.State is GitRepositoryOperationState.Running or GitRepositoryOperationState.Uncertain)
            {
                await _operations.RecordReconciliationAsync(row.Id, GitRepositoryOperationState.Failed, row.Url, token);
                allSucceeded = false;
                continue;
            }
            GitRepositoryOperationState state = row.State;
            if (row.State is GitRepositoryOperationState.Running or GitRepositoryOperationState.Uncertain)
            {
                state = GitRepositoryOperationState.Uncertain;
                GitRepositoryOperationResult? result = null;
                try
                {
                    if (row.Commit is not null)
                        result = await _remote.ReconcileAsync(attempt.Target.GitRepository!, _uploads.DirectoryFor(attempt.Id), row.Kind, row.Commit, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch
                {
                    // Revoked access must not prevent proving the publisher stopped.
                    // Failure retains the uncertain history; it does not assert that
                    // GitHub was unchanged or automatically repeat the operation.
                }
                if (result is not null) state = GitRepositoryOperationState.Succeeded;
                // Each external subprocess has a 120s watchdog; the entire operation also has
                // a 120s limit. After a controller crash no child can survive this bound.
                else if (_evidence.ExternalProcessStopped(row.Id)) state = GitRepositoryOperationState.Failed;
                string? url = result is not null ? result.Url : row.Url;
                await _operations.RecordReconciliationAsync(row.Id, state, url, token);
            }
            if (state != GitRepositoryOperationState.Succeeded) allSucceeded = false;
        }
        return allSucceeded ? null : new(ObservationKind.Uncertain, Failure: FailureKind.ExecutionFailed);
    }

    public async Task StopAsync(WorkSnapshot work, CancellationToken token)
    {
        long attemptId = work.Attempts[^1].Id;
        if (_active.TryGetValue(attemptId, out CancellationTokenSource? cancellation)) cancellation.Cancel();
        SemaphoreSlim gate = _locks.GetOrAdd(attemptId, _ => new(1));
        await gate.WaitAsync(token);
        try
        {
            await _operations.FailQueuedAsync(attemptId, token);
            if ((await ObserveAsync(work, token))?.Kind == ObservationKind.Uncertain &&
                await _operations.HasUnresolvedExecutionAsync(attemptId, token))
                throw new ApplicationFailure("repository_operation_unavailable");
        }
        finally { gate.Release(); }
    }

    public async Task ReleaseAsync(WorkSnapshot work, CancellationToken token)
    {
        await StopAsync(work, token);
        // Called only after sandbox termination is confirmed. Published GitHub
        // checkpoints and the retained sandbox PVC own recovery; discard duplicates.
        string directory = _uploads.DirectoryFor(work.Attempts[^1].Id);
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}

public static class ExecuteGitRepositoryOperationHandler
{
    public static Task Handle(ExecuteGitRepositoryOperation command, GitRepositoryBroker broker, CancellationToken token) => broker.ExecuteAsync(command.Id, token);
}
