using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;

namespace Goblin.Execution;

public sealed record SandboxOptions(string Namespace, string Image, string CodexHome, string GitHubCredentialFile);

public sealed class SandboxHost : IExecutionHost
{
    private readonly KubernetesApi _api;
    private readonly SandboxOptions _options;
    private readonly IExecutionHost _textHost;

    public SandboxHost(KubernetesApi api, SandboxOptions options, IExecutionHost textHost)
    {
        _api = api;
        _options = options;
        _textHost = textHost;
    }

    public RuntimeCapabilities[] Capabilities => [new("codex", true, true, true, false, false)];
    public string EnvironmentFor(Guid workId, Guid attemptId) => "goblin/" + attemptId.ToString("N");
    private string Core => "/api/v1/namespaces/" + _options.Namespace;
    private string Sandboxes => "/apis/agents.x-k8s.io/v1beta1/namespaces/" + _options.Namespace + "/sandboxes";
    private static string Name(WorkSnapshot work) => "work-" + work.Attempts[^1].Id.ToString("N");

    public async Task StartAsync(WorkSnapshot work, CancellationToken token)
    {
        if (work.Attempts[^1].Target.Repository is null) { await _textHost.StartAsync(work, token); return; }
        string name = Name(work);
        // Reserve the identity before provisioning inputs. A delayed or duplicate
        // starter that meets a cancellation fence cannot recreate credentials.
        if (!await _api.CreateAsync(Sandboxes, Manifest(work, suspended: false), token)) return;
        // Credentials are scoped to this execution's mounts, outside Work JSON.
        string codex = await File.ReadAllTextAsync(Path.Combine(_options.CodexHome, "auth.json"), token);
        string github = await File.ReadAllTextAsync(_options.GitHubCredentialFile, token);
        using var githubJson = JsonDocument.Parse(github);
        string accessToken = githubJson.RootElement.GetProperty("accessToken").GetString()!;
        JsonObject secret = Resource("v1", "Secret", name, work);
        secret["type"] = "Opaque";
        secret["stringData"] = new JsonObject { ["auth.json"] = codex, ["github-token"] = accessToken };
        await _api.CreateAsync(Core + "/secrets", secret, token);
        JsonObject input = Resource("v1", "ConfigMap", name, work);
        input["data"] = new JsonObject { ["input.json"] = JsonSerializer.Serialize(new WorkerInput(work, "/run/codex", "codex"), ExecutionFiles.Json) };
        await _api.CreateAsync(Core + "/configmaps", input, token);
        JsonObject volume = Resource("v1", "PersistentVolumeClaim", name, work);
        volume["spec"] = JsonNode.Parse("""{"accessModes":["ReadWriteOnce"],"resources":{"requests":{"storage":"2Gi"}}}""");
        await _api.CreateAsync(Core + "/persistentvolumeclaims", volume, token);
        JsonObject? reserved = await _api.GetAsync(Sandboxes + "/" + name, token);
        if (reserved?["spec"]?["operatingMode"]?.GetValue<string>() == "Suspended")
            await CleanupAsync(work, token);
    }

    public async Task<ExecutionObservation> ObserveAsync(WorkSnapshot work, bool stop, CancellationToken token)
    {
        if (work.Attempts[^1].Target.Repository is null) return await _textHost.ObserveAsync(work, stop, token);
        string name = Name(work);
        JsonObject? sandbox = await _api.GetAsync(Sandboxes + "/" + name, token);
        if (sandbox is null)
        {
            // Reserve the deterministic name in suspended mode. Any in-flight
            // create either wins first and is observed, or loses to this fence.
            await _api.CreateAsync(Sandboxes, Manifest(work, suspended: true), token);
            sandbox = await _api.GetAsync(Sandboxes + "/" + name, token);
            stop = true;
        }
        JsonObject? pods = await _api.GetAsync(Core + "/pods?labelSelector=goblin-attempt%3D" + work.Attempts[^1].Id.ToString("N"), token);
        foreach (JsonNode? pod in pods?["items"]?.AsArray() ?? [])
        {
            string podName = pod!["metadata"]!["name"]!.GetValue<string>();
            string phase = pod["status"]?["phase"]?.GetValue<string>() ?? "Pending";
            if (phase is "Running" or "Succeeded" or "Failed")
            {
                string? logs = await _api.LogsAsync(Core + "/pods/" + podName + "/log?container=execution&limitBytes=262144", token);
                string? result = logs?.Split('\n').LastOrDefault(x => x.StartsWith("GOBLIN_RESULT ", StringComparison.Ordinal));
                if (result is not null)
                    return JsonSerializer.Deserialize<ExecutionObservation>(result[14..], ExecutionFiles.Json)!;
                if (phase is "Succeeded" or "Failed") return new(ObservationKind.Failed, Failure: FailureKind.ExecutionFailed);
                string? progress = logs?.Split('\n').LastOrDefault(x => x.StartsWith("GOBLIN_PROGRESS ", StringComparison.Ordinal));
                if (!stop && progress is not null)
                    return JsonSerializer.Deserialize<ExecutionObservation>(progress[16..], ExecutionFiles.Json)!;
            }
        }
        if (stop || sandbox?["spec"]?["operatingMode"]?.GetValue<string>() == "Suspended")
        {
            await _api.PatchAsync(Sandboxes + "/" + name, new() { ["spec"] = new JsonObject { ["operatingMode"] = "Suspended" } }, token);
            // Suspension is intent. Confirm only after every owned pod is gone.
            pods = await _api.GetAsync(Core + "/pods?labelSelector=goblin-attempt%3D" + work.Attempts[^1].Id.ToString("N"), token);
            return new(pods?["items"]?.AsArray().Count == 0 ? ObservationKind.Stopped : ObservationKind.Pending);
        }
        return new(ObservationKind.Pending);
    }

    public async Task CleanupAsync(WorkSnapshot work, CancellationToken token)
    {
        if (work.Attempts[^1].Target.Repository is null) { await _textHost.CleanupAsync(work, token); return; }
        string name = Name(work);
        await _api.PatchAsync(Sandboxes + "/" + name, new() { ["spec"] = new JsonObject { ["operatingMode"] = "Suspended" } }, token);
        await _api.DeleteAsync(Core + "/secrets/" + name, token);
        await _api.DeleteAsync(Core + "/configmaps/" + name, token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (true)
        {
            JsonObject? pods = await _api.GetAsync(Core + "/pods?labelSelector=goblin-attempt%3D" + work.Attempts[^1].Id.ToString("N"), timeout.Token);
            if (pods?["items"]?.AsArray().Count == 0) break;
            await Task.Delay(200, timeout.Token);
        }
        // Keep the suspended identity fence and workspace PVC for recovery.
        // No database certificate, web data volume, or service token was mounted.
    }

    public JsonObject Manifest(WorkSnapshot work, bool suspended)
    {
        string name = Name(work);
        JsonObject resource = Resource("agents.x-k8s.io/v1beta1", "Sandbox", name, work);
        JsonNode pod = JsonNode.Parse("""
            {"metadata":{"labels":{}},"spec":{"automountServiceAccountToken":false,"restartPolicy":"Never",
             "activeDeadlineSeconds":3600,"terminationGracePeriodSeconds":15,
             "securityContext":{"runAsNonRoot":true,"runAsUser":1000,"runAsGroup":1000,"fsGroup":1000,"seccompProfile":{"type":"RuntimeDefault"}},
             "containers":[{"name":"execution","image":"","command":["dotnet","Goblin.Web.dll","--sandbox-execute"],
              "securityContext":{"allowPrivilegeEscalation":false,"readOnlyRootFilesystem":true,"capabilities":{"drop":["ALL"]}},
              "resources":{"requests":{"cpu":"100m","memory":"256Mi"},"limits":{"cpu":"2","memory":"2Gi"}},
              "volumeMounts":[{"name":"workspace","mountPath":"/workspace"},{"name":"temporary","mountPath":"/tmp"},{"name":"runtime","mountPath":"/runtime"},
                {"name":"credentials","mountPath":"/run/credentials","readOnly":true},{"name":"input","mountPath":"/run/input","readOnly":true}]}],
             "volumes":[{"name":"workspace","persistentVolumeClaim":{"claimName":""}},{"name":"temporary","emptyDir":{"sizeLimit":"256Mi"}},
               {"name":"credentials","secret":{"secretName":"","defaultMode":288}}, {"name":"input","configMap":{"name":""}},
               {"name":"runtime","emptyDir":{"sizeLimit":"256Mi"}}]}}
            """)!;
        pod["metadata"]!["labels"] = resource["metadata"]!["labels"]!.DeepClone();
        pod["spec"]!["containers"]![0]!["image"] = _options.Image;
        pod["spec"]!["volumes"]![0]!["persistentVolumeClaim"]!["claimName"] = name;
        pod["spec"]!["volumes"]![2]!["secret"]!["secretName"] = name;
        pod["spec"]!["volumes"]![3]!["configMap"]!["name"] = name;
        resource["spec"] = new JsonObject { ["operatingMode"] = suspended ? "Suspended" : "Running", ["shutdownPolicy"] = "Retain", ["service"] = false, ["podTemplate"] = pod };
        return resource;
    }

    private static JsonObject Resource(string apiVersion, string kind, string name, WorkSnapshot work) => new()
    {
        ["apiVersion"] = apiVersion,
        ["kind"] = kind,
        ["metadata"] = new JsonObject
        {
            ["name"] = name,
            ["labels"] = new JsonObject
            {
                ["goblin-attempt"] = work.Attempts[^1].Id.ToString("N"),
                ["goblin-work"] = work.Id.ToString("N"),
                ["app"] = "goblin-execution"
            }
        }
    };
}
