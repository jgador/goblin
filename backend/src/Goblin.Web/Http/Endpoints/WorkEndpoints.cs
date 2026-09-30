using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.Work;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

using Api = Goblin.Web.Http.Contracts;

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
        WorkResponse.Json(Array.ConvertAll(await store.ListAsync(token), Api.WorkView.From));

    private static async Task<IResult> GetAsync(long id, WorkStore store, CancellationToken token) =>
        WorkResponse.Json(Api.WorkView.From(await store.GetAsync(id, token)));

    private static async Task<IResult> ApplyAsync(HttpContext context, WorkStore store)
    {
        Dictionary<string, JsonElement> body = ApiRequest.Body(context);
        Api.WorkCommand command = JsonSerializer.Deserialize<Api.WorkCommand>(JsonSerializer.Serialize(body), WorkStore.Json)
            ?? throw new PublicError("invalid_command", "Send a work command.");
        // Once accepted, the command has an independent transaction and
        // execution lifecycle. RequestAborted is deliberately not passed.
        return WorkResponse.Json(Api.WorkView.From(await store.ApplyAsync(command.ToApplication())));
    }

    private static async Task<IResult> ReserveIdentitiesAsync(HttpContext context, IdentityStore store, CancellationToken token)
    {
        Dictionary<string, JsonElement> body = ApiRequest.Body(context);
        Api.IdentityRequest request = JsonSerializer.Deserialize<Api.IdentityRequest>(JsonSerializer.Serialize(body), WorkStore.Json)
            ?? throw new PublicError("invalid_command", "Specify the IDs to reserve.");
        return WorkResponse.Json(Api.ReservedIdentities.From(await store.ReserveAsync(request.ToApplication(), token)));
    }

    private static async Task<IResult> AgentsAsync(WorkStore store, CancellationToken token) =>
        WorkResponse.Json(Array.ConvertAll(await store.AgentsAsync(token), Api.AgentView.From));
}
