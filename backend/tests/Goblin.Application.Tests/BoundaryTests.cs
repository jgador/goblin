using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Application.Work;
using Goblin.Contracts;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;
using Goblin.Execution;
using Xunit;

namespace Goblin.Application.Tests;

public sealed class BoundaryTests
{
    private static long _nextId = int.MaxValue;
    private static long NextId() => System.Threading.Interlocked.Increment(ref _nextId);

    [Fact]
    public void PublicContractsAndWorkOrchestrationDoNotReferenceProtocolsOrHttp()
    {
        foreach (Assembly? assembly in new[] { typeof(AccountView).Assembly, typeof(WorkStore).Assembly })
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), x =>
                x.Name!.Contains("Protocol", StringComparison.Ordinal) || x.Name.Contains("Integrations", StringComparison.Ordinal) ||
                x.Name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
        Assert.Equal("{\"type\":\"chatgpt\",\"email\":\"user@example.test\",\"planType\":\"self_serve_business_prolite\"}",
            JsonSerializer.Serialize<AccountView>(new ChatgptAccountView("user@example.test", "self_serve_business_prolite"), WorkStore.Json));
    }

    [Fact]
    public void RepositorySandboxMountsOnlyItsOwnInputsWorkspaceAndCredentials()
    {
        var work = new WorkItem(NextId(), "Edit assigned repository", DateTimeOffset.UtcNow);
        work.Assign(NextId(), DateTimeOffset.UtcNow);
        work.QueueExecution(NextId(), new("codex", NextId(), repository: new("owner/repo", "Goblin", "goblin@example.test")), DateTimeOffset.UtcNow);
        using var api = new KubernetesApi("http://127.0.0.1:1");
        var host = new SandboxHost(api, new("executions", "worker-image", "/private/codex", "http://goblin-repository:8788"), new UnusedHost(), new UnusedBroker());
        JsonObject manifest = host.Manifest(work.Snapshot(), false);
        JsonNode spec = manifest["spec"]!["podTemplate"]!["spec"]!;
        Assert.False(spec["automountServiceAccountToken"]!.GetValue<bool>());
        Assert.Equal("Never", spec["restartPolicy"]!.GetValue<string>());
        Assert.Equal(1000, spec["securityContext"]!["runAsUser"]!.GetValue<int>());
        string json = manifest.ToJsonString();
        Assert.DoesNotContain("postgres", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hostPath", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/private/", json, StringComparison.Ordinal);
        Assert.DoesNotContain("owner-password", json, StringComparison.Ordinal);
        Assert.Equal("Suspended", host.Manifest(work.Snapshot(), true)["spec"]!["operatingMode"]!.GetValue<string>());
    }

    private sealed class UnusedHost : IExecutionHost
    {
        public RuntimeCapabilities[] Capabilities => [];
        public string EnvironmentFor(long workId, long attemptId) => throw new NotSupportedException();
        public Task StartAsync(WorkSnapshot work, CancellationToken token) => throw new NotSupportedException();
        public Task<ExecutionObservation> ObserveAsync(WorkSnapshot work, bool stop, CancellationToken token) => throw new NotSupportedException();
        public Task CleanupAsync(WorkSnapshot work, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class UnusedBroker : IRepositoryBroker
    {
        public Task<string> PrepareAsync(WorkSnapshot work, CancellationToken token) => throw new NotSupportedException();
        public Task<ExecutionObservation?> ObserveAsync(WorkSnapshot work, CancellationToken token) => throw new NotSupportedException();
        public Task StopAsync(WorkSnapshot work, CancellationToken token) => throw new NotSupportedException();
        public Task ReleaseAsync(WorkSnapshot work, CancellationToken token) => throw new NotSupportedException();
    }
}
