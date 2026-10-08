using System;
using Goblin.Database.Scaffolding;
using Microsoft.EntityFrameworkCore.Scaffolding.Internal;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Goblin.Database.Tests;

public sealed class DatabaseToolBoundaryTests
{
    [Fact]
    public void MigrationRunnerDoesNotReferenceApplicationPersistenceOrEntityFramework()
    {
        Assert.DoesNotContain(typeof(SqlMigrations).Assembly.GetReferencedAssemblies(), assembly =>
            assembly.Name == "Goblin.Persistence" ||
            assembly.Name!.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
    }

    // Scaffolding owns this pinned EF internal API; application/persistence tests
    // do not need design-time dependencies to exercise their runtime boundaries.
#pragma warning disable EF1001
    [Fact]
    public void ScaffoldingUsesGitNamesWithoutRenamingDatabaseObjects()
    {
        var services = new ServiceCollection();
        new GitRepositoryDesignTimeServices().ConfigureDesignTimeServices(services);
        Assert.Equal(typeof(ICandidateNamingService), Assert.Single(services).ServiceType);
        using ServiceProvider provider = services.BuildServiceProvider();
        ICandidateNamingService naming = provider.GetRequiredService<ICandidateNamingService>();

        Assert.Equal("GitRepositorySetupMemories", naming.GenerateCandidateIdentifier("repository_setup_memories"));
        Assert.Equal("GitRepositoryOperations", naming.GenerateCandidateIdentifier("repository_operations"));
        Assert.Equal("GitRepositoryId", naming.GenerateCandidateIdentifier("repository_id"));
        Assert.Equal("GitRepository", naming.GenerateCandidateIdentifier("repository"));
        Assert.Equal("GithubRepositories", naming.GenerateCandidateIdentifier("github_repositories"));
        Assert.Equal("WorkItems", naming.GenerateCandidateIdentifier("work_items"));
    }
#pragma warning restore EF1001
}
