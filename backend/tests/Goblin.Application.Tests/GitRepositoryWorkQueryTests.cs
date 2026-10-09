using System;
using System.Linq;
using Goblin.Application.GitRepositories;
using Goblin.Application.Work;
using Goblin.Core.Work;
using Goblin.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;
using AttemptRow = Goblin.Persistence.Entities.ExecutionAttempt;
using WorkRow = Goblin.Persistence.Entities.WorkItem;

namespace Goblin.Application.Tests;

public sealed class GitRepositoryWorkQueryTests
{
    [Fact]
    public void OwnerProjectionRestoresLegacyWorkState()
    {
        DateTime created = new(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc);
        var row = new AttemptRow
        {
            Id = 7,
            Status = AttemptStatus.Running.ToString(),
            TurnNumber = 3,
            Work = new WorkRow { Id = 5, Objective = "Inspect repository", CreatedAt = created }
        };

        GitRepositoryWorkOwner owner = GitRepositoryWorkQuery.ForAttempt(new[] { row }.AsQueryable(), 7).Single();
        Assert.Equal(3, owner.TurnNumber);
        Assert.Equal(5, owner.Snapshot().Id);
        Assert.Equal("Inspect repository", owner.Snapshot().Objective);
    }

    [Fact]
    public void OwnerProjectionRestoresPersistedAggregateState()
    {
        DateTimeOffset created = new(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);
        var work = new WorkItem(5, "Inspect repository", created);
        work.Assign(11, created.AddMinutes(1));
        WorkRow saved = WorkStatePersistence.Create(work, created.AddMinutes(1));
        var row = new AttemptRow { Id = 7, Status = AttemptStatus.Running.ToString(), TurnNumber = 3, Work = saved };

        WorkSnapshot snapshot = GitRepositoryWorkQuery.ForAttempt(new[] { row }.AsQueryable(), 7).Single().Snapshot();

        Assert.Equal(5, snapshot.Id);
        Assert.Equal(11, snapshot.AgentId);
        Assert.Equal("Inspect repository", snapshot.Objective);
    }

    [Fact]
    public void OwnerProjectionUsesOnePostgresQueryWithOnlyAuthorizationState()
    {
        using var db = new GoblinDbContext(new DbContextOptionsBuilder<GoblinDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused").Options);

        string sql = GitRepositoryWorkQuery.ForAttempt(db.ExecutionAttempts.AsNoTracking(), 7).ToQueryString();

        Assert.Contains("JOIN", sql);
        Assert.Contains("state", sql);
        Assert.Contains("objective", sql);
        Assert.Contains("created_at", sql);
        Assert.Contains("turn_number", sql);
        Assert.DoesNotContain("status", sql);
        Assert.DoesNotContain("connection_id", sql);
        Assert.DoesNotContain("environment_reference", sql);
    }
}
