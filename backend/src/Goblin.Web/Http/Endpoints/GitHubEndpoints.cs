using System;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.Connections;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Api = Goblin.Web.Http.Contracts;

namespace Goblin.Web;

internal sealed class GitHubEndpoints
{
    private readonly ApplicationOptions _options;

    public GitHubEndpoints(ApplicationOptions options) => _options = options;

    public void Map(WebApplication app)
    {
        app.MapGet("/api/github", StatusAsync);
        app.MapPost("/api/github/connect", ConnectAsync);
        app.MapPost("/api/github/disconnect", DisconnectAsync);
        app.MapPost("/api/github/cancel", CancelAsync);
        app.MapPost("/api/github/check", CheckAsync);
        if (_options.EnableWork)
        {
            app.MapGet("/api/github/repositories", GitRepositoriesAsync);
            app.MapGet("/api/github/available-repositories", AvailableGitRepositoriesAsync);
            app.MapPost("/api/github/repositories", SetGitRepositoryAsync);
        }
    }

    private static async Task<Api.GitHubState> StatusAsync(GitHubConnectionService service, CancellationToken token) =>
        Api.GitHubState.From(await service.StatusAsync(token));

    private static async Task<Api.GitHubState> ConnectAsync(GitHubConnectionService service, CancellationToken token) =>
        Api.GitHubState.From(await service.ConnectAsync(token));

    private static async Task<Api.GitHubState> DisconnectAsync(GitHubConnectionService service, CancellationToken token) =>
        Api.GitHubState.From(await service.DisconnectAsync(token));

    private static async Task<Api.GitHubState> CancelAsync(GitHubConnectionService service, CancellationToken token) =>
        Api.GitHubState.From(await service.CancelAsync(token));

    private static async Task<Api.GitHubState> CheckAsync(GitHubConnectionService service, CancellationToken token) =>
        Api.GitHubState.From(await service.CheckAsync(token));

    private static async Task<IResult> GitRepositoriesAsync(GitHubConnectionService service, CancellationToken token) =>
        WorkResponse.Json(Array.ConvertAll(await service.GitRepositoriesAsync(token), Api.EnabledGitRepository.From));

    private static async Task<IResult> AvailableGitRepositoriesAsync(GitHubConnectionService service, int? page, CancellationToken token) =>
        WorkResponse.Json(Array.ConvertAll(await service.AvailableGitRepositoriesAsync(page ?? 1, token), Api.GitRepositoryInfo.From));

    private static async Task<IResult> SetGitRepositoryAsync(HttpContext context, GitHubConnectionService service)
    {
        GitRepositorySelectionRequest request = ApiRequest.Body<GitRepositorySelectionRequest>(context);
        return WorkResponse.Json(Array.ConvertAll(await service.SetGitRepositoryAsync(request.GitRepository ?? "",
            request.Enabled == "true", context.RequestAborted), Api.EnabledGitRepository.From));
    }
}
