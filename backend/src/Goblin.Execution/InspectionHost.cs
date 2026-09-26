using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts.Runtime;
using K = Goblin.Execution.Kubernetes;

namespace Goblin.Execution;

public sealed class InspectionHost : IInspectionHost
{
    private readonly KubernetesApi _api;
    private readonly SandboxOptions _options;
    public InspectionHost(KubernetesApi api, SandboxOptions options) { _api = api; _options = options; }
    public static string Name(long id) => "inspect-" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public static string NamespaceFor(InspectionAllocation session) => Source(session).Namespace;
    private static (string Namespace, string Volume) Source(InspectionAllocation session)
    {
        string[] parts = session.SourceVolume.Split('/');
        if (parts.Length == 3 && parts[0] == "k8s" && ValidName(parts[1]) &&
            parts[2] == "work-" + session.WorkId.ToString(System.Globalization.CultureInfo.InvariantCulture)) return (parts[1], parts[2]);
        throw new IOException("Unrecognized workspace reference.");
    }
    private static bool ValidName(string name) => Regex.IsMatch(name, "\\A[a-z0-9](?:[-a-z0-9]{0,61}[a-z0-9])?\\z", RegexOptions.CultureInvariant);
    private string Core(InspectionAllocation session) => "/api/v1/namespaces/" + NamespaceFor(session);
    private string Sandboxes(InspectionAllocation session) => "/apis/agents.x-k8s.io/v1beta1/namespaces/" + NamespaceFor(session) + "/sandboxes";
    public async Task StartAsync(InspectionAllocation session, CancellationToken token)
    {
        if (!await _api.CreateAsync(Sandboxes(session), Manifest(session, false), token)) return;
        if (await _api.GetAsync<K.PersistentVolumeClaim>(Core(session) + "/persistentvolumeclaims/" + Source(session).Volume, token) is null)
            throw new IOException("Saved workspace is unavailable.");
    }

    public async Task<string> ObserveAsync(InspectionAllocation session, CancellationToken token)
    {
        string name = Name(session.Id);
        K.Pod? pod = await _api.GetAsync<K.Pod>(Core(session) + "/pods/" + name, token);
        if (pod is null)
        {
            K.Sandbox? sandbox = await _api.GetAsync<K.Sandbox>(Sandboxes(session) + "/" + name, token);
            return sandbox?.Spec.OperatingMode == K.SandboxSpecOperatingMode.Running ? "Pending" : "Missing";
        }
        string phase = pod.Status?.Phase ?? "Pending";
        if (phase == "Running") return "Running";
        if (phase is "Failed" or "Succeeded") return "Failed";
        if (pod.Status?.InitContainerStatuses is { } statuses)
            foreach (K.ContainerStatus status in statuses)
                if (status.State?.Terminated?.ExitCode is > 0) return "Failed";
        return "Pending";
    }
    public async Task StopAsync(InspectionAllocation session, CancellationToken token)
    {
        string name = Name(session.Id);
        await _api.CreateAsync(Sandboxes(session), Manifest(session, true), token);
        await _api.PatchAsync(Sandboxes(session) + "/" + name, SuspendPatch(), token);
    }
    public K.Sandbox Manifest(InspectionAllocation session, bool suspended)
    {
        string name = Name(session.Id);
        var volumes = new List<K.SandboxSpecPodTemplateSpecVolumesItem>
        {
            new()
            {
                Name = "workspace",
                PersistentVolumeClaim = new() { ClaimName = Source(session).Volume, ReadOnly = true }
            },
            new() { Name = "temporary", EmptyDir = new() { SizeLimit = "128Mi" } }
        };
        return new K.Sandbox
        {
            ApiVersion = "agents.x-k8s.io/v1beta1",
            Kind = "Sandbox",
            Metadata = new() { Name = name, Namespace = NamespaceFor(session) },
            Spec = new()
            {
                OperatingMode = suspended ? K.SandboxSpecOperatingMode.Suspended : K.SandboxSpecOperatingMode.Running,
                ShutdownPolicy = K.SandboxSpecShutdownPolicy.Retain,
                Service = false,
                PodTemplate = new()
                {
                    Metadata = new() { Labels = new() { ["app"] = "goblin-inspection" } },
                    Spec = new()
                    {
                        AutomountServiceAccountToken = false,
                        RestartPolicy = "Never",
                        TerminationGracePeriodSeconds = 2,
                        SecurityContext = new()
                        {
                            RunAsNonRoot = true,
                            RunAsUser = 1000,
                            RunAsGroup = 1000,
                            SeccompProfile = new() { Type = "RuntimeDefault" }
                        },
                        Containers =
                        [
                            new()
                            {
                                Name = "inspect", Image = _options.Image,
                                Command = ["sleep", "infinity"], WorkingDir = "/workspace/repository",
                                SecurityContext = new()
                                {
                                    AllowPrivilegeEscalation = false, ReadOnlyRootFilesystem = true,
                                    Capabilities = new() { Drop = ["ALL"] }
                                },
                                Resources = new()
                                {
                                    Requests = new() { ["cpu"] = "10m", ["memory"] = "32Mi" },
                                    Limits = new() { ["cpu"] = "500m", ["memory"] = "256Mi" }
                                },
                                VolumeMounts =
                                [
                                    new() { Name = "workspace", MountPath = "/workspace", ReadOnly = true },
                                    new() { Name = "temporary", MountPath = "/tmp" }
                                ]
                            }
                        ],
                        Volumes = volumes
                    }
                }
            }
        };
    }

    private static K.SandboxSuspendPatch SuspendPatch() => new()
    {
        Spec = new() { OperatingMode = K.SandboxSuspendPatchSpecOperatingMode.Suspended }
    };
}
