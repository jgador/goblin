using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.Work;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Goblin.Web;

internal static class WorkEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/work", ListAsync);
        app.MapGet("/api/work/{id:long}", GetAsync);
        app.MapPost("/api/work/commands", ApplyAsync);
        app.MapPost("/api/identities", ReserveIdentitiesAsync);
        app.MapGet("/api/agents", AgentsAsync);
    }

    private static async Task<IResult> ListAsync(WorkStore store, CancellationToken token) =>
        WorkResponse.Json(await store.ListAsync(token));

    private static async Task<IResult> GetAsync(long id, WorkStore store, CancellationToken token) =>
        WorkResponse.Json(await store.GetAsync(id, token));

    private static async Task<IResult> ApplyAsync(HttpContext context, WorkStore store)
    {
        Dictionary<string, JsonElement> body = ApiRequest.Body(context);
        WorkCommand command = JsonSerializer.Deserialize<WorkCommand>(JsonSerializer.Serialize(body), WorkStore.Json)
            ?? throw new PublicError("invalid_command", "Send a work command.");
        // Once accepted, the command has an independent transaction and
        // execution lifecycle. RequestAborted is deliberately not passed.
        return WorkResponse.Json(await store.ApplyAsync(command));
    }

    private static async Task<IResult> ReserveIdentitiesAsync(HttpContext context, IdentityStore store, CancellationToken token)
    {
        Dictionary<string, JsonElement> body = ApiRequest.Body(context);
        IdentityRequest request = JsonSerializer.Deserialize<IdentityRequest>(JsonSerializer.Serialize(body), WorkStore.Json)
            ?? throw new PublicError("invalid_command", "Specify the IDs to reserve.");
        return WorkResponse.Json(await store.ReserveAsync(request, token));
    }

    private static async Task<IResult> AgentsAsync(WorkStore store, CancellationToken token) =>
        WorkResponse.Json(await store.AgentsAsync(token));
}
