using Api = Goblin.Web.Http.Contracts;
using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.Work;
using Goblin.Contracts;
using Goblin.Contracts.Runtime;
using Goblin.Integrations.Codex;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Goblin.Web;

internal sealed class ConnectionEndpoints
{
    private readonly ApplicationOptions _options;
    private readonly Authentication _auth;

    public ConnectionEndpoints(ApplicationOptions options, Authentication auth)
    {
        _options = options;
        _auth = auth;
    }

    public void Map(WebApplication app)
    {
        app.MapGet("/api/status", (Delegate)StatusAsync);
        app.MapPost("/api/auth/chatgpt", (Delegate)LoginChatGptAsync);
        app.MapPost("/api/auth/api-key", (Delegate)LoginApiKeyAsync);
        app.MapPost("/api/auth/cancel", (Delegate)CancelLoginAsync);
        app.MapPost("/api/auth/logout", (Delegate)LogoutAsync);
        app.MapPost("/api/prompt", (Delegate)PromptAsync);
        if (_options.EnableWork)
        {
            app.MapGet("/api/connections", ListAsync);
            app.MapGet("/api/connections/{id:long}/models", ModelsAsync);
            app.MapPost("/api/connections/{id:long}/models/refresh", RefreshModelsAsync);
            app.MapGet("/api/runtimes", Runtimes);
        }
    }

    private Task<Api.AuthenticationState> StatusAsync(HttpContext context) => ConnectionAsync(context, false, _auth.StatusAsync);
    private Task<Api.AuthenticationState> LoginChatGptAsync(HttpContext context) => ConnectionAsync(context, true, _auth.LoginChatGptAsync);
    private Task<Api.AuthenticationState> LoginApiKeyAsync(HttpContext context) =>
        ConnectionAsync(context, true, () => _auth.LoginApiKeyAsync(ApiRequest.StringField(context, "apiKey")));
    private Task<Api.AuthenticationState> CancelLoginAsync(HttpContext context) => ConnectionAsync(context, true, _auth.CancelLoginAsync);
    private Task<Api.AuthenticationState> LogoutAsync(HttpContext context) => ConnectionAsync(context, true, _auth.LogoutAsync);

    private async Task<IResult> PromptAsync(HttpContext context)
    {
        WorkStore? store = _options.EnableWork ? context.RequestServices.GetRequiredService<WorkStore>() : null;
        if (store is not null) await store.BeginVerificationAsync(WorkStore.DefaultAgentId, context.RequestAborted);
        bool available = false;
        try
        {
            PromptResult result = await _auth.SendPromptAsync(ApiRequest.StringField(context, "prompt"), context.RequestAborted);
            available = true;
            return Results.Json(Api.PromptResult.From(result));
        }
        finally { if (store is not null) await store.EndVerificationAsync(WorkStore.DefaultAgentId, available); }
    }

    private async Task<IResult> ListAsync(HttpContext context, WorkStore store, CancellationToken token)
    {
        try { await StatusAsync(context); } catch (IntegrationFailure) { }
        return WorkResponse.Json(Array.ConvertAll(await store.ConnectionsAsync(token), Api.ConnectionView.From));
    }

    private static async Task<IResult> ModelsAsync(long id, int? limit, string? selected, ModelCatalogStore catalogs, CancellationToken token) =>
        WorkResponse.Json(Api.ModelCatalogView.From(await catalogs.GetAsync(id, limit ?? 3, selected, token)));

    private static async Task<IResult> RefreshModelsAsync(long id, ModelCatalogStore catalogs, CancellationToken token)
    {
        catalogs.ScheduleRefresh(id, force: true);
        return WorkResponse.Json(Api.ModelCatalogView.From(await catalogs.GetAsync(id, 3, null, token)));
    }

    private static Api.RuntimeCapabilities[] Runtimes(IExecutionHost host) => Array.ConvertAll(host.Capabilities, Api.RuntimeCapabilities.From);

    private async Task<Api.AuthenticationState> ConnectionAsync(HttpContext context, bool changing, Func<Task<AuthenticationState>> action)
    {
        WorkStore? store = _options.EnableWork ? context.RequestServices.GetRequiredService<WorkStore>() : null;
        if (changing && store is not null) await store.SetConnectionAsync(WorkStore.DefaultAgentId, ConnectionAvailability.Changing, requireIdle: true);
        try
        {
            AuthenticationState state = await action();
            if (store is not null)
            {
                bool available = state.Account is not null && state.RuntimeReady;
                bool changed = await store.SetConnectionAsync(WorkStore.DefaultAgentId,
                    available ? ConnectionAvailability.Available : ConnectionAvailability.Disconnected, requireIdle: false,
                    completeChange: changing, observeAccount: true,
                    accountSignature: AccountSignature(state.Account));
                if (available)
                {
                    ModelCatalogStore catalogs = context.RequestServices.GetRequiredService<ModelCatalogStore>();
                    if (changed) catalogs.ScheduleRefresh(WorkStore.DefaultAgentId);
                    else
                        try { await catalogs.ObserveExecutableAsync(WorkStore.DefaultAgentId, context.RequestAborted); }
                        catch { /* Discovery cannot change the connection result. */ }
                }
            }
            return Api.AuthenticationState.From(state);
        }
        catch
        {
            if (store is not null) await store.SetConnectionAsync(WorkStore.DefaultAgentId, ConnectionAvailability.Unavailable, requireIdle: false, completeChange: changing);
            throw;
        }
    }

    private static string? AccountSignature(AccountView? account)
    {
        if (account is null) return null;
        string identity = account switch
        {
            ChatgptAccountView chatgpt => "chatgpt:" + chatgpt.Email?.Trim().ToLowerInvariant(),
            ApiKeyAccountView => "apiKey",
            _ => account.GetType().Name
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
}
