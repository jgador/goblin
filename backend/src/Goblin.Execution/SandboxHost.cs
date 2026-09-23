using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;
using K = Goblin.Execution.Kubernetes;

namespace Goblin.Execution;

public sealed record SandboxOptions(string Namespace, string Image, string CodexHome, string RepositoryUrl)
{
    public string CpuLimit { get; init; } = "2";
    public string MemoryLimit { get; init; } = "2Gi";
}

public sealed class SandboxHost : IExecutionHost
{
    private readonly KubernetesApi _api;
    private readonly SandboxOptions _options;
    private readonly IExecutionHost _textHost;
    private readonly IRepositoryBroker _repositories;
    private readonly IWorkspaceArchive? _archives;
    private readonly WorkspaceLimits _limits;

    public SandboxHost(KubernetesApi api, SandboxOptions options, IExecutionHost textHost, IRepositoryBroker repositories, IWorkspaceArchive? archives = null, WorkspaceLimits? limits = null)
    {
        if (!ValidNamespace(options.Namespace))
            throw new ArgumentException("Invalid execution namespace.", nameof(options));
        _api = api;
        _options = options;
        _textHost = textHost;
        _repositories = repositories;
        _archives = archives;
        _limits = limits ?? new();
    }

    public RuntimeCapabilities[] Capabilities => [new("codex", true, true, true, false, false)];
    // Claims retain the global IDs; the visible suffix comes from the Work's
    // durable execution history, not from the global attempt sequence.
    public string EnvironmentFor(long workId, long attemptId) => "k8s/" + _options.Namespace + "/run-" +
        workId.ToString(CultureInfo.InvariantCulture) + "/" + attemptId.ToString(CultureInfo.InvariantCulture);

    private static bool ValidNamespace(string value) => Regex.IsMatch(value, "\\A[a-z0-9](?:[-a-z0-9]{0,61}[a-z0-9])?\\z", RegexOptions.CultureInvariant);

    private static string RunName(WorkSnapshot work) => "run-" + work.Id.ToString(CultureInfo.InvariantCulture) + "-" +
        work.Attempts.Length.ToString(CultureInfo.InvariantCulture) +
        (work.Attempts[^1].WorkspaceNumber == 1 ? "" : "-s" + work.Attempts[^1].WorkspaceNumber);

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
        if (work.Attempts[^1].Target.Repository is null || work.Attempts[^1].ReasoningOnly) { await _textHost.StartAsync(work, token); return; }
        SandboxAddress address = Address(work);
        string name = address.Name;
        // Reserve the identity before provisioning inputs. A delayed or duplicate
        // starter that meets a cancellation fence cannot recreate credentials.
        if (!await _api.CreateAsync(address.Sandboxes, Manifest(work, suspended: false), token))
        {
            K.Sandbox? existing = await _api.GetAsync<K.Sandbox>(address.Sandboxes + "/" + name, token);
            K.Pod? existingPod = await _api.GetAsync<K.Pod>(address.Core + "/pods/" + name, token);
            if (work.Attempts[^1].TurnNumber > 1 && existing?.Spec.OperatingMode == K.SandboxSpecOperatingMode.Running &&
                existingPod?.Status?.Phase == "Running") return;
            if (work.Attempts[^1].TurnNumber == 1) return;
            throw new IOException("Execution allocation cannot be restarted.");
        }
        await ReclaimAsync(address, token);
        // Credentials are scoped to this execution's mounts, outside Work JSON.
        string codex = await File.ReadAllTextAsync(Path.Combine(_options.CodexHome, "auth.json"), token);
        string capability = await _repositories.PrepareAsync(work, token);
        var secret = new K.Secret
        {
            ApiVersion = "v1",
            Kind = "Secret",
            Metadata = Metadata(name, work),
            Type = "Opaque",
            StringData = new()
            {
                ["auth.json"] = codex,
                ["repository-capability"] = capability,
                ["repository-url"] = _options.RepositoryUrl
            }
        };
        await _api.CreateAsync(address.Core + "/secrets", secret, token);
        var input = new K.ConfigMap
        {
            ApiVersion = "v1",
            Kind = "ConfigMap",
            Metadata = Metadata(name, work),
            Data = new() { ["input.json"] = JsonSerializer.Serialize(new WorkerInput(work, "/run/codex", "codex"), ExecutionFiles.Json) }
        };
        await _api.CreateAsync(address.Core + "/configmaps", input, token);
        var volume = new K.PersistentVolumeClaim
        {
            ApiVersion = "v1",
            Kind = "PersistentVolumeClaim",
            Metadata = Metadata(name, work),
            Spec = new() { AccessModes = ["ReadWriteOnce"], Resources = new() { Requests = new() { ["storage"] = "2Gi" } } }
        };
        await _api.CreateAsync(address.Core + "/persistentvolumeclaims", volume, token);
        K.Sandbox? reserved = await _api.GetAsync<K.Sandbox>(address.Sandboxes + "/" + name, token);
        if (reserved?.Spec.OperatingMode == K.SandboxSpecOperatingMode.Suspended)
            await CleanupAsync(work, token);
    }

    public async Task<ExecutionObservation> ObserveAsync(WorkSnapshot work, bool stop, CancellationToken token) =>
        (await ObserveCoreAsync(work, stop, token)) with { TurnNumber = work.Attempts[^1].TurnNumber };

    private async Task<ExecutionObservation> ObserveCoreAsync(WorkSnapshot work, bool stop, CancellationToken token)
    {
        if (work.Attempts[^1].Target.Repository is null || work.Attempts[^1].ReasoningOnly) return await _textHost.ObserveAsync(work, stop, token);
        SandboxAddress address = Address(work);
        string name = address.Name;
        ExecutionObservation? repositoryObservation = await _repositories.ObserveAsync(work, token);
        if (repositoryObservation?.Kind == ObservationKind.Uncertain) stop = true;
        K.Sandbox? sandbox = await _api.GetAsync<K.Sandbox>(address.Sandboxes + "/" + name, token);
        if (sandbox is null)
        {
            // Reserve the deterministic name in suspended mode. Any in-flight
            // create either wins first and is observed, or loses to this fence.
            await _api.CreateAsync(address.Sandboxes, Manifest(work, suspended: true), token);
            sandbox = await _api.GetAsync<K.Sandbox>(address.Sandboxes + "/" + name, token);
            stop = true;
        }
        K.PodList? pods = await _api.GetAsync<K.PodList>(address.Core + "/pods?labelSelector=goblin-attempt%3D" + work.Attempts[^1].Id.ToString(CultureInfo.InvariantCulture), token);
        foreach (K.Pod pod in pods?.Items ?? [])
        {
            string podName = pod.Metadata?.Name ?? throw new IOException("Execution pod has no name.");
            string phase = pod.Status?.Phase ?? "Pending";
            if (podName != name) continue;
            if (work.Attempts[^1].Status == AttemptStatus.Waiting && !stop)
            {
                if (phase == "Running") return new(ObservationKind.Pending);
                if (phase is "Succeeded" or "Failed") return new(ObservationKind.Stopped);
            }
            if (phase is "Running" or "Succeeded" or "Failed")
            {
                string? logs = await _api.LogsAsync(address.Core + "/pods/" + podName + "/log?container=execution&limitBytes=262144", token);
                string? result = logs?.Split('\n').LastOrDefault(x => x.StartsWith("GOBLIN_RESULT ", StringComparison.Ordinal));
                if (result is not null && repositoryObservation is null)
                {
                    ExecutionObservation observation = JsonSerializer.Deserialize<ExecutionObservation>(result[14..], ExecutionFiles.Json)!;
                    if (observation.TurnNumber == work.Attempts[^1].TurnNumber && !(stop && observation.Kind == ObservationKind.Paused))
                    {
                        if (observation.CheckpointId is not null && (_archives is null || !Guid.TryParse(observation.CheckpointId, out Guid checkpoint) ||
                            !await _archives.VerifiedAsync(checkpoint, work.Attempts[^1].Id, observation.TurnNumber, token)))
                            return new(ObservationKind.Uncertain, Failure: FailureKind.StorageUnavailable) { TurnNumber = work.Attempts[^1].TurnNumber };
                        return observation;
                    }
                }
                if (phase is "Succeeded" or "Failed" && repositoryObservation is null) return new(ObservationKind.Failed, Failure: FailureKind.ExecutionFailed) { TurnNumber = work.Attempts[^1].TurnNumber };
                string? progress = logs?.Split('\n').LastOrDefault(x => x.StartsWith("GOBLIN_PROGRESS ", StringComparison.Ordinal));
                if (!stop && progress is not null)
                {
                    ExecutionObservation update = JsonSerializer.Deserialize<ExecutionObservation>(progress[16..], ExecutionFiles.Json)!;
                    if (update.TurnNumber == work.Attempts[^1].TurnNumber) return update;
                }
            }
        }
        if (stop || sandbox?.Spec.OperatingMode == K.SandboxSpecOperatingMode.Suspended)
        {
            await _api.PatchAsync(address.Sandboxes + "/" + name, SuspendPatch(), token);
            // Suspension is intent. Confirm only after every owned pod is gone.
            pods = await _api.GetAsync<K.PodList>(address.Core + "/pods?labelSelector=goblin-attempt%3D" + work.Attempts[^1].Id.ToString(CultureInfo.InvariantCulture), token);
            if (!(pods?.Items.Any(p => p.Metadata?.Name == name) ?? false))
            {
                try { await _repositories.StopAsync(work, token); }
                catch { return new(ObservationKind.Uncertain, Failure: FailureKind.ExecutionFailed); }
                return new(ObservationKind.Stopped) { TurnNumber = work.Attempts[^1].TurnNumber };
            }
            return repositoryObservation ?? new(ObservationKind.Pending);
        }
        return new(ObservationKind.Pending);
    }

    public async Task CleanupAsync(WorkSnapshot work, CancellationToken token)
    {
        if (work.Attempts[^1].Target.Repository is null || work.Attempts[^1].ReasoningOnly) { await _textHost.CleanupAsync(work, token); return; }
        SandboxAddress address = Address(work);
        string name = address.Name;
        await _repositories.StopAsync(work, token);
        await _api.PatchAsync(address.Sandboxes + "/" + name, SuspendPatch(), token);
        await _api.DeleteAsync(address.Core + "/secrets/" + name, token);
        await _api.DeleteAsync(address.Core + "/configmaps/" + name, token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (true)
        {
            K.PodList? pods = await _api.GetAsync<K.PodList>(address.Core + "/pods?labelSelector=goblin-attempt%3D" + work.Attempts[^1].Id.ToString(CultureInfo.InvariantCulture), timeout.Token);
            if (!(pods?.Items.Any(p => p.Metadata?.Name == name) ?? false)) break;
            await Task.Delay(200, timeout.Token);
        }
        // Keep the suspended identity fence and workspace PVC for recovery.
        // No database certificate, web data volume, or service token was mounted.
        await _repositories.ReleaseAsync(work, token);
    }

    private async Task ReclaimAsync(SandboxAddress address, CancellationToken token)
    {
        if (_archives is null) return;
        K.PersistentVolumeClaimList? volumes = await _api.GetAsync<K.PersistentVolumeClaimList>(address.Core + "/persistentvolumeclaims?labelSelector=app%3Dgoblin-execution", token);
        K.PersistentVolumeClaim[] candidates = volumes?.Items.ToArray() ?? [];
        int retained = candidates.Length;
        if (retained < _limits.MaxCachedVolumes) return;
        K.PodList? pods = await _api.GetAsync<K.PodList>(address.Core + "/pods", token);
        foreach (K.PersistentVolumeClaim volume in candidates)
        {
            if (retained < _limits.MaxCachedVolumes) break;
            string name = volume.Metadata?.Name ?? throw new IOException("Workspace volume has no name.");
            if (pods?.Items.Any(p => p.Spec?.Volumes?.Any(v => v.PersistentVolumeClaim?.ClaimName == name) == true) == true) continue;
            if (!long.TryParse(volume.Metadata?.Labels?.GetValueOrDefault("goblin-attempt"), out long attemptId)) continue;
            int number = int.TryParse(volume.Metadata?.Labels?.GetValueOrDefault("goblin-workspace"), out int parsed) ? parsed : 1;
            if (!await _archives.CanDiscardAsync(attemptId, number, token)) continue;
            await _api.DeleteAsync(address.Core + "/persistentvolumeclaims/" + name, token);
            retained--;
        }
        if (retained >= _limits.MaxCachedVolumes) throw new IOException("Workspace storage needs attention before another allocation.");
    }

    public K.Sandbox Manifest(WorkSnapshot work, bool suspended)
    {
        SandboxAddress address = Address(work);
        string name = address.Name;
        K.ObjectMeta metadata = Metadata(name, work, address.Namespace);
        return new K.Sandbox
        {
            ApiVersion = "agents.x-k8s.io/v1beta1",
            Kind = "Sandbox",
            Metadata = metadata,
            Spec = new()
            {
                OperatingMode = suspended ? K.SandboxSpecOperatingMode.Suspended : K.SandboxSpecOperatingMode.Running,
                ShutdownPolicy = K.SandboxSpecShutdownPolicy.Retain,
                Service = false,
                PodTemplate = new()
                {
                    Metadata = new() { Labels = new Dictionary<string, string>(metadata.Labels!) },
                    Spec = new()
                    {
                        AutomountServiceAccountToken = false,
                        RestartPolicy = "Never",
                        ActiveDeadlineSeconds = 3600,
                        TerminationGracePeriodSeconds = 15,
                        SecurityContext = new()
                        {
                            RunAsNonRoot = true,
                            RunAsUser = 1000,
                            RunAsGroup = 1000,
                            FsGroup = 1000,
                            SeccompProfile = new() { Type = "RuntimeDefault" }
                        },
                        Containers =
                        [
                            new()
                            {
                                Name = "execution", Image = _options.Image,
                                Command = ["dotnet", "Goblin.Web.dll", "--sandbox-execute"],
                                SecurityContext = new()
                                {
                                    AllowPrivilegeEscalation = false, ReadOnlyRootFilesystem = true,
                                    Capabilities = new() { Drop = ["ALL"] }
                                },
                                Resources = new()
                                {
                                    Requests = new() { ["cpu"] = "100m", ["memory"] = "256Mi" },
                                    Limits = new() { ["cpu"] = _options.CpuLimit, ["memory"] = _options.MemoryLimit }
                                },
                                VolumeMounts =
                                [
                                    new() { Name = "workspace", MountPath = "/workspace" },
                                    new() { Name = "temporary", MountPath = "/tmp" },
                                    new() { Name = "runtime", MountPath = "/runtime" },
                                    new() { Name = "credentials", MountPath = "/run/credentials", ReadOnly = true },
                                    new() { Name = "input", MountPath = "/run/input", ReadOnly = true }
                                ]
                            }
                        ],
                        Volumes =
                        [
                            new() { Name = "workspace", PersistentVolumeClaim = new() { ClaimName = name } },
                            new() { Name = "temporary", EmptyDir = new() { SizeLimit = "256Mi" } },
                            new() { Name = "credentials", Secret = new() { SecretName = name, DefaultMode = 288 } },
                            new() { Name = "input", ConfigMap = new() { Name = name } },
                            new() { Name = "runtime", EmptyDir = new() { SizeLimit = "256Mi" } }
                        ]
                    }
                }
            }
        };
    }

    private static K.SandboxSuspendPatch SuspendPatch() => new()
    {
        Spec = new() { OperatingMode = K.SandboxSuspendPatchSpecOperatingMode.Suspended }
    };

    private static K.ObjectMeta Metadata(string name, WorkSnapshot work, string? ns = null) => new()
    {
        Name = name,
        Namespace = ns,
        Labels = new Dictionary<string, string>
        {
            ["goblin-attempt"] = work.Attempts[^1].Id.ToString(CultureInfo.InvariantCulture),
            ["goblin-work"] = work.Id.ToString(CultureInfo.InvariantCulture),
            ["app"] = "goblin-execution",
            ["goblin-workspace"] = work.Attempts[^1].WorkspaceNumber.ToString(CultureInfo.InvariantCulture)
        }
    };
}
