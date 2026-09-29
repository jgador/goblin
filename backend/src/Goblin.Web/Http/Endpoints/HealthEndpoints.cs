using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Goblin.Web;

internal sealed class HealthEndpoints
{
    private readonly ApplicationOptions _options;

    public HealthEndpoints(ApplicationOptions options) => _options = options;

    public void Map(WebApplication app)
    {
        app.MapGet("/healthz", Health);
        app.MapGet("/readyz", (Delegate)ReadyAsync);
    }

    private static IResult Health() => Results.Json(new { ok = true });

    private async Task<IResult> ReadyAsync(HttpContext context)
    {
        bool ready = true;
        if (_options.EnableWork)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                IDbContextFactory<GoblinDbContext> factory = context.RequestServices.GetRequiredService<IDbContextFactory<GoblinDbContext>>();
                await using GoblinDbContext db = await factory.CreateDbContextAsync(timeout.Token);
                await db.WorkItems.AsNoTracking().Select(x => x.Id).Take(1).ToArrayAsync(timeout.Token);
            }
            catch { ready = false; }
        }
        return Results.Json(new { ready }, statusCode: ready ? 200 : 503);
    }
}
