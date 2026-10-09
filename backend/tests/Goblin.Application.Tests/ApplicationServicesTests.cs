using System.Linq;
using Goblin.Application;
using Goblin.Application.Connections;
using Goblin.Application.GitRepositories;
using Goblin.Application.Runtime;
using Goblin.Application.Work;
using Goblin.Application.Workspaces;
using Goblin.Contracts.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Goblin.Application.Tests;

public sealed class ApplicationServicesTests
{
    [Fact]
    public void RepositoryOperationStoreAndItsOutboxFactoryHaveCompatibleExplicitLifetimes()
    {
        var services = new ServiceCollection();
        services.AddWorkApplication();

        Assert.Equal(ServiceLifetime.Singleton,
            services.Single(x => x.ServiceType == typeof(GitRepositoryOperationStore)).Lifetime);
        Assert.Equal(ServiceLifetime.Singleton,
            services.Single(x => x.ServiceType == typeof(WorkOutboxFactory)).Lifetime);
    }

    [Fact]
    public void InspectionCoordinatorUsesExplicitSingletonSafeDependencies()
    {
        var services = new ServiceCollection();
        services.AddWorkApplication();

        Assert.Equal(ServiceLifetime.Singleton,
            services.Single(x => x.ServiceType == typeof(InspectionStore)).Lifetime);
        Assert.Equal(new[] { typeof(InspectionStore), typeof(IInspectionHost), typeof(IServiceScopeFactory) },
            typeof(InspectionCoordinator).GetConstructors().Single().GetParameters().Select(x => x.ParameterType));
    }

    [Fact]
    public void ExecutionStoresAreSharedWithoutRetainingAnOperationContext()
    {
        var services = new ServiceCollection();
        services.AddWorkApplication();
        foreach (System.Type store in new[] { typeof(WorkStore), typeof(IdentityStore), typeof(ConnectionStore), typeof(GitHubStore) })
            Assert.Equal(ServiceLifetime.Singleton, services.Single(x => x.ServiceType == store).Lifetime);
        Assert.Equal(new[] { typeof(WorkStore), typeof(IdentityStore), typeof(IExecutionHost), typeof(IDispatchFailureJournal) },
            typeof(ExecutionCoordinator).GetConstructors().Single().GetParameters().Select(x => x.ParameterType));
        Assert.DoesNotContain(typeof(WorkRecovery).GetConstructors().Single().GetParameters(),
            parameter => parameter.ParameterType == typeof(System.IServiceProvider));
    }
}
