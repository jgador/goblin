using System;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.Work;
using Goblin.Contracts;
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
        Api.ConversationCommand command = ApiRequest.Body<Api.ConversationCommand>(context, ContractJson.Options);
        return WorkResponse.Json(Api.ConversationView.From(await store.ApplyAsync(command.ToApplication())));
    }
}
