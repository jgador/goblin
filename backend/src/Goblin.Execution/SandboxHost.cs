using System;
using System.Collections.Concurrent;
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
using Goblin.Execution.Kubernetes;
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
    private readonly IWorkspaceCheckpoints? _checkpoints;
    private readonly WorkspaceLimits _limits;
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _workLocks = new();

    public SandboxHost(KubernetesApi api, SandboxOptions options, IExecutionHost textHost, IRepositoryBroker repositories, IWorkspaceCheckpoints? checkpoints = null, WorkspaceLimits? limits = null)
    {
        if (!ValidNamespace(options.Namespace))
            throw new ArgumentException("Invalid execution namespace.", nameof(options));
        _api = api;
        _options = options;
        _textHost = textHost;
        _repositories = repositories;
        _checkpoints = checkpoints;
        _limits = limits ?? new();
    }

    public RuntimeCapabilities[] Capabilities => [new("codex", true, true, true, false, false)];
    public string EnvironmentFor(long workId, long attemptId) => "k8s/" + _options.Namespace + "/work-" +
        workId.ToString(CultureInfo.InvariantCulture);
    public string EnvironmentFor(WorkSnapshot work) => work.Attempts[^1] is { Target.Repository: null } or { ReasoningOnly: true }
        ? _textHost.EnvironmentFor(work) : EnvironmentFor(work.Id, work.Attempts[^1].Id);

    private static bool ValidNamespace(string value) => Regex.IsMatch(value, "\\A[a-z0-9](?:[-a-z0-9]{0,61}[a-z0-9])?\\z", RegexOptions.CultureInvariant);

    private SandboxAddress Address(WorkSnapshot work)
    {
        string reference = work.Workspace?.EnvironmentReference ?? throw new IOException("Work has no workspace.");
        string[] parts = reference.Split('/');
        if (parts.Length == 3 && parts[0] == "k8s" && ValidNamespace(parts[1]) &&
            parts[2] == "work-" + work.Id.ToString(CultureInfo.InvariantCulture))
            return new(parts[1], parts[2]);
        throw new IOException("Unrecognized execution environment reference.");
    }

    private static string InputName(WorkSnapshot work) => "input-" + work.Id.ToString(CultureInfo.InvariantCulture) + "-" +
        work.Attempts[^1].Id.ToString(CultureInfo.InvariantCulture) + "-" + work.Attempts[^1].WorkspaceNumber.ToString(CultureInfo.InvariantCulture);

    private static bool Owns(K.Sandbox sandbox, WorkSnapshot work) =>
        sandbox.Metadata?.Labels?.GetValueOrDefault("goblin-work") == work.Id.ToString(CultureInfo.InvariantCulture) &&
        sandbox.Metadata.Labels.GetValueOrDefault("goblin-attempt") == work.Attempts[^1].Id.ToString(CultureInfo.InvariantCulture) &&
        sandbox.Metadata.Labels.GetValueOrDefault("goblin-workspace") == work.Attempts[^1].WorkspaceNumber.ToString(CultureInfo.InvariantCulture);

    private static bool CanTakeOver(K.Sandbox sandbox, WorkSnapshot work)
    {
        if (sandbox.Metadata?.Labels?.GetValueOrDefault("goblin-work") != work.Id.ToString(CultureInfo.InvariantCulture)) return false;
        if (!long.TryParse(sandbox.Metadata.Labels.GetValueOrDefault("goblin-attempt"), out long id)) return false;
        if (!int.TryParse(sandbox.Metadata.Labels.GetValueOrDefault("goblin-workspace"), out int allocation)) return false;
        AttemptSnapshot? previous = work.Attempts.SingleOrDefault(a => a.Id == id);
        if (previous is null || previous.CleanupPending) return false;
        return previous.Id != work.Attempts[^1].Id
            ? previous.Status is AttemptStatus.Failed or AttemptStatus.Succeeded or AttemptStatus.Cancelled
            : allocation < previous.WorkspaceNumber;
    }

    private static string Version(K.Sandbox sandbox) => sandbox.Metadata?.ResourceVersion ?? throw new IOException("Workspace version is missing.");

    private async Task<T> ExclusiveAsync<T>(long workId, Func<Task<T>> action, CancellationToken token)
    {
        SemaphoreSlim gate = _workLocks.GetOrAdd(workId, _ => new(1, 1));
        await gate.WaitAsync(token);
        try { return await action(); }
        finally { gate.Release(); }
    }

    private sealed record SandboxAddress(string Namespace, string Name)
    {
        public string Core => "/api/v1/namespaces/" + Namespace;
        public string Sandboxes => "/apis/agents.x-k8s.io/v1beta1/namespaces/" + Namespace + "/sandboxes";
    }

    public async Task StartAsync(WorkSnapshot work, CancellationToken token) =>
        await ExclusiveAsync(work.Id, async () => { await StartCoreAsync(work, token); return true; }, token);

    private async Task StartCoreAsync(WorkSnapshot work, CancellationToken token)
    {
        AttemptSnapshot attempt = work.Attempts[^1];
        if (attempt.Target.Repository is null || attempt.ReasoningOnly) { await _textHost.StartAsync(work, token); return; }
        if (attempt.Status != AttemptStatus.Starting || attempt.OwnerId is null) return;
        SandboxAddress address = Address(work);
        string path = address.Sandboxes + "/" + address.Name;
        K.Sandbox? existing = await _api.GetAsync<K.Sandbox>(path, token);
        bool requiresVolume = existing?.Metadata?.Labels?.GetValueOrDefault("goblin-volume") == "created";
        if (existing is null)
        {
            // Reserve while suspended. Inputs cannot launch a pod before activation.
            if (!await _api.CreateAsync(address.Sandboxes, Manifest(work, true, phase: "reserved"), token)) return;
        }
        else
        {
            // The same allocation can carry another authorized turn in its live pod.
            // A duplicate start never resumes a stopped or partially prepared allocation.
            if (Owns(existing, work)) return;
            if (!CanTakeOver(existing, work)) return;
            if (existing.Spec.OperatingMode != K.SandboxSpecOperatingMode.Suspended ||
                existing.Metadata?.Labels?.GetValueOrDefault("goblin-phase") != "stopped" ||
                await _api.GetAsync<K.Pod>(address.Core + "/pods/" + address.Name, token) is not null)
                throw new IOException("Workspace suspension is not confirmed.");
            if (!await _api.TryPatchAsync(path, Manifest(work, true, Version(existing), "reserved"), token)) return;
        }
        K.Sandbox reserved = await _api.GetAsync<K.Sandbox>(path, token) ?? throw new IOException("Workspace reservation is missing.");
        if (!Owns(reserved, work) || reserved.Metadata?.Labels?.GetValueOrDefault("goblin-phase") != "reserved") return;
        string name = InputName(work);
        bool activated = false;
        try
        {
            K.PersistentVolumeClaim? volume = await _api.GetAsync<K.PersistentVolumeClaim>(address.Core + "/persistentvolumeclaims/" + address.Name, token);
            if (volume is null)
            {
                // Missing retained storage needs explicit recovery; never substitute an
                // older checkpoint for edits that were supposed to survive suspension.
                if (requiresVolume || (existing is null && (attempt.WorkspaceNumber > 1 || attempt.TurnNumber > 1 ||
                    work.Attempts.SkipLast(1).Any(a => a.Target.Repository is not null && a.EnvironmentReference is not null))))
                    throw new IOException("Retained workspace storage is missing.");
                await RequireVolumeCapacityAsync(address, token);
                if (!await _api.CreateAsync(address.Core + "/persistentvolumeclaims", new K.PersistentVolumeClaim
                {
                    ApiVersion = "v1",
                    Kind = "PersistentVolumeClaim",
                    Metadata = Metadata(address.Name, work),
                    Spec = new() { AccessModes = ["ReadWriteOnce"], Resources = new() { Requests = new() { ["storage"] = "2Gi" } } }
                }, token)) throw new IOException("Workspace volume already exists.");
            }
            else
            {
                if (volume.Metadata?.Labels?.GetValueOrDefault("goblin-work") != work.Id.ToString(CultureInfo.InvariantCulture))
                    throw new IOException("Workspace volume ownership does not match.");
                await _api.PatchAsync(address.Core + "/persistentvolumeclaims/" + address.Name,
                    new { metadata = Metadata(address.Name, work) }, token);
            }
            reserved = await ReservationAsync(path, work, token);
            if (!await _api.TryPatchAsync(path, new
            {
                metadata = new { resourceVersion = Version(reserved), labels = new Dictionary<string, string> { ["goblin-volume"] = "created" } }
            }, token)) throw new IOException("Workspace reservation changed.");
            reserved = await _api.GetAsync<K.Sandbox>(path, token) ?? throw new IOException("Workspace reservation is missing.");
            if (!Owns(reserved, work) || reserved.Metadata?.Labels?.GetValueOrDefault("goblin-phase") != "reserved")
                throw new IOException("Workspace reservation changed.");
            string codex = await File.ReadAllTextAsync(Path.Combine(_options.CodexHome, "auth.json"), token);
            string capability = await _repositories.PrepareAsync(work, token);
            if (!await _api.CreateAsync(address.Core + "/secrets", new K.Secret
            {
                ApiVersion = "v1",
                Kind = "Secret",
                Metadata = Metadata(name, work),
                Type = "Opaque",
                StringData = new() { ["auth.json"] = codex, ["repository-capability"] = capability, ["repository-url"] = _options.RepositoryUrl }
            }, token)) throw new IOException("Execution inputs already exist.");
            if (!await _api.CreateAsync(address.Core + "/configmaps", new K.ConfigMap
            {
                ApiVersion = "v1",
                Kind = "ConfigMap",
                Metadata = Metadata(name, work),
                Data = new() { ["input.json"] = JsonSerializer.Serialize(new WorkerInput(work, "/run/codex", "codex") { SandboxImage = _options.Image }, ExecutionFiles.Json) }
            }, token)) throw new IOException("Execution inputs already exist.");
            reserved = await ReservationAsync(path, work, token);
            activated = await _api.TryPatchAsync(path, Manifest(work, false, Version(reserved), "running"), token);
            if (!activated) throw new IOException("Workspace reservation changed.");
        }
        finally
        {
            if (!activated)
            {
                await _api.DeleteAsync(address.Core + "/secrets/" + name, token);
                await _api.DeleteAsync(address.Core + "/configmaps/" + name, token);
            }
        }
    }

    private async Task<K.Sandbox> ReservationAsync(string path, WorkSnapshot work, CancellationToken token)
    {
        K.Sandbox sandbox = await _api.GetAsync<K.Sandbox>(path, token) ?? throw new IOException("Workspace reservation is missing.");
        if (!Owns(sandbox, work) || sandbox.Metadata?.Labels?.GetValueOrDefault("goblin-phase") != "reserved")
            throw new IOException("Workspace reservation changed.");
        return sandbox;
    }

    public Task<ExecutionObservation> ObserveAsync(WorkSnapshot work, bool stop, CancellationToken token) =>
        ExclusiveAsync(work.Id, async () => (await ObserveCoreAsync(work, stop, token)) with { TurnNumber = work.Attempts[^1].TurnNumber }, token);

    private async Task<ExecutionObservation> ObserveCoreAsync(WorkSnapshot work, bool stop, CancellationToken token)
    {
        if (work.Attempts[^1].Target.Repository is null || work.Attempts[^1].ReasoningOnly) return await _textHost.ObserveAsync(work, stop, token);
        if (work.Attempts[^1].Status == AttemptStatus.Uncertain) stop = true;
        SandboxAddress address = Address(work);
        string name = address.Name;
        ExecutionObservation? repositoryObservation = await _repositories.ObserveAsync(work, token);
        if (repositoryObservation?.Kind == ObservationKind.Uncertain) stop = true;
        K.Sandbox? sandbox = await _api.GetAsync<K.Sandbox>(address.Sandboxes + "/" + name, token);
        if (sandbox is null)
        {
            // Reserve the deterministic name in suspended mode. Any in-flight
            // create either wins first and is observed, or loses to this fence.
            await _api.CreateAsync(address.Sandboxes, Manifest(work, suspended: true, phase: "stopping"), token);
            sandbox = await _api.GetAsync<K.Sandbox>(address.Sandboxes + "/" + name, token);
            stop = true;
        }
        if (sandbox is null || !Owns(sandbox, work)) return new(ObservationKind.Uncertain, Failure: FailureKind.HostUnavailable);
        // A delayed stop observation must not reopen cleanup on an allocation
        // already released for the next attempt.
        if (sandbox.Metadata?.Labels?.GetValueOrDefault("goblin-phase") == "stopped") return new(ObservationKind.Stopped);
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
                    if (observation.TurnNumber == work.Attempts[^1].TurnNumber &&
                        !(stop && observation.Kind is ObservationKind.Paused or ObservationKind.Uncertain))
                    {
                        if (observation.CheckpointId is long checkpoint && (_checkpoints is null || checkpoint <= 0 ||
                            !await _checkpoints.VerifiedAsync(checkpoint, work.Attempts[^1].Id, observation.TurnNumber, token)))
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
            if (!await SuspendAsync(address, sandbox, token)) return new(ObservationKind.Pending);
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

    public async Task CleanupAsync(WorkSnapshot work, CancellationToken token) =>
        await ExclusiveAsync(work.Id, async () => { await CleanupCoreAsync(work, token); return true; }, token);

    private async Task CleanupCoreAsync(WorkSnapshot work, CancellationToken token)
    {
        if (work.Attempts[^1].Target.Repository is null || work.Attempts[^1].ReasoningOnly) { await _textHost.CleanupAsync(work, token); return; }
        SandboxAddress address = Address(work);
        string path = address.Sandboxes + "/" + address.Name;
        K.Sandbox? sandbox = await _api.GetAsync<K.Sandbox>(path, token);
        if (sandbox is null)
        {
            await _api.CreateAsync(address.Sandboxes, Manifest(work, true, phase: "stopping"), token);
            sandbox = await _api.GetAsync<K.Sandbox>(path, token);
        }
        if (sandbox is null) throw new IOException("Workspace is unavailable.");
        if (!Owns(sandbox, work)) return; // A later allocation owns this Work's sandbox.
        if (sandbox.Metadata?.Labels?.GetValueOrDefault("goblin-phase") == "stopped") return;
        if (!await SuspendAsync(address, sandbox, token)) throw new IOException("Workspace ownership changed.");
        await _repositories.StopAsync(work, token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (await _api.GetAsync<K.Pod>(address.Core + "/pods/" + address.Name, timeout.Token) is not null)
            await Task.Delay(200, timeout.Token);
        // Mount names belong to the allocation, so delayed cleanup cannot delete
        // a replacement's credentials.
        string input = InputName(work);
        await _api.DeleteAsync(address.Core + "/secrets/" + input, token);
        await _api.DeleteAsync(address.Core + "/configmaps/" + input, token);
        await _repositories.ReleaseAsync(work, token);
        K.Sandbox stopped = await _api.GetAsync<K.Sandbox>(path, token) ?? throw new IOException("Workspace is unavailable.");
        if (!Owns(stopped, work) || !await _api.TryPatchAsync(path, new
        {
            metadata = new { resourceVersion = Version(stopped), labels = new Dictionary<string, string> { ["goblin-phase"] = "stopped" } },
            spec = new { operatingMode = "Suspended" }
        }, token)) throw new IOException("Workspace ownership changed.");
    }

    private Task<bool> SuspendAsync(SandboxAddress address, K.Sandbox sandbox, CancellationToken token) =>
        _api.TryPatchAsync(address.Sandboxes + "/" + address.Name, new
        {
            metadata = new { resourceVersion = Version(sandbox), labels = new Dictionary<string, string> { ["goblin-phase"] = "stopping" } },
            spec = new { operatingMode = "Suspended" }
        }, token);

    private async Task RequireVolumeCapacityAsync(SandboxAddress address, CancellationToken token)
    {
        K.PersistentVolumeClaimList? volumes = await _api.GetAsync<K.PersistentVolumeClaimList>(address.Core + "/persistentvolumeclaims?labelSelector=app%3Dgoblin-execution", token);
        // Git checkpoints do not preserve ignored/untracked files. No automatic
        // volume deletion is safe without a separately approved retention policy.
        if ((volumes?.Items.Count ?? 0) >= _limits.MaxCachedVolumes)
            throw new IOException("Workspace storage needs attention before another allocation.");
    }

    public K.Sandbox Manifest(WorkSnapshot work, bool suspended, string? resourceVersion = null, string? phase = null)
    {
        SandboxAddress address = Address(work);
        string name = address.Name;
        K.ObjectMeta metadata = Metadata(name, work, address.Namespace, resourceVersion, phase);
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
                            new() { Name = "credentials", Secret = new() { SecretName = InputName(work), DefaultMode = 288 } },
                            new() { Name = "input", ConfigMap = new() { Name = InputName(work) } },
                            new() { Name = "runtime", EmptyDir = new() { SizeLimit = "256Mi" } }
                        ]
                    }
                }
            }
        };
    }

    private static K.ObjectMeta Metadata(string name, WorkSnapshot work, string? ns = null, string? resourceVersion = null, string? phase = null) => new()
    {
        Name = name,
        Namespace = ns,
        ResourceVersion = resourceVersion,
        Labels = new Dictionary<string, string>
        {
            ["goblin-attempt"] = work.Attempts[^1].Id.ToString(CultureInfo.InvariantCulture),
            ["goblin-work"] = work.Id.ToString(CultureInfo.InvariantCulture),
            ["app"] = "goblin-execution",
            ["goblin-workspace"] = work.Attempts[^1].WorkspaceNumber.ToString(CultureInfo.InvariantCulture),
            ["goblin-phase"] = phase ?? "running"
        }
    };
}
