using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;

namespace Goblin.Execution;

public sealed record SandboxOptions(string Namespace, string Image, string CodexHome, string RepositoryUrl);

public sealed class SandboxHost : IExecutionHost
{
    private readonly KubernetesApi _api;
    private readonly SandboxOptions _options;
    private readonly IExecutionHost _textHost;
    private readonly IRepositoryBroker _repositories;

    public SandboxHost(KubernetesApi api, SandboxOptions options, IExecutionHost textHost, IRepositoryBroker repositories)
    {
        if (!ValidNamespace(options.Namespace))
            throw new ArgumentException("Invalid execution namespace.", nameof(options));
        _api = api;
        _options = options;
        _textHost = textHost;
        _repositories = repositories;
    }

    public RuntimeCapabilities[] Capabilities => [new("codex", true, true, true, false, false)];
    // Claims retain the global IDs; the visible suffix comes from the Work's
    // durable execution history, not from the global attempt sequence.
    public string EnvironmentFor(long workId, long attemptId) => "k8s/" + _options.Namespace + "/run-" +
        workId.ToString(CultureInfo.InvariantCulture) + "/" + attemptId.ToString(CultureInfo.InvariantCulture);

    private static bool ValidNamespace(string value) => Regex.IsMatch(value, "\\A[a-z0-9](?:[-a-z0-9]{0,61}[a-z0-9])?\\z", RegexOptions.CultureInvariant);

    private static string RunName(WorkSnapshot work) => "run-" + work.Id.ToString(CultureInfo.InvariantCulture) + "-" +
        work.Attempts.Length.ToString(CultureInfo.InvariantCulture);

    private SandboxAddress Address(WorkSnapshot work)
    {
        AttemptSnapshot attempt = work.Attempts[^1];
        string id = attempt.Id.ToString(CultureInfo.InvariantCulture);
        // Use the claimed namespace even if configuration changes after dispatch.
        if (attempt.EnvironmentReference is null) return new(_options.Namespace, RunName(work));
        string[] parts = attempt.EnvironmentReference.Split('/');
        if (parts.Length == 4 && parts[0] == "k8s" && ValidNamespace(parts[1]) &&
            parts[2] == "run-" + work.Id.ToString(CultureInfo.InvariantCulture) && parts[3] == id)
            return new(parts[1], RunName(work));
        throw new IOException("Unrecognized execution environment reference.");
    }

    private sealed record SandboxAddress(string Namespace, string Name)
    {
        public string Core => "/api/v1/namespaces/" + Namespace;
        public string Sandboxes => "/apis/agents.x-k8s.io/v1beta1/namespaces/" + Namespace + "/sandboxes";
    }

    public async Task StartAsync(WorkSnapshot work, CancellationToken token)
    {
        if (work.Attempts[^1].Target.Repository is null) { await _textHost.StartAsync(work, token); return; }
        SandboxAddress address = Address(work);
        string name = address.Name;
        // Reserve the identity before provisioning inputs. A delayed or duplicate
        // starter that meets a cancellation fence cannot recreate credentials.
        if (!await _api.CreateAsync(address.Sandboxes, Manifest(work, suspended: false), token)) return;
        // Credentials are scoped to this execution's mounts, outside Work JSON.
        string codex = await File.ReadAllTextAsync(Path.Combine(_options.CodexHome, "auth.json"), token);
        string capability = await _repositories.PrepareAsync(work, token);
        JsonObject secret = Resource("v1", "Secret", name, work);
        secret["type"] = "Opaque";
        secret["stringData"] = new JsonObject
        {
            ["auth.json"] = codex,
            ["repository-capability"] = capability,
            ["repository-url"] = _options.RepositoryUrl
        };
        await _api.CreateAsync(address.Core + "/secrets", secret, token);
        JsonObject input = Resource("v1", "ConfigMap", name, work);
        input["data"] = new JsonObject { ["input.json"] = JsonSerializer.Serialize(new WorkerInput(work, "/run/codex", "codex"), ExecutionFiles.Json) };
        await _api.CreateAsync(address.Core + "/configmaps", input, token);
        JsonObject volume = Resource("v1", "PersistentVolumeClaim", name, work);
        volume["spec"] = JsonNode.Parse("""{"accessModes":["ReadWriteOnce"],"resources":{"requests":{"storage":"2Gi"}}}""");
        await _api.CreateAsync(address.Core + "/persistentvolumeclaims", volume, token);
        JsonObject? reserved = await _api.GetAsync(address.Sandboxes + "/" + name, token);
        if (reserved?["spec"]?["operatingMode"]?.GetValue<string>() == "Suspended")
            await CleanupAsync(work, token);
    }

    public async Task<ExecutionObservation> ObserveAsync(WorkSnapshot work, bool stop, CancellationToken token)
    {
        if (work.Attempts[^1].Target.Repository is null) return await _textHost.ObserveAsync(work, stop, token);
        SandboxAddress address = Address(work);
        string name = address.Name;
        ExecutionObservation? repositoryObservation = await _repositories.ObserveAsync(work, token);
        if (repositoryObservation?.Kind == ObservationKind.Uncertain) stop = true;
        JsonObject? sandbox = await _api.GetAsync(address.Sandboxes + "/" + name, token);
        if (sandbox is null)
        {
            // Reserve the deterministic name in suspended mode. Any in-flight
            // create either wins first and is observed, or loses to this fence.
            await _api.CreateAsync(address.Sandboxes, Manifest(work, suspended: true), token);
            sandbox = await _api.GetAsync(address.Sandboxes + "/" + name, token);
            stop = true;
        }
        JsonObject? pods = await _api.GetAsync(address.Core + "/pods?labelSelector=goblin-attempt%3D" + work.Attempts[^1].Id.ToString(CultureInfo.InvariantCulture), token);
        foreach (JsonNode? pod in pods?["items"]?.AsArray() ?? [])
        {
            string podName = pod!["metadata"]!["name"]!.GetValue<string>();
            string phase = pod["status"]?["phase"]?.GetValue<string>() ?? "Pending";
            if (phase is "Running" or "Succeeded" or "Failed")
            {
                string? logs = await _api.LogsAsync(address.Core + "/pods/" + podName + "/log?container=execution&limitBytes=262144", token);
                string? result = logs?.Split('\n').LastOrDefault(x => x.StartsWith("GOBLIN_RESULT ", StringComparison.Ordinal));
                if (result is not null && repositoryObservation is null)
                    return JsonSerializer.Deserialize<ExecutionObservation>(result[14..], ExecutionFiles.Json)!;
                if (phase is "Succeeded" or "Failed" && repositoryObservation is null) return new(ObservationKind.Failed, Failure: FailureKind.ExecutionFailed);
                string? progress = logs?.Split('\n').LastOrDefault(x => x.StartsWith("GOBLIN_PROGRESS ", StringComparison.Ordinal));
                if (!stop && progress is not null)
                    return JsonSerializer.Deserialize<ExecutionObservation>(progress[16..], ExecutionFiles.Json)!;
            }
        }
        if (stop || sandbox?["spec"]?["operatingMode"]?.GetValue<string>() == "Suspended")
        {
            await _api.PatchAsync(address.Sandboxes + "/" + name, new() { ["spec"] = new JsonObject { ["operatingMode"] = "Suspended" } }, token);
            // Suspension is intent. Confirm only after every owned pod is gone.
            pods = await _api.GetAsync(address.Core + "/pods?labelSelector=goblin-attempt%3D" + work.Attempts[^1].Id.ToString(CultureInfo.InvariantCulture), token);
            if (pods?["items"]?.AsArray().Count == 0)
            {
                try { await _repositories.StopAsync(work, token); }
                catch { return new(ObservationKind.Uncertain, Failure: FailureKind.ExecutionFailed); }
                return new(ObservationKind.Stopped);
            }
            return repositoryObservation ?? new(ObservationKind.Pending);
        }
        return new(ObservationKind.Pending);
    }

    public async Task CleanupAsync(WorkSnapshot work, CancellationToken token)
    {
        if (work.Attempts[^1].Target.Repository is null) { await _textHost.CleanupAsync(work, token); return; }
        SandboxAddress address = Address(work);
        string name = address.Name;
        await _repositories.StopAsync(work, token);
        await _api.PatchAsync(address.Sandboxes + "/" + name, new() { ["spec"] = new JsonObject { ["operatingMode"] = "Suspended" } }, token);
        await _api.DeleteAsync(address.Core + "/secrets/" + name, token);
        await _api.DeleteAsync(address.Core + "/configmaps/" + name, token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (true)
        {
            JsonObject? pods = await _api.GetAsync(address.Core + "/pods?labelSelector=goblin-attempt%3D" + work.Attempts[^1].Id.ToString(CultureInfo.InvariantCulture), timeout.Token);
            if (pods?["items"]?.AsArray().Count == 0) break;
            await Task.Delay(200, timeout.Token);
        }
        // Keep the suspended identity fence and workspace PVC for recovery.
        // No database certificate, web data volume, or service token was mounted.
        await _repositories.ReleaseAsync(work, token);
    }

    public JsonObject Manifest(WorkSnapshot work, bool suspended)
    {
        SandboxAddress address = Address(work);
        string name = address.Name;
        JsonObject resource = Resource("agents.x-k8s.io/v1beta1", "Sandbox", name, work);
        resource["metadata"]!["namespace"] = address.Namespace;
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
                ["goblin-attempt"] = work.Attempts[^1].Id.ToString(CultureInfo.InvariantCulture),
                ["goblin-work"] = work.Id.ToString(CultureInfo.InvariantCulture),
                ["app"] = "goblin-execution"
            }
        }
    };
}
