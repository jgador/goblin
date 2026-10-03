using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;
using Goblin.Persistence;
using Goblin.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Goblin.Application.Work;

public sealed class EnabledGitRepository
{
    public EnabledGitRepository(long id, string name, string defaultBranch, bool enabled)
    {
        Id = id;
        Name = name;
        DefaultBranch = defaultBranch;
        Enabled = enabled;
    }

    public long Id { get; init; }

    public string Name { get; init; }

    public string DefaultBranch { get; init; }

    public bool Enabled { get; init; }
}

public sealed class GitHubStore
{
    private readonly IDbContextFactory<GoblinDbContext> _factory;

    public GitHubStore(IDbContextFactory<GoblinDbContext> factory) => _factory = factory;

    public async Task BeginChangeAsync(CancellationToken token = default)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        await using IDbContextTransaction transaction = await WorkStore.BeginAsync(db, token);
        await RequireIdleAsync(db, token);
        GithubConnection connection = await db.GithubConnections.SingleAsync(x => x.Id == 1, token);
        connection.Availability = nameof(ConnectionAvailability.Changing);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    public async Task ObserveAsync(GitRepositoryAccount? account, GitHubConnectionStatus status, CancellationToken token = default)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        await using IDbContextTransaction transaction = await WorkStore.BeginAsync(db, token);
        GithubConnection connection = await db.GithubConnections.SingleAsync(x => x.Id == 1, token);
        if (connection.Generation != account?.Generation)
        {
            await RequireIdleAsync(db, token);
            if (connection.AccountId != account?.AccountId)
                await db.GithubRepositories.ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, false), token);
        }
        connection.Generation = account?.Generation;
        connection.AccountId = account?.AccountId;
        connection.Login = account?.Login;
        connection.Availability = status == GitHubConnectionStatus.Connecting ? nameof(ConnectionAvailability.Changing) : status.ToString();
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    public async Task<EnabledGitRepository[]> GitRepositoriesAsync(CancellationToken token = default)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        return await db.GithubRepositories.OrderBy(x => x.Name)
            .Select(x => new EnabledGitRepository(x.Id, x.Name, x.DefaultBranch, x.Enabled)).ToArrayAsync(token);
    }

    public async Task SetGitRepositoryAsync(GitRepositoryInfo gitRepository, bool enabled, string generation, CancellationToken token = default)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        await using IDbContextTransaction transaction = await WorkStore.BeginAsync(db, token);
        await RequireIdleAsync(db, token);
        GithubConnection connection = await db.GithubConnections.SingleAsync(x => x.Id == 1, token);
        if (connection.Generation != generation || connection.Availability != nameof(GitHubConnectionStatus.Connected))
            throw new ApplicationFailure("repository_unavailable");
        GithubRepository? row = await db.GithubRepositories.SingleOrDefaultAsync(x => x.Id == gitRepository.Id, token);
        if (row is null) { row = new() { Id = gitRepository.Id, ConnectionId = 1 }; db.GithubRepositories.Add(row); }
        row.Name = gitRepository.Name; row.DefaultBranch = gitRepository.DefaultBranch; row.Enabled = enabled;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    internal static async Task AcceptAsync(GoblinDbContext db, GitRepositoryAuthorization approval, CancellationToken token)
    {
        GitRepositoryChange requested = approval.Target.GitRepository!;
        GitRepositoryGrant grant = requested.Grant!;
        GithubConnection connection = await db.GithubConnections.SingleAsync(x => x.Id == grant.ConnectionId, token);
        if (connection.Availability != nameof(GitHubConnectionStatus.Connected) || connection.Generation != grant.Generation ||
            connection.AccountId != grant.AccountId || connection.Login != grant.Login)
            throw new ApplicationFailure("repository_authorization_changed");
        GithubRepository? gitRepository = await db.GithubRepositories.SingleOrDefaultAsync(x => x.Id == grant.GitRepositoryId, token);
        if (gitRepository is not null && (gitRepository.ConnectionId != grant.ConnectionId ||
            !gitRepository.Name.Equals(requested.GitRepository, StringComparison.OrdinalIgnoreCase)))
            throw new ApplicationFailure("repository_authorization_changed");
        if (gitRepository?.Enabled != true)
        {
            if (!approval.EnableGitRepository) throw new ApplicationFailure("repository_authorization_changed");
            await RequireIdleAsync(db, token);
            if (gitRepository is null)
            {
                gitRepository = new() { Id = grant.GitRepositoryId, ConnectionId = grant.ConnectionId, Name = requested.GitRepository };
                db.GithubRepositories.Add(gitRepository);
            }
            gitRepository.DefaultBranch = approval.DefaultBranch ?? grant.BaseBranch;
            gitRepository.Enabled = true;
        }
    }

    private static async Task RequireIdleAsync(GoblinDbContext db, CancellationToken token)
    {
        if (await db.ExecutionAttempts.AnyAsync(x => x.GithubConnectionId == 1 &&
            (x.Status == "Queued" || x.Status == "Starting" || x.Status == "Running" || x.Status == "Uncertain" || x.Status == "CancellationRequested" || x.CleanupPending || x.WorkspaceRetained), token))
            throw new ApplicationFailure("github_connection_in_use");
    }
}
