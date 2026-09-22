using System;
using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts.Runtime;

namespace Goblin.Execution;

public sealed class InspectionHost : IInspectionHost
{
    private readonly KubernetesApi _api;
    private readonly SandboxOptions _options;
    public InspectionHost(KubernetesApi api, SandboxOptions options) { _api = api; _options = options; }
    public static string Name(Guid id) => "inspect-" + id.ToString("N");
    private string Core => "/api/v1/namespaces/" + _options.Namespace;
    private string Sandboxes => "/apis/agents.x-k8s.io/v1beta1/namespaces/" + _options.Namespace + "/sandboxes";
    public async Task StartAsync(InspectionAllocation session, string capability, CancellationToken token)
    {
        string name = Name(session.Id);
        if (!await _api.CreateAsync(Sandboxes, Manifest(session, false), token)) return;
        if (session.CheckpointId is null)
        {
            if (await _api.GetAsync(Core + "/persistentvolumeclaims/" + session.SourceVolume, token) is null)
                throw new IOException("Saved workspace is unavailable.");
            return;
        }
        await _api.CreateAsync(Core + "/secrets", new JsonObject
        {
            ["apiVersion"] = "v1",
            ["kind"] = "Secret",
            ["metadata"] = new JsonObject { ["name"] = name },
            ["stringData"] = new JsonObject { ["capability"] = capability, ["url"] = _options.RepositoryUrl + "/internal/workspaces/" + session.Id }
        }, token);
        // A concurrent Stop creates/patches the same identity, so it fences delayed starts.
        JsonObject? saved = await _api.GetAsync(Sandboxes + "/" + name, token);
        if (saved?["spec"]?["operatingMode"]?.GetValue<string>() == "Suspended") await _api.DeleteAsync(Core + "/secrets/" + name, token);
    }
    public async Task<string> ObserveAsync(InspectionAllocation session, CancellationToken token)
    {
        string name = Name(session.Id);
        JsonObject? pod = await _api.GetAsync(Core + "/pods/" + name, token);
        if (pod is null)
        {
            JsonObject? sandbox = await _api.GetAsync(Sandboxes + "/" + name, token);
            return sandbox?["spec"]?["operatingMode"]?.GetValue<string>() == "Running" ? "Pending" : "Missing";
        }
        string phase = pod["status"]?["phase"]?.GetValue<string>() ?? "Pending";
        if (phase == "Running")
        {
            await _api.DeleteAsync(Core + "/secrets/" + name, token);
            return "Running";
        }
        if (phase is "Failed" or "Succeeded") return "Failed";
        JsonArray? statuses = pod["status"]?["initContainerStatuses"]?.AsArray();
        if (statuses is not null)
            foreach (JsonNode? status in statuses)
                if (status?["state"]?["terminated"]?["exitCode"]?.GetValue<int>() is > 0) return "Failed";
        return "Pending";
    }
    public async Task StopAsync(InspectionAllocation session, CancellationToken token)
    {
        string name = Name(session.Id);
        await _api.CreateAsync(Sandboxes, Manifest(session, true), token);
        await _api.PatchAsync(Sandboxes + "/" + name, new JsonObject { ["spec"] = new JsonObject { ["operatingMode"] = "Suspended" } }, token);
        await _api.DeleteAsync(Core + "/secrets/" + name, token);
    }
    public JsonObject Manifest(InspectionAllocation session, bool suspended)
    {
        string name = Name(session.Id);
        JsonObject pod = JsonNode.Parse("""
        {"metadata":{"labels":{"app":"goblin-inspection"}},"spec":{
          "automountServiceAccountToken":false,"restartPolicy":"Never","terminationGracePeriodSeconds":2,
          "securityContext":{"runAsNonRoot":true,"runAsUser":1000,"runAsGroup":1000,"seccompProfile":{"type":"RuntimeDefault"}},
          "containers":[{"name":"inspect","image":"","command":["sleep","infinity"],"workingDir":"/workspace/repository",
            "securityContext":{"allowPrivilegeEscalation":false,"readOnlyRootFilesystem":true,"capabilities":{"drop":["ALL"]}},
            "resources":{"requests":{"cpu":"10m","memory":"32Mi"},"limits":{"cpu":"500m","memory":"256Mi"}},
            "volumeMounts":[{"name":"workspace","mountPath":"/workspace","readOnly":true},{"name":"temporary","mountPath":"/tmp"}]}],
          "volumes":[{"name":"workspace"},{"name":"temporary","emptyDir":{"sizeLimit":"128Mi"}}]}}
        """)!.AsObject();
        pod["spec"]!["containers"]![0]!["image"] = _options.Image;
        if (session.CheckpointId is null)
            pod["spec"]!["volumes"]![0]!["persistentVolumeClaim"] = new JsonObject { ["claimName"] = session.SourceVolume, ["readOnly"] = true };
        else
        {
            pod["spec"]!["securityContext"]!["fsGroup"] = 1000;
            pod["spec"]!["volumes"]![0]!["emptyDir"] = new JsonObject { ["sizeLimit"] = "4Gi" };
            pod["spec"]!["volumes"]!.AsArray().Add(new JsonObject { ["name"] = "restore", ["secret"] = new JsonObject { ["secretName"] = name } });
            JsonObject init = pod["spec"]!["containers"]![0]!.DeepClone().AsObject();
            init["name"] = "restore";
            init.Remove("workingDir");
            init["command"] = new JsonArray("dotnet", "Goblin.Web.dll", "--inspection-restore");
            init["volumeMounts"]![0]!["readOnly"] = false;
            init["volumeMounts"]!.AsArray().Add(new JsonObject { ["name"] = "restore", ["mountPath"] = "/run/restore", ["readOnly"] = true });
            init["resources"]!["limits"]!["memory"] = "512Mi";
            pod["spec"]!["initContainers"] = new JsonArray(init);
        }
        return new JsonObject
        {
            ["apiVersion"] = "agents.x-k8s.io/v1beta1",
            ["kind"] = "Sandbox",
            ["metadata"] = new JsonObject { ["name"] = name, ["namespace"] = _options.Namespace },
            ["spec"] = new JsonObject { ["operatingMode"] = suspended ? "Suspended" : "Running", ["shutdownPolicy"] = "Retain", ["service"] = false, ["podTemplate"] = pod }
        };
    }
    public static async Task<int> RestoreAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            client.DefaultRequestHeaders.Add("X-Goblin-Inspection", (await File.ReadAllTextAsync("/run/restore/capability")).Trim());
            using HttpResponseMessage response = await client.GetAsync((await File.ReadAllTextAsync("/run/restore/url")).Trim(), HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            string file = "/tmp/workspace.tar.gz";
            await using (FileStream output = File.Create(file)) await response.Content.CopyToAsync(output);
            WorkspaceFiles.Unpack(file, "/workspace"); File.Delete(file);
            return 0;
        }
        catch { Console.Error.WriteLine("Workspace restoration needs attention."); return 1; }
    }
}
