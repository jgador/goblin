using System;
using Goblin.Contracts.Runtime;
using Microsoft.Extensions.Configuration;
using Env = Goblin.Contracts.Configuration.EnvironmentVariables;

namespace Goblin.Web;

// Capture host configuration once so service construction does not become a
// second, distributed configuration system with duplicated defaults.
internal sealed class WebRuntimeConfiguration
{
    private WebRuntimeConfiguration(IConfiguration configuration, bool workEnabled)
    {
        DatabaseConnection = configuration.GetConnectionString("Goblin");
        ExecutionNamespace = configuration[Env.GoblinExecutionNamespace];
        NodeName = configuration[Env.GoblinNodeName];
        KubernetesUrl = configuration[Env.GoblinKubernetesUrl];
        KubernetesTokenFile = configuration[Env.GoblinKubernetesTokenFile] ??
            "/var/run/secrets/kubernetes.io/serviceaccount/token";
        KubernetesCaFile = configuration[Env.GoblinKubernetesCaFile] ??
            "/var/run/secrets/kubernetes.io/serviceaccount/ca.crt";
        KubernetesNamespace = configuration[Env.GoblinNamespace] ?? "goblin";
        MonitoringExecutionNamespace = ExecutionNamespace ?? "agents";
        ExecutionImage = configuration[Env.GoblinExecutionImage] ?? "goblin-auth:0.1.0";
        GitRepositoryUrl = configuration[Env.GoblinGitRepositoryUrl] ??
            "http://goblin-repository.goblin.svc:8788";
        SandboxCpuLimit = configuration[Env.GoblinSandboxCpuLimit] ?? "2";
        SandboxMemoryLimit = configuration[Env.GoblinSandboxMemoryLimit] ?? "2Gi";
        // Preserve startup validation order: missing durable storage is reported
        // before capacity values that cannot be used without it.
        WorkspaceLimits = workEnabled && !string.IsNullOrWhiteSpace(DatabaseConnection)
            ? ReadWorkspaceLimits(configuration)
            : new();
    }

    public string? DatabaseConnection { get; }
    public string? ExecutionNamespace { get; }
    public string? NodeName { get; }
    public string? KubernetesUrl { get; }
    public string KubernetesTokenFile { get; }
    public string KubernetesCaFile { get; }
    public string KubernetesNamespace { get; }
    public string MonitoringExecutionNamespace { get; }
    public string ExecutionImage { get; }
    public string GitRepositoryUrl { get; }
    public string SandboxCpuLimit { get; }
    public string SandboxMemoryLimit { get; }
    public WorkspaceLimits WorkspaceLimits { get; }

    public static WebRuntimeConfiguration Read(IConfiguration configuration, bool workEnabled) =>
        new(configuration, workEnabled);

    private static WorkspaceLimits ReadWorkspaceLimits(IConfiguration configuration)
    {
        var limits = new WorkspaceLimits(
            configuration.GetValue(Env.GoblinMaxSandboxes, 2),
            configuration.GetValue(Env.GoblinMaxCachedWorkspaces, 4));
        if (limits.MaxSandboxes < 1 || limits.MaxCachedVolumes < 1)
            throw new InvalidOperationException("Invalid workspace capacity configuration.");
        return limits;
    }
}
