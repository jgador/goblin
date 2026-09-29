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

internal static class ConversationEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/conversations", ListAsync);
        app.MapPost("/api/conversations/commands", ApplyAsync);
    }

    private static async Task<IResult> ListAsync(ConversationStore store, CancellationToken token) =>
        WorkResponse.Json(Array.ConvertAll(await store.ListAsync(token), Api.ConversationView.From));

    private static async Task<IResult> ApplyAsync(HttpContext context, ConversationStore store)
    {
        Dictionary<string, JsonElement> body = ApiRequest.Body(context);
        Api.ConversationCommand command = JsonSerializer.Deserialize<Api.ConversationCommand>(JsonSerializer.Serialize(body), WorkStore.Json)
            ?? throw new PublicError("invalid_command", "Send a conversation command.");
        return WorkResponse.Json(Api.ConversationView.From(await store.ApplyAsync(command.ToApplication())));
    }
}
