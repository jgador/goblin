using System;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.Work;
using Goblin.Contracts.Runtime;
using Goblin.Integrations.GitHub;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Api = Goblin.Web.Http.Contracts;

namespace Goblin.Web;

internal sealed class GitHubEndpoints
{
    private readonly ApplicationOptions _options;
    // One gate per application, shared by every GitHub connection request.
    private readonly SemaphoreSlim _gate = new(1);

    public GitHubEndpoints(ApplicationOptions options) => _options = options;

    public void Map(WebApplication app)
    {
        app.MapGet("/api/github", (Delegate)StatusAsync);
        app.MapPost("/api/github/connect", (Delegate)ConnectAsync);
        app.MapPost("/api/github/disconnect", (Delegate)DisconnectAsync);
        app.MapPost("/api/github/cancel", (Delegate)CancelAsync);
        app.MapPost("/api/github/check", (Delegate)CheckAsync);
        if (_options.EnableWork)
        {
            app.MapGet("/api/github/repositories", RepositoriesAsync);
            app.MapGet("/api/github/available-repositories", AvailableRepositoriesAsync);
            app.MapPost("/api/github/repositories", SetRepositoryAsync);
        }
    }

    private Task<Api.GitHubState> StatusAsync(HttpContext context) => GitHubAsync(context, "status");

    private Task<Api.GitHubState> ConnectAsync(HttpContext context) => GitHubAsync(context, "connect");

    private Task<Api.GitHubState> DisconnectAsync(HttpContext context) => GitHubAsync(context, "disconnect");

    private Task<Api.GitHubState> CancelAsync(HttpContext context) => GitHubAsync(context, "cancel");

    private Task<Api.GitHubState> CheckAsync(HttpContext context) => GitHubAsync(context, "check");

    private async Task<Api.GitHubState> GitHubAsync(HttpContext context, string action)
    {
        await _gate.WaitAsync(context.RequestAborted);
        GitHubConnection github = context.RequestServices.GetRequiredService<GitHubConnection>();
        GitHubStore? store = _options.EnableWork ? context.RequestServices.GetRequiredService<GitHubStore>() : null;
        try
        {
            if (action is "connect" or "disconnect" or "cancel")
            {
                if (store is not null) await store.BeginChangeAsync(context.RequestAborted);
            }
            GitHubState state = action switch
            {
                "connect" => await github.StartAsync(),
                "disconnect" or "cancel" => await github.DisconnectAsync(),
                "check" => await github.CheckAsync(context.RequestAborted),
                _ => await github.StatusAsync()
            };
            if (store is not null) await store.ObserveAsync(state.Account, state.Status);
            return Api.GitHubState.From(state);
        }
        finally { _gate.Release(); }
    }

    private static async Task<IResult> RepositoriesAsync(GitHubStore store, CancellationToken token) =>
        WorkResponse.Json(Array.ConvertAll(await store.RepositoriesAsync(token), Api.EnabledRepository.From));

    private static async Task<IResult> AvailableRepositoriesAsync(GitHubConnection github, int? page, CancellationToken token) =>
        WorkResponse.Json(Array.ConvertAll(await github.RepositoriesAsync(page ?? 1, token), Api.RepositoryInfo.From));

    private static async Task<IResult> SetRepositoryAsync(HttpContext context, GitHubConnection github, GitHubStore store)
    {
        RepositoryAccount account = (await github.StatusAsync()).Account ?? throw new PublicError("repository_unavailable", "Connect GitHub first.", 409);
        RepositorySelectionRequest request = ApiRequest.Body<RepositorySelectionRequest>(context);
        RepositoryInfo repository = await github.RepositoryAsync(request.Repository ?? "", context.RequestAborted);
        await store.SetRepositoryAsync(repository, request.Enabled == "true", account.Generation, context.RequestAborted);
        return WorkResponse.Json(Array.ConvertAll(await store.RepositoriesAsync(), Api.EnabledRepository.From));
    }
}
