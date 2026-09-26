using System;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts.Runtime;

namespace Goblin.Execution;

public static class InspectionFiles
{
    public static async Task<JsonElement> ReadAsync(KubernetesApi api, InspectionAllocation session, string? path, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        string[] command = path is null ? ["dotnet", "/app/Goblin.Web.dll", "--workspace-files"]
            : ["dotnet", "/app/Goblin.Web.dll", "--workspace-files", path];
        using ClientWebSocket socket = await api.ExecAsync(InspectionHost.NamespaceFor(session), InspectionHost.Name(session.Id), "inspect", command, false, deadline.Token);
        using var output = new MemoryStream();
        using var status = new MemoryStream();
        byte[] buffer = new byte[32768];
        int channel = -1;
        long received = 0;
        while (true)
        {
            ValueWebSocketReceiveResult message = await socket.ReceiveAsync(buffer.AsMemory(), deadline.Token);
            if (message.MessageType == WebSocketMessageType.Close) throw new IOException("Workspace read was interrupted.");
            int offset = 0;
            if (channel == -1 && message.Count > 0) { channel = buffer[0]; offset = 1; }
            if ((received += message.Count) > 8 * 1024 * 1024) throw new IOException("Workspace preview exceeds limits.");
            if (message.Count > offset && channel is 1 or 3)
                (channel == 1 ? output : status).Write(buffer, offset, message.Count - offset);
            if (!message.EndOfMessage) continue;
            if (channel == 3)
            {
                using JsonDocument result = JsonDocument.Parse(status.ToArray());
                if (result.RootElement.GetProperty("status").GetString() != "Success") throw new IOException("Workspace read failed.");
                using JsonDocument data = JsonDocument.Parse(output.ToArray());
                return data.RootElement.Clone();
            }
            channel = -1;
        }
    }
}
