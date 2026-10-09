using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Goblin.Core.Work;
using K = Goblin.Execution.Kubernetes;

namespace Goblin.Execution;

// Owns the desired Kubernetes resource shape and its security policy. The host
// coordinates lifecycle and recovery without constructing pod specifications.
internal sealed class SandboxManifestBuilder
{
    // Additional writable top-level paths are logical directories on the same
    // Work disk. This single list owns both initialization and mounts; paths are
    // application constants, never user input or independent storage quotas.
    private static readonly (string MountPath, string SubPath)[] PersistentDirectories =
    [
        ("/runtime", ".goblin/runtime"),
        ("/tmp", ".goblin/tmp")
    ];

    private readonly SandboxOptions _options;

    internal SandboxManifestBuilder(SandboxOptions options) => _options = options;

    internal string InputName(WorkSnapshot work) => "input-" + work.Id.ToString(CultureInfo.InvariantCulture) + "-" +
        work.Attempts[^1].Id.ToString(CultureInfo.InvariantCulture) + "-" +
        work.Attempts[^1].WorkspaceNumber.ToString(CultureInfo.InvariantCulture);

    internal K.ObjectMeta Metadata(string name, WorkSnapshot work, string? ns = null,
        string? resourceVersion = null, string? phase = null) => new()
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

    internal K.Sandbox Create(WorkSnapshot work, string ns, string name, bool suspended,
        string? resourceVersion = null, string? phase = null)
    {
        K.ObjectMeta metadata = Metadata(name, work, ns, resourceVersion, phase);
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
                        InitContainers =
                        [
                            new()
                            {
                                Name = "prepare-storage", Image = _options.Image,
                                // Keep the existing PVC root and checkout layout. Subpaths must
                                // exist before Kubernetes mounts them into the execution container.
                                Command = ["sh", "-ec", "umask 077; mkdir -p " +
                                    string.Join(" ", PersistentDirectories.Select(directory => "/workspace/" + directory.SubPath))],
                                SecurityContext = new()
                                {
                                    AllowPrivilegeEscalation = false, ReadOnlyRootFilesystem = true,
                                    Capabilities = new() { Drop = ["ALL"] }
                                },
                                Resources = new()
                                {
                                    Requests = new() { ["cpu"] = "10m", ["memory"] = "16Mi" },
                                    Limits = new() { ["cpu"] = "100m", ["memory"] = "64Mi" }
                                },
                                VolumeMounts = [new() { Name = "workspace", MountPath = "/workspace" }]
                            }
                        ],
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
                                    .. PersistentDirectories.Select(directory => new K.SandboxSpecPodTemplateSpecContainersItemVolumeMountsItem
                                    {
                                        Name = "workspace", MountPath = directory.MountPath, SubPath = directory.SubPath
                                    }),
                                    new() { Name = "codex", MountPath = "/run/codex" },
                                    new() { Name = "credentials", MountPath = "/run/credentials", ReadOnly = true },
                                    new() { Name = "input", MountPath = "/run/input", ReadOnly = true }
                                ]
                            }
                        ],
                        Volumes =
                        [
                            new() { Name = "workspace", PersistentVolumeClaim = new() { ClaimName = name } },
                            // Codex keeps writable, refreshable authentication alongside native
                            // sessions. Keep this private directory off the retained Work disk.
                            new() { Name = "codex", EmptyDir = new() },
                            new() { Name = "credentials", Secret = new() { SecretName = InputName(work), DefaultMode = 288 } },
                            new() { Name = "input", ConfigMap = new() { Name = InputName(work) } }
                        ]
                    }
                }
            }
        };
    }
}
