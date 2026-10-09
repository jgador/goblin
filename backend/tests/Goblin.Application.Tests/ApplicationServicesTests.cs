using System.Linq;
using Goblin.Application;
using Goblin.Application.GitRepositories;
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
}
