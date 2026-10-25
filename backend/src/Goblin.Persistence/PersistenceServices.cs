using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Goblin.Persistence;

public static class PersistenceServices
{
    public static IServiceCollection AddGoblinPersistence(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddDbContext<ChatGptOAuthDbContext>(options => options.UseNpgsql(connectionString).UseOpenIddict<long>());
        return services.AddDbContextFactory<GoblinDbContext>(options => options.UseNpgsql(connectionString));
    }
}
