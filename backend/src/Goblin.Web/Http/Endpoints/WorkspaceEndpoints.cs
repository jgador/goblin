using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.Work;
using Goblin.Application.Workspaces;
using Goblin.Contracts.Runtime;
using Goblin.Execution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

using Api = Goblin.Web.Http.Contracts;

namespace Goblin.Web;

internal sealed class WorkspaceEndpoints
{
    private readonly bool _repositoryListener;

    public WorkspaceEndpoints(bool repositoryListener) => _repositoryListener = repositoryListener;

    public void Map(WebApplication app)
    {
        app.MapGet("/api/work/{id:long}/workspace", GetAsync);
        if (_repositoryListener)
        {
            app.MapPost("/api/work/{id:long}/workspace/sessions", OpenAsync);
            app.MapPost("/api/work/{id:long}/workspace/sessions/{session:long}/stop", StopAsync);
            app.MapGet("/api/work/{id:long}/workspace/sessions/{session:long}/files", FilesAsync);
            app.MapGet("/api/work/{id:long}/workspace/sessions/{session:long}/terminal", TerminalAsync);
        }
    }

    private async Task<IResult> GetAsync(long id, WorkspaceCheckpoints checkpoints, InspectionStore sessions, WorkStore store, CancellationToken token)
    {
        await store.GetAsync(id, token);
        return WorkResponse.Json(new Api.WorkspaceView(
            Array.ConvertAll(await checkpoints.ListAsync(id, token), Api.WorkspaceCheckpoint.From),
            Array.ConvertAll(await sessions.ListAsync(id, token), Api.InspectionView.From), _repositoryListener));
    }

    private static async Task<IResult> OpenAsync(long id, HttpContext context, InspectionStore sessions)
    {
        Dictionary<string, JsonElement> body = ApiRequest.Body(context);
        InspectionRequest request = JsonSerializer.Deserialize<InspectionRequest>(JsonSerializer.Serialize(body), WorkStore.Json)!;
        return WorkResponse.Json(Api.InspectionView.From(await sessions.OpenAsync(id, request.Id, request.AttemptId, CancellationToken.None)));
    }

    private static async Task<IResult> StopAsync(long id, long session, InspectionStore sessions)
    {
        await sessions.StopAsync(id, session, CancellationToken.None);
        return Results.NoContent();
    }

    private static async Task<IResult> FilesAsync(long id, long session, string? path, InspectionStore sessions, KubernetesApi api, CancellationToken token)
    {
        await sessions.RequireAvailableAsync(id, session, token);
        InspectionAllocation allocation = await sessions.GetAsync(session, token);
        JsonElement files = await InspectionFiles.ReadAsync(api, allocation, path, token);
        await sessions.RequireAvailableAsync(id, session, token);
        return Results.Json(files);
    }

    private static Task TerminalAsync(long id, long session, HttpContext context, InspectionStore sessions, KubernetesApi api, Workspace workspace) =>
        WorkspaceTerminal.ConnectAsync(context, id, session, sessions, api, workspace);
}
