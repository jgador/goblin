using System.Text.Json;
using Goblin.Execution.Kubernetes;
using Xunit;

namespace Goblin.Application.Tests;

public sealed class KubernetesSerializationTests
{
    [Fact]
    public void SuspensionPatchContainsOnlyTheMergePatchIntent()
    {
        var patch = new SandboxSuspendPatch
        {
            Spec = new() { OperatingMode = SandboxSuspendPatchSpecOperatingMode.Suspended }
        };
        Assert.Equal("{\"spec\":{\"operatingMode\":\"Suspended\"}}",
            JsonSerializer.Serialize(patch, KubernetesJson.Options));
    }

    [Fact]
    public void SelectedSandboxReadsKnownFieldsAndIgnoresUnselectedServerFields()
    {
        const string response = """
            {"metadata":{"name":"run-1-1","resourceVersion":"8"},
             "spec":{"operatingMode":"Suspended","podTemplate":{"spec":{"containers":[]}},"shutdownTime":"2030-01-01T00:00:00Z"},
             "status":{"conditions":[{"type":"Ready","status":"False"}]}}
            """;
        Sandbox sandbox = JsonSerializer.Deserialize<Sandbox>(response, KubernetesJson.Options)!;
        Assert.Equal("run-1-1", sandbox.Metadata?.Name);
        Assert.Equal(SandboxSpecOperatingMode.Suspended, sandbox.Spec.OperatingMode);
        Assert.Empty(sandbox.Spec.PodTemplate.Spec.Containers);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Sandbox>(
            response.Replace("Suspended", "Unknown", System.StringComparison.Ordinal), KubernetesJson.Options));
    }

    [Fact]
    public void KubernetesQuantityUnionKeepsStringAndIntegerWireForms()
    {
        Assert.Equal("\"2Gi\"", JsonSerializer.Serialize(new KubernetesIntOrString("2Gi"), KubernetesJson.Options));
        Assert.Equal("8", JsonSerializer.Serialize(new KubernetesIntOrString(8), KubernetesJson.Options));
        Assert.Equal("2Gi", JsonSerializer.Deserialize<KubernetesIntOrString>("\"2Gi\"", KubernetesJson.Options)!.Text);
        Assert.Equal(8, JsonSerializer.Deserialize<KubernetesIntOrString>("8", KubernetesJson.Options)!.Number);
    }
}
