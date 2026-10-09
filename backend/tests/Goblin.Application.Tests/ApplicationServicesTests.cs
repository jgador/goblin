using System.Linq;
using Goblin.Application;
using Goblin.Application.GitRepositories;
using Goblin.Application.Work;
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
}
