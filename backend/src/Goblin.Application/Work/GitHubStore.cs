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
        if (connection.Generation != generation || connection.Availability != "Connected" || (enabled && !repository.CanPush))
            throw new ApplicationFailure("repository_unavailable");
        GithubRepository? row = await db.GithubRepositories.SingleOrDefaultAsync(x => x.Id == repository.Id, token);
        if (row is null) { row = new() { Id = repository.Id, ConnectionId = 1 }; db.GithubRepositories.Add(row); }
        row.Name = repository.Name; row.DefaultBranch = repository.DefaultBranch; row.Enabled = enabled;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }
    internal static async Task<RepositoryChange> BindAsync(GoblinDbContext db, RepositoryChange requested, long workId, long attemptId, CancellationToken token)
    {
        GithubRepository repository = await db.GithubRepositories.SingleOrDefaultAsync(x => x.Name.ToLower() == requested.Repository.ToLower() && x.Enabled, token)
            ?? throw new ApplicationFailure("repository_unavailable");
        GithubConnection connection = await db.GithubConnections.SingleAsync(x => x.Id == repository.ConnectionId, token);
        if (connection.Availability != "Connected" || connection.Generation is null || connection.AccountId is null || connection.Login is null)
            throw new ApplicationFailure("repository_unavailable");
        return new(repository.Name, requested.GitAuthorName, requested.GitAuthorEmail,
            new(connection.Id, connection.Generation, connection.AccountId, connection.Login, repository.Id,
                repository.DefaultBranch, $"goblin/{workId}/{attemptId}"));
    }
    private static async Task RequireIdleAsync(GoblinDbContext db, CancellationToken token)
    {
        if (await db.ExecutionAttempts.AnyAsync(x => x.GithubConnectionId == 1 &&
            (x.Status == "Queued" || x.Status == "Starting" || x.Status == "Running" || x.Status == "Uncertain" || x.Status == "CancellationRequested" || x.CleanupPending), token))
            throw new ApplicationFailure("github_connection_in_use");
    }
}
