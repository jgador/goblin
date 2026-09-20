using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Goblin.Persistence;

public static class PersistenceServices
{
    public static IServiceCollection AddGoblinPersistence(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return services.AddDbContextFactory<GoblinDbContext>(options => options.UseNpgsql(connectionString));
    }
}
