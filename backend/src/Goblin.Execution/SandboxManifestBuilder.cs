using System.Collections.Generic;
using System.Globalization;
using Goblin.Core.Work;
using K = Goblin.Execution.Kubernetes;

namespace Goblin.Execution;

// Owns the desired Kubernetes resource shape and its security policy. The host
// coordinates lifecycle and recovery without constructing pod specifications.
internal sealed class SandboxManifestBuilder
{
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
}
