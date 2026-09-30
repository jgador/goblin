using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.Repositories;
using Goblin.Application.Work;
using Goblin.Application.Workspaces;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;
using Goblin.Execution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Goblin.Web;

internal static class RepositoryEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/internal/repository/{attemptId:long}/input", Input);
        app.MapPost("/internal/repository/{attemptId:long}/operation-id", ReserveOperationIdAsync);
        app.MapPost("/internal/repository/{attemptId:long}/{operationId:long}/{kind}", EnqueueAsync);
        app.MapGet("/internal/repository/{attemptId:long}/operations/{operationId:long}", StatusAsync);
        app.MapGet("/internal/repository/{attemptId:long}/current", CurrentAsync);
        app.MapGet("/internal/repository/{attemptId:long}/setup-memory", ReadSetupAsync);
        app.MapPost("/internal/repository/{attemptId:long}/setup-memory", SaveSetupAsync);
        app.MapPost("/internal/repository/{attemptId:long}/checkpoint", CheckpointAsync);
    }

    private static IResult Input(long attemptId, RepositoryBroker broker) =>
        Results.File(broker.InputPath(attemptId), "application/octet-stream");

    private static async Task<IResult> ReserveOperationIdAsync(long attemptId, RepositoryBroker broker, CancellationToken token) =>
        Results.Json(await broker.ReserveOperationIdAsync(attemptId, token));

    private static async Task<IResult> EnqueueAsync(long attemptId, long operationId, string kind, HttpContext context, RepositoryBroker broker)
    {
        if (!RepositoryOperationNames.TryParse(kind, out RepositoryOperationKind operation))
            throw new ApplicationFailure("repository_operation_unavailable");
        return Results.Json(await broker.EnqueueAsync(attemptId, operationId, operation, context.Request.Body, context.RequestAborted));
    }

    private static async Task<IResult> StatusAsync(long attemptId, long operationId, RepositoryBroker broker, CancellationToken token) =>
        Results.Json(await broker.StatusAsync(attemptId, operationId, token));

    private static async Task<IResult> CurrentAsync(long attemptId, RepositoryBroker broker, CancellationToken token) =>
        Results.Json(await broker.CurrentAsync(attemptId, token), ExecutionFiles.Json);

    private static async Task<IResult> ReadSetupAsync(long attemptId, RepositorySetupStore store, CancellationToken token) =>
        Results.Json(await store.ReadAsync(attemptId, token), ExecutionFiles.Json);

    private static async Task<IResult> SaveSetupAsync(long attemptId, HttpContext context, RepositorySetupStore store)
    {
        await store.SaveAsync(attemptId, context.Request.Body, context.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> CheckpointAsync(long attemptId, HttpContext context, RepositoryBroker broker, WorkspaceCheckpoints checkpoints)
    {
        Dictionary<string, JsonElement> body = await ApiRequest.ReadBodyAsync(context.Request);
        WorkspaceCheckpointWrite request = JsonSerializer.Deserialize<WorkspaceCheckpointWrite>(JsonSerializer.Serialize(body), ExecutionFiles.Json)
            ?? throw new ApplicationFailure("invalid_command");
        return Results.Json(await checkpoints.SaveAsync(attemptId, request.TurnNumber, request.CommitSha, broker, context.RequestAborted), ExecutionFiles.Json);
    }
}
