using System;
using System.Linq;
using Goblin.Persistence;
using Goblin.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Goblin.Persistence.Tests;

public sealed class GitRepositoryMappingTests
{
    [Fact]
    public void RenamedEntitiesKeepTheirExistingSqlMappings()
    {
        // Building metadata and translating a query do not open a connection.
        using var db = new GoblinDbContext(new DbContextOptionsBuilder<GoblinDbContext>()
            .UseNpgsql("Host=localhost;Database=example;Username=example").Options);
        IEntityType memory = db.Model.FindEntityType(typeof(GitRepositorySetupMemory))!;
        Assert.Equal("repository_setup_memories", memory.GetTableName());
        Assert.Equal("repository_operations", db.Model.FindEntityType(typeof(GitRepositoryOperation))!.GetTableName());
        Assert.Equal("repository_id", memory.FindProperty(nameof(GitRepositorySetupMemory.GitRepositoryId))!.GetColumnName());
        Assert.Equal("repository", db.Model.FindEntityType(typeof(WorkspaceCheckpoint))!
            .FindProperty(nameof(WorkspaceCheckpoint.GitRepository))!.GetColumnName());
        Assert.Equal(typeof(GithubRepository), memory.FindNavigation(nameof(GitRepositorySetupMemory.GitRepository))!.TargetEntityType.ClrType);
        Assert.DoesNotContain(memory.GetProperties(), property => property.IsShadowProperty());

        string sql = db.GitRepositorySetupMemories.Where(row => row.GitRepositoryId == 7)
            .Select(row => new { row.GitRepository.Name, row.Checkpoint.GitRepository }).ToQueryString();
        Assert.Contains("repository_setup_memories", sql, StringComparison.Ordinal);
        Assert.Contains("repository_id", sql, StringComparison.Ordinal);
        Assert.Contains("github_repositories", sql, StringComparison.Ordinal);
        Assert.Contains(".repository", sql, StringComparison.Ordinal);
    }
}
