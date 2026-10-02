using System;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts.Runtime;
using Goblin.Execution;
using Goblin.Integrations.Slack;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Goblin.Tests;

public sealed class TypedJsonSocketTests
{
    [Fact]
    public async Task SocketModeDeserializesFragmentedTextAndStillRecognizesCloseFrames()
    {
        await using WebApplication app = Server();
        app.Map("/socket", async context =>
        {
            using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
            byte[] json = Encoding.UTF8.GetBytes("{\"type\":\"hello\",\"connection_info\":{\"app_id\":\"A123\"},\"future\":{\"x\":1}}");
            await SendFragmentsAsync(socket, json, WebSocketMessageType.Text);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", context.RequestAborted);
        });
        await app.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri(app.Urls.Single().Replace("http:", "ws:") + "/socket"), deadline.Token);
        SlackSocketEnvelope hello = (await SlackApi.ReceiveAsync(client, deadline.Token))!;
        Assert.Equal("hello", hello.Type);
        Assert.Equal("A123", hello.ConnectionInfo!.AppId);
        Assert.Null(await SlackApi.ReceiveAsync(client, deadline.Token));
    }

    [Theory]
    [InlineData("Success")]
    [InlineData("Failure")]
    public async Task InspectionReadsTypedFilesOnlyAfterSuccessfulFragmentedExecStatus(string status)
    {
        await using WebApplication app = Server();
        app.Map("/{**path}", async context =>
        {
            Assert.EndsWith("/exec", context.Request.Path.Value);
            Assert.Equal("inspect", context.Request.Query["container"]);
            using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync("v4.channel.k8s.io");
            byte[] files = Encoding.UTF8.GetBytes("\u0001{\"files\":[{\"path\":\"repository/a.txt\",\"size\":7}],\"truncated\":false}");
            await SendFragmentsAsync(socket, files, WebSocketMessageType.Binary);
            byte[] result = Encoding.UTF8.GetBytes("\u0003{\"status\":\"" + status + "\",\"unconsumed\":{\"details\":1}}");
            await SendFragmentsAsync(socket, result, WebSocketMessageType.Binary);
        });
        await app.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var api = new KubernetesApi(app.Urls.Single());
        var session = new InspectionAllocation(1, 1, 1, "k8s/agents/work-1");
        if (status == "Failure")
            await Assert.ThrowsAsync<IOException>(() => InspectionFiles.ReadAsync(api, session, null, deadline.Token));
        else
        {
            WorkspaceFilesResponse result = await InspectionFiles.ReadAsync(api, session, null, deadline.Token);
            WorkspaceFileEntry file = Assert.Single(result.Files!);
            Assert.Equal("repository/a.txt", file.Path);
            Assert.Equal(7, file.Size);
            Assert.False(result.Truncated);
            Assert.Null(result.Path);
        }
    }

    private static WebApplication Server()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        WebApplication app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.UseWebSockets();
        return app;
    }

    private static async Task SendFragmentsAsync(WebSocket socket, byte[] bytes, WebSocketMessageType type)
    {
        await socket.SendAsync(bytes.AsMemory(0, 3), type, false, CancellationToken.None);
        await socket.SendAsync(bytes.AsMemory(3), type, true, CancellationToken.None);
    }
}
