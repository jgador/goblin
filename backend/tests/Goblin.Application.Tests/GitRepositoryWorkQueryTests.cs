using System;
using System.Linq;
using Goblin.Application.GitRepositories;
using Goblin.Application.Work;
using Goblin.Core.Work;
using Goblin.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;
using AttemptRow = Goblin.Persistence.Entities.ExecutionAttempt;

namespace Goblin.Application.Tests;

public sealed class GitRepositoryWorkQueryTests
{
    [Fact]
    public void OwnerProjectionRejectsMissingState()
    {
        var owner = new GitRepositoryWorkOwner { State = null!, TurnNumber = 3 };
        Assert.Throws<ArgumentNullException>(() => owner.Snapshot());
    }

    [Fact]
    public void OwnerProjectionRestoresPersistedAggregateState()
    {
        DateTimeOffset created = new(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);
        var work = new WorkItem(5, "Inspect repository", created);
        work.Assign(11, created.AddMinutes(1));
        Persistence.Entities.WorkItem saved = WorkStatePersistence.Create(work, created.AddMinutes(1));
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
        Assert.DoesNotContain("objective", sql);
        Assert.DoesNotContain("created_at", sql);
        Assert.Contains("turn_number", sql);
        Assert.DoesNotContain("status", sql);
        Assert.DoesNotContain("connection_id", sql);
        Assert.DoesNotContain("environment_reference", sql);
    }
}
