using System;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.Connections;
using Goblin.Application.Work;
using Goblin.Contracts.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Api = Goblin.Web.Http.Contracts;

namespace Goblin.Web;

internal sealed class ConnectionEndpoints
{
    private readonly ApplicationOptions _options;

    public ConnectionEndpoints(ApplicationOptions options) => _options = options;

    public void Map(WebApplication app)
    {
        app.MapGet("/api/status", StatusAsync);
        app.MapPost("/api/auth/chatgpt", LoginChatGPTAsync);
        app.MapPost("/api/auth/api-key", LoginApiKeyAsync);
        app.MapPost("/api/auth/cancel", CancelLoginAsync);
        app.MapPost("/api/auth/logout", LogoutAsync);
        app.MapPost("/api/prompt", PromptAsync);
        if (_options.EnableWork)
        {
            app.MapGet("/api/connections", ListAsync);
            app.MapGet("/api/connections/{id:long}/models", ModelsAsync);
            app.MapPost("/api/connections/{id:long}/models/refresh", RefreshModelsAsync);
            app.MapGet("/api/runtimes", Runtimes);
        }
    }

    private static async Task<Api.AuthenticationState> StatusAsync(ConnectionService service, CancellationToken token) =>
        Api.AuthenticationState.From(await service.StatusAsync(token));

    private static async Task<Api.AuthenticationState> LoginChatGPTAsync(ConnectionService service, CancellationToken token) =>
        Api.AuthenticationState.From(await service.LoginChatGPTAsync(token));

    private static async Task<Api.AuthenticationState> LoginApiKeyAsync(HttpContext context, ConnectionService service) =>
        Api.AuthenticationState.From(await service.LoginApiKeyAsync(ApiRequest.Body<ApiKeyRequest>(context).ApiKey, context.RequestAborted));

    private static async Task<Api.AuthenticationState> CancelLoginAsync(ConnectionService service, CancellationToken token) =>
        Api.AuthenticationState.From(await service.CancelLoginAsync(token));

    private static async Task<Api.AuthenticationState> LogoutAsync(ConnectionService service, CancellationToken token) =>
        Api.AuthenticationState.From(await service.LogoutAsync(token));

    private static async Task<IResult> PromptAsync(HttpContext context, ConnectionService service) =>
        Results.Json(Api.PromptResult.From(await service.PromptAsync(ApiRequest.Body<PromptRequest>(context).Prompt, context.RequestAborted)));

    private static async Task<IResult> ListAsync(ConnectionService service, CancellationToken token) =>
        WorkResponse.Json(Array.ConvertAll(await service.ListAsync(token), Api.ConnectionView.From));

    private static async Task<IResult> ModelsAsync(long id, int? limit, string? selected, ModelCatalogStore catalogs, CancellationToken token) =>
        WorkResponse.Json(Api.ModelCatalogView.From(await catalogs.GetAsync(id, limit ?? 3, selected, token)));

    private static async Task<IResult> RefreshModelsAsync(long id, ModelCatalogStore catalogs, CancellationToken token)
    {
        catalogs.ScheduleRefresh(id, force: true);
        return WorkResponse.Json(Api.ModelCatalogView.From(await catalogs.GetAsync(id, 3, null, token)));
    }

    private static Api.RuntimeCapabilities[] Runtimes(IExecutionHost host) => Array.ConvertAll(host.Capabilities, Api.RuntimeCapabilities.From);
}
