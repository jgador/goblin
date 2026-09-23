using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
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
    public static string Name(Guid id) => "inspect-" + id.ToString("N");
    private string Core => "/api/v1/namespaces/" + _options.Namespace;
    private string Sandboxes => "/apis/agents.x-k8s.io/v1beta1/namespaces/" + _options.Namespace + "/sandboxes";
    public async Task StartAsync(InspectionAllocation session, string capability, CancellationToken token)
    {
        string name = Name(session.Id);
        if (!await _api.CreateAsync(Sandboxes, Manifest(session, false), token)) return;
        if (session.CheckpointId is null)
        {
            if (await _api.GetAsync<K.PersistentVolumeClaim>(Core + "/persistentvolumeclaims/" + session.SourceVolume, token) is null)
                throw new IOException("Saved workspace is unavailable.");
            return;
        }
        await _api.CreateAsync(Core + "/secrets", new K.Secret
        {
            ApiVersion = "v1",
            Kind = "Secret",
            Metadata = new() { Name = name },
            StringData = new() { ["capability"] = capability, ["url"] = _options.RepositoryUrl + "/internal/workspaces/" + session.Id }
        }, token);
        // A concurrent Stop creates/patches the same identity, so it fences delayed starts.
        K.Sandbox? saved = await _api.GetAsync<K.Sandbox>(Sandboxes + "/" + name, token);
        if (saved?.Spec.OperatingMode == K.SandboxSpecOperatingMode.Suspended) await _api.DeleteAsync(Core + "/secrets/" + name, token);
    }
    public async Task<string> ObserveAsync(InspectionAllocation session, CancellationToken token)
    {
        string name = Name(session.Id);
        K.Pod? pod = await _api.GetAsync<K.Pod>(Core + "/pods/" + name, token);
        if (pod is null)
        {
            K.Sandbox? sandbox = await _api.GetAsync<K.Sandbox>(Sandboxes + "/" + name, token);
            return sandbox?.Spec.OperatingMode == K.SandboxSpecOperatingMode.Running ? "Pending" : "Missing";
        }
        string phase = pod.Status?.Phase ?? "Pending";
        if (phase == "Running")
        {
            await _api.DeleteAsync(Core + "/secrets/" + name, token);
            return "Running";
        }
        if (phase is "Failed" or "Succeeded") return "Failed";
        if (pod.Status?.InitContainerStatuses is { } statuses)
            foreach (K.ContainerStatus status in statuses)
                if (status.State?.Terminated?.ExitCode is > 0) return "Failed";
        return "Pending";
    }
    public async Task StopAsync(InspectionAllocation session, CancellationToken token)
    {
        string name = Name(session.Id);
        await _api.CreateAsync(Sandboxes, Manifest(session, true), token);
        await _api.PatchAsync(Sandboxes + "/" + name, SuspendPatch(), token);
        await _api.DeleteAsync(Core + "/secrets/" + name, token);
    }
    public K.Sandbox Manifest(InspectionAllocation session, bool suspended)
    {
        string name = Name(session.Id);
        bool restore = session.CheckpointId is not null;
        var volumes = new List<K.SandboxSpecPodTemplateSpecVolumesItem>
        {
            new()
            {
                Name = "workspace",
                PersistentVolumeClaim = restore ? null : new() { ClaimName = session.SourceVolume, ReadOnly = true },
                EmptyDir = restore ? new() { SizeLimit = "4Gi" } : null
            },
            new() { Name = "temporary", EmptyDir = new() { SizeLimit = "128Mi" } }
        };
        if (restore) volumes.Add(new() { Name = "restore", Secret = new() { SecretName = name } });

        List<K.SandboxSpecPodTemplateSpecInitContainersItem>? initContainers = restore ?
        [
            new()
            {
                Name = "restore", Image = _options.Image,
                Command = ["dotnet", "Goblin.Web.dll", "--inspection-restore"],
                SecurityContext = new()
                {
                    AllowPrivilegeEscalation = false, ReadOnlyRootFilesystem = true,
                    Capabilities = new() { Drop = ["ALL"] }
                },
                Resources = new()
                {
                    Requests = new() { ["cpu"] = "10m", ["memory"] = "32Mi" },
                    Limits = new() { ["cpu"] = "500m", ["memory"] = "512Mi" }
                },
                VolumeMounts =
                [
                    new() { Name = "workspace", MountPath = "/workspace", ReadOnly = false },
                    new() { Name = "temporary", MountPath = "/tmp" },
                    new() { Name = "restore", MountPath = "/run/restore", ReadOnly = true }
                ]
            }
        ] : null;

        return new K.Sandbox
        {
            ApiVersion = "agents.x-k8s.io/v1beta1",
            Kind = "Sandbox",
            Metadata = new() { Name = name, Namespace = _options.Namespace },
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
                            FsGroup = restore ? 1000 : null,
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
                        InitContainers = initContainers,
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
