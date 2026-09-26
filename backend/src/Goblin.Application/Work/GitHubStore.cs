using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;
using Goblin.Persistence;
using Goblin.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Goblin.Application.Work;

public sealed record EnabledRepository(long Id, string Name, string DefaultBranch, bool Enabled);
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
        connection.Availability = "Changing";
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }
    public async Task ObserveAsync(RepositoryAccount? account, string status, CancellationToken token = default)
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
        connection.Availability = status == "Connecting" ? "Changing" : status;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }
    public async Task<EnabledRepository[]> RepositoriesAsync(CancellationToken token = default)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        return await db.GithubRepositories.OrderBy(x => x.Name)
            .Select(x => new EnabledRepository(x.Id, x.Name, x.DefaultBranch, x.Enabled)).ToArrayAsync(token);
    }
    public async Task SetRepositoryAsync(RepositoryInfo repository, bool enabled, string generation, CancellationToken token = default)
    {
        await using GoblinDbContext db = await _factory.CreateDbContextAsync(token);
        await using IDbContextTransaction transaction = await WorkStore.BeginAsync(db, token);
        await RequireIdleAsync(db, token);
        GithubConnection connection = await db.GithubConnections.SingleAsync(x => x.Id == 1, token);
        if (connection.Generation != generation || connection.Availability != "Connected")
            throw new ApplicationFailure("repository_unavailable");
        GithubRepository? row = await db.GithubRepositories.SingleOrDefaultAsync(x => x.Id == repository.Id, token);
        if (row is null) { row = new() { Id = repository.Id, ConnectionId = 1 }; db.GithubRepositories.Add(row); }
        row.Name = repository.Name; row.DefaultBranch = repository.DefaultBranch; row.Enabled = enabled;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }
    internal static async Task AcceptAsync(GoblinDbContext db, RepositoryAuthorization approval, CancellationToken token)
    {
        RepositoryChange requested = approval.Target.Repository!;
        RepositoryGrant grant = requested.Grant!;
        GithubConnection connection = await db.GithubConnections.SingleAsync(x => x.Id == grant.ConnectionId, token);
        if (connection.Availability != "Connected" || connection.Generation != grant.Generation ||
            connection.AccountId != grant.AccountId || connection.Login != grant.Login)
            throw new ApplicationFailure("repository_authorization_changed");
        GithubRepository? repository = await db.GithubRepositories.SingleOrDefaultAsync(x => x.Id == grant.RepositoryId, token);
        if (repository is not null && (repository.ConnectionId != grant.ConnectionId ||
            !repository.Name.Equals(requested.Repository, StringComparison.OrdinalIgnoreCase)))
            throw new ApplicationFailure("repository_authorization_changed");
        if (repository?.Enabled != true)
        {
            if (!approval.EnableRepository) throw new ApplicationFailure("repository_authorization_changed");
            await RequireIdleAsync(db, token);
            if (repository is null)
            {
                repository = new() { Id = grant.RepositoryId, ConnectionId = grant.ConnectionId, Name = requested.Repository };
                db.GithubRepositories.Add(repository);
            }
            repository.DefaultBranch = approval.DefaultBranch ?? grant.BaseBranch;
            repository.Enabled = true;
        }
    }
    private static async Task RequireIdleAsync(GoblinDbContext db, CancellationToken token)
    {
        if (await db.ExecutionAttempts.AnyAsync(x => x.GithubConnectionId == 1 &&
            (x.Status == "Queued" || x.Status == "Starting" || x.Status == "Running" || x.Status == "Uncertain" || x.Status == "CancellationRequested" || x.CleanupPending || x.WorkspaceRetained), token))
            throw new ApplicationFailure("github_connection_in_use");
    }
}
