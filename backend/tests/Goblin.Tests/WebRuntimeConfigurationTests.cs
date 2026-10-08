using System;
using System.Collections.Generic;
using Goblin.Contracts.Configuration;
using Goblin.Web;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Goblin.Tests;

public sealed class WebRuntimeConfigurationTests
{
    [Fact]
    public void DefaultsAreOwnedByOneConfigurationSnapshot()
    {
        WebRuntimeConfiguration configuration = WebRuntimeConfiguration.Read(new ConfigurationBuilder().Build(), true);

        Assert.Null(configuration.DatabaseConnection);
        Assert.Null(configuration.ExecutionNamespace);
        Assert.Null(configuration.NodeName);
        Assert.Null(configuration.KubernetesUrl);
        Assert.Equal("/var/run/secrets/kubernetes.io/serviceaccount/token", configuration.KubernetesTokenFile);
        Assert.Equal("/var/run/secrets/kubernetes.io/serviceaccount/ca.crt", configuration.KubernetesCaFile);
        Assert.Equal("goblin", configuration.KubernetesNamespace);
        Assert.Equal("agents", configuration.MonitoringExecutionNamespace);
        Assert.Equal("goblin-auth:0.1.0", configuration.ExecutionImage);
        Assert.Equal("http://goblin-repository.goblin.svc:8788", configuration.GitRepositoryUrl);
        Assert.Equal("2", configuration.SandboxCpuLimit);
        Assert.Equal("2Gi", configuration.SandboxMemoryLimit);
        Assert.Equal(2, configuration.WorkspaceLimits.MaxSandboxes);
        Assert.Equal(4, configuration.WorkspaceLimits.MaxCachedVolumes);
    }

    [Fact]
    public void OverridesAreCapturedWithoutChangingTheirMeaning()
    {
        IConfiguration source = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Goblin"] = "database",
            [EnvironmentVariables.GoblinExecutionNamespace] = "execution",
            [EnvironmentVariables.GoblinNodeName] = "node",
            [EnvironmentVariables.GoblinKubernetesUrl] = "https://kubernetes",
            [EnvironmentVariables.GoblinKubernetesTokenFile] = "/token",
            [EnvironmentVariables.GoblinKubernetesCaFile] = "/ca",
            [EnvironmentVariables.GoblinNamespace] = "application",
            [EnvironmentVariables.GoblinExecutionImage] = "image",
            [EnvironmentVariables.GoblinGitRepositoryUrl] = "http://repository",
            [EnvironmentVariables.GoblinSandboxCpuLimit] = "3",
            [EnvironmentVariables.GoblinSandboxMemoryLimit] = "5Gi",
            [EnvironmentVariables.GoblinMaxSandboxes] = "6",
            [EnvironmentVariables.GoblinMaxCachedWorkspaces] = "7"
        }).Build();

        WebRuntimeConfiguration configuration = WebRuntimeConfiguration.Read(source, true);

        Assert.Equal("database", configuration.DatabaseConnection);
        Assert.Equal("execution", configuration.ExecutionNamespace);
        Assert.Equal("execution", configuration.MonitoringExecutionNamespace);
        Assert.Equal("node", configuration.NodeName);
        Assert.Equal("https://kubernetes", configuration.KubernetesUrl);
        Assert.Equal("/token", configuration.KubernetesTokenFile);
        Assert.Equal("/ca", configuration.KubernetesCaFile);
        Assert.Equal("application", configuration.KubernetesNamespace);
        Assert.Equal("image", configuration.ExecutionImage);
        Assert.Equal("http://repository", configuration.GitRepositoryUrl);
        Assert.Equal("3", configuration.SandboxCpuLimit);
        Assert.Equal("5Gi", configuration.SandboxMemoryLimit);
        Assert.Equal(6, configuration.WorkspaceLimits.MaxSandboxes);
        Assert.Equal(7, configuration.WorkspaceLimits.MaxCachedVolumes);
    }

    [Fact]
    public void DisabledWorkDoesNotValidateUnusedCapacitySettings()
    {
        IConfiguration source = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Goblin"] = "database",
            [EnvironmentVariables.GoblinMaxSandboxes] = "invalid",
            [EnvironmentVariables.GoblinMaxCachedWorkspaces] = "0"
        }).Build();

        WebRuntimeConfiguration configuration = WebRuntimeConfiguration.Read(source, false);

        Assert.Equal(2, configuration.WorkspaceLimits.MaxSandboxes);
        Assert.Equal(4, configuration.WorkspaceLimits.MaxCachedVolumes);
        Assert.Throws<InvalidOperationException>(() => WebRuntimeConfiguration.Read(source, true));
    }
}
