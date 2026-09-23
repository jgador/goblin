using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.Workspaces;
using Goblin.Execution;
using Microsoft.AspNetCore.Http;

namespace Goblin.Web;

public static class WorkspaceTerminal
{
    public static async Task ConnectAsync(HttpContext context, long workId, long sessionId, string ns,
        InspectionStore store, KubernetesApi kubernetes, Workspace access)
    {
        access.ValidateOrigin(context.Request);
        await store.RequireAvailableAsync(workId, sessionId, context.RequestAborted);
        if (!context.WebSockets.IsWebSocketRequest) throw new PublicError("invalid_command", "Open a terminal connection.");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        using ClientWebSocket upstream = await kubernetes.ExecAsync(ns, InspectionHost.Name(sessionId), "inspect",
            ["env", "TERM=dumb", "HOME=/tmp", "HISTFILE=/dev/null", "/bin/bash", "--noprofile", "--norc", "-i"], true, stop.Token);
        using WebSocket browser = await context.WebSockets.AcceptWebSocketAsync();
        async Task Input()
        {
            byte[] buffer = new byte[8193]; buffer[0] = 0;
            while (!stop.IsCancellationRequested)
            {
                ValueWebSocketReceiveResult message = await browser.ReceiveAsync(buffer.AsMemory(1), stop.Token);
                if (message.MessageType == WebSocketMessageType.Close) return;
                if (message.MessageType != WebSocketMessageType.Text || !message.EndOfMessage) return;
                await upstream.SendAsync(buffer.AsMemory(0, message.Count + 1), WebSocketMessageType.Binary, true, stop.Token);
            }
        }
        async Task Output()
        {
            byte[] buffer = new byte[32768];
            int channel = -1;
            while (!stop.IsCancellationRequested)
            {
                ValueWebSocketReceiveResult message = await upstream.ReceiveAsync(buffer.AsMemory(), stop.Token);
                if (message.MessageType == WebSocketMessageType.Close) return;
                int offset = 0;
                if (channel == -1 && message.Count > 0) { channel = buffer[0]; offset = 1; }
                if (message.Count > offset && channel is 1 or 2)
                    await browser.SendAsync(buffer.AsMemory(offset, message.Count - offset), WebSocketMessageType.Binary, true, stop.Token);
                if (channel == 3) return;
                if (message.EndOfMessage) channel = -1;
            }
        }
        async Task Authorization()
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stop.Token);
                if (access.SessionId(context.Request) is null) return;
                await store.RequireAvailableAsync(workId, sessionId, stop.Token);
            }
        }
        Task[] tasks = [Input(), Output(), Authorization()];
        try { await Task.WhenAny(tasks); }
        finally
        {
            stop.Cancel(); upstream.Abort(); browser.Abort();
            try { await Task.WhenAll(tasks); } catch { }
        }
    }
}
