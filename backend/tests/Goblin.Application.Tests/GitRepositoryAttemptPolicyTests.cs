using System;
using System.Linq;
using Goblin.Application.GitRepositories;
using Goblin.Core.Work;
using Goblin.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;
using AttemptRow = Goblin.Persistence.Entities.ExecutionAttempt;

namespace Goblin.Application.Tests;

public sealed class GitRepositoryAttemptPolicyTests
{
    [Fact]
    public void EveryAttemptStateHasTheSameRepositoryPermissionInMemoryAndStorage()
    {
        foreach (AttemptStatus status in Enum.GetValues<AttemptStatus>())
        {
            bool expected = status is AttemptStatus.Starting or AttemptStatus.Running;

            Assert.Equal(expected, GitRepositoryAttemptPolicy.AllowsOperations(status));
            Assert.Equal(expected, GitRepositoryAttemptPolicy.AllowsOperations(status.ToString()));
        }
    }

    [Fact]
    public void RepositoryPermissionQueryComposesAndTranslatesToPostgres()
    {
        AttemptRow[] rows = [.. Enum.GetValues<AttemptStatus>()
            .Select((status, index) => new AttemptRow { Id = index + 1, Status = status.ToString() })];
        Assert.Equal(
            [AttemptStatus.Starting.ToString(), AttemptStatus.Running.ToString()],
            GitRepositoryAttemptPolicy.AllowingOperations(rows.AsQueryable()).Select(x => x.Status));

        using var db = new GoblinDbContext(new DbContextOptionsBuilder<GoblinDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused").Options);
        string sql = GitRepositoryAttemptPolicy.AllowingOperations(db.ExecutionAttempts).ToQueryString();
        Assert.Contains(nameof(AttemptStatus.Starting), sql);
        Assert.Contains(nameof(AttemptStatus.Running), sql);
        Assert.DoesNotContain(nameof(AttemptStatus.Queued), sql);
    }
}
