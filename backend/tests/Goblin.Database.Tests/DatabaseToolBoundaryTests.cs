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
    public void ScaffoldingUsesSingularGitCandidatesAndPreservesColumnNames()
    {
        var services = new ServiceCollection();
        new GitRepositoryDesignTimeServices().ConfigureDesignTimeServices(services);
        Assert.Equal(typeof(ICandidateNamingService), Assert.Single(services).ServiceType);
        using ServiceProvider provider = services.BuildServiceProvider();
        ICandidateNamingService naming = provider.GetRequiredService<ICandidateNamingService>();

        Assert.Equal("GitRepositorySetupMemory", naming.GenerateCandidateIdentifier("repository_setup_memory"));
        Assert.Equal("GitRepositoryOperation", naming.GenerateCandidateIdentifier("repository_operation"));
        Assert.Equal("GitRepositoryId", naming.GenerateCandidateIdentifier("repository_id"));
        Assert.Equal("GitRepository", naming.GenerateCandidateIdentifier("repository"));
        Assert.Equal("GithubRepository", naming.GenerateCandidateIdentifier("github_repository"));
        Assert.Equal("WorkItem", naming.GenerateCandidateIdentifier("work_item"));
    }
#pragma warning restore EF1001
}
