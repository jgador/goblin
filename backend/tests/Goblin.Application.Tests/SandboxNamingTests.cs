using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;
using Goblin.Execution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;
using K = Goblin.Execution.Kubernetes;

namespace Goblin.Application.Tests;

public sealed class SandboxNamingTests
{
    [Theory]
    [InlineData("k8s/agents/work-101", "agents", "work-101")]
    [InlineData("k8s/custom-agents/work-101", "custom-agents", "work-101")]
    public async Task StartObservationAndCleanupUseThePersistedAddress(string reference, string expectedNamespace, string name)
    {
        var requests = new List<(string Method, string Path, JsonNode? Body)>();
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        await using WebApplication server = builder.Build();
        server.Urls.Add("http://127.0.0.1:0");
        server.Run(async context =>
        {
            JsonNode? body = context.Request.ContentLength > 0 ? await JsonNode.ParseAsync(context.Request.Body) : null;
            requests.Add((context.Request.Method, context.Request.Path.Value!, body));
            // An existing identity must short-circuit Start before credentials or
            // repository setup.
            if (context.Request.Method == "POST") context.Response.StatusCode = 409;
            else if (context.Request.Path.Value!.Contains("/pods/", StringComparison.Ordinal)) context.Response.StatusCode = 404;
            else if (context.Request.Path.Value!.EndsWith("/pods", StringComparison.Ordinal))
                await context.Response.WriteAsync("{\"items\":[]}");
            else await context.Response.WriteAsJsonAsync(new
            {
                metadata = new { resourceVersion = "1", labels = new Dictionary<string, string> { ["goblin-work"] = "101", ["goblin-attempt"] = "12", ["goblin-workspace"] = "1", ["goblin-phase"] = "stopping" } },
                spec = new
                {
                    operatingMode = "Suspended",
                    podTemplate = new
                    {
                        spec = new
                        {
                            containers = Array.Empty<object>(),
                            volumes = new object[] {
                    new { name = "credentials", secret = new { secretName = "input-101-12-1" } }, new { name = "input", configMap = new { name = "input-101-12-1" } } }
                        }
                    }
                }
            });
        });
        await server.StartAsync();
        using var api = new KubernetesApi(server.Urls.Single());
        var broker = new Broker();
        var host = new SandboxHost(api, new("new-default", "worker-image", "/missing/credentials", "http://repository"), new TextHost(), broker);
        WorkSnapshot work = Work(reference);
        JsonObject manifest = Json(host.Manifest(work, false));
        Assert.Equal(expectedNamespace, manifest["metadata"]!["namespace"]!.GetValue<string>());
        Assert.Equal(name, manifest["metadata"]!["name"]!.GetValue<string>());
        Assert.Equal(name, manifest["spec"]!["podTemplate"]!["spec"]!["volumes"]![0]!["persistentVolumeClaim"]!["claimName"]!.GetValue<string>());
        Assert.Equal("12", manifest["metadata"]!["labels"]!["goblin-attempt"]!.GetValue<string>());
        Assert.Equal("101", manifest["metadata"]!["labels"]!["goblin-work"]!.GetValue<string>());

        await host.StartAsync(work, CancellationToken.None);
        Assert.Equal(ObservationKind.Stopped, (await host.ObserveAsync(work, true, CancellationToken.None)).Kind);
        await host.CleanupAsync(work, CancellationToken.None);

        Assert.All(requests, request => Assert.Contains("/namespaces/" + expectedNamespace + "/", request.Path));
        Assert.DoesNotContain(requests, request => request.Method == "POST");
        Assert.All(requests.Where(request => request.Method == "PATCH"), request =>
        {
            Assert.EndsWith("/sandboxes/" + name, request.Path);
            Assert.Equal("Suspended", request.Body!["spec"]!["operatingMode"]!.GetValue<string>());
        });
        Assert.Equal(new[] { "/api/v1/namespaces/" + expectedNamespace + "/secrets/input-101-12-1",
            "/api/v1/namespaces/" + expectedNamespace + "/configmaps/input-101-12-1" },
            requests.Where(request => request.Method == "DELETE").Select(request => request.Path));
        Assert.Equal(2, broker.Stops);
        Assert.Equal(1, broker.Releases);
    }

    [Theory]
    [InlineData("agents")]
    [InlineData("custom-agents")]
    public void WorkspacesUseWorkNamesInTheConfiguredNamespace(string executionNamespace)
    {
        using var api = new KubernetesApi("http://127.0.0.1:1");
        var host = new SandboxHost(api, new(executionNamespace, "image", "/private", "http://repository"), new TextHost(), new Broker());
        Assert.Equal("k8s/" + executionNamespace + "/work-101", host.EnvironmentFor(101, 12));
        JsonObject manifest = Json(host.Manifest(Work(host.EnvironmentFor(101, 12)), false));
        Assert.Equal(executionNamespace, manifest["metadata"]!["namespace"]!.GetValue<string>());
        Assert.Equal("work-101", manifest["metadata"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void AttemptsAndRevisionsKeepTheWorkSandboxAndVolume()
    {
        using var api = new KubernetesApi("http://127.0.0.1:1");
        var host = new SandboxHost(api, new("agents", "image", "/private", "http://repository"), new TextHost(), new Broker());
        var work = WorkItem.Restore(Work(host.EnvironmentFor(101, 12)));
        WorkSnapshot first = work.Snapshot();
        Assert.Equal("work-101", Json(host.Manifest(first, false))["metadata"]!["name"]!.GetValue<string>());

        work.ExecutionFailed(12, 1, FailureKind.ExecutionFailed, DateTimeOffset.UtcNow);
        work.RequireCleanup(12, 1, DateTimeOffset.UtcNow);
        work.ConfirmCleanup(12, 1, DateTimeOffset.UtcNow);
        Assert.Single(work.Attempts); // Failure alone never creates another run.
        work.RetryExecution(907, first.Attempts[0].Target, DateTimeOffset.UtcNow);
        Assert.True(work.TryClaimExecution(907, 2, host.EnvironmentFor(101, 907), DateTimeOffset.UtcNow));
        WorkSnapshot second = WorkItem.Restore(work.Snapshot()).Snapshot();
        JsonObject manifest = Json(host.Manifest(second, false));
        Assert.Equal("work-101", manifest["metadata"]!["name"]!.GetValue<string>());
        Assert.Equal("907", manifest["metadata"]!["labels"]!["goblin-attempt"]!.GetValue<string>());
        Assert.Equal("work-101", manifest["spec"]!["podTemplate"]!["spec"]!["volumes"]![0]!["persistentVolumeClaim"]!["claimName"]!.GetValue<string>());
        Assert.Equal("work-101", Json(host.Manifest(first, true))["metadata"]!["name"]!.GetValue<string>());

        // Requested revisions use a new attempt in the same Work workspace.
        work.ProposeResult(907, 2, "Proposed change", DateTimeOffset.UtcNow);
        work.RequireCleanup(907, 2, DateTimeOffset.UtcNow);
        work.ConfirmCleanup(907, 2, DateTimeOffset.UtcNow);
        work.RequestChanges(907, "Revise it", DateTimeOffset.UtcNow);
        work.QueueExecution(3001, first.Attempts[0].Target, DateTimeOffset.UtcNow);
        Assert.True(work.TryClaimExecution(3001, 3, host.EnvironmentFor(101, 3001), DateTimeOffset.UtcNow));
        Assert.Equal("work-101", Json(host.Manifest(work.Snapshot(), false))["metadata"]!["name"]!.GetValue<string>());
        Assert.Equal(new long[] { 12, 907, 3001 }, work.Attempts.Select(attempt => attempt.Id));
        Assert.Equal("work-202", Json(host.Manifest(Work(host.EnvironmentFor(202, 4000), 202, 4000), false))["metadata"]!["name"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("k8s/agents/work-102")]
    [InlineData("k8s/agents/work-101/12")]
    [InlineData("k8s/../work-101")]
    [InlineData("k8s/agents/other-101")]
    [InlineData("unknown/12")]
    public async Task InvalidReferencesCannotFallBackToANewExecution(string reference)
    {
        using var api = new KubernetesApi("http://127.0.0.1:1");
        var host = new SandboxHost(api, new("agents", "image", "/private", "http://repository"), new TextHost(), new Broker());
        WorkSnapshot work = Work(reference);
        Assert.Throws<IOException>(() => host.Manifest(work, false));
        Assert.Equal("Unrecognized execution environment reference.",
            (await Assert.ThrowsAsync<IOException>(() => host.StartAsync(work, CancellationToken.None))).Message);
        await Assert.ThrowsAsync<IOException>(() => host.ObserveAsync(work, true, CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => host.CleanupAsync(work, CancellationToken.None));
    }

    private static WorkSnapshot Work(string reference, long workId = 101, long attemptId = 12)
    {
        var work = new WorkItem(workId, "Edit repository", DateTimeOffset.UtcNow);
        work.Assign(1, DateTimeOffset.UtcNow);
        work.QueueExecution(attemptId, new("codex", 1, repository: new("owner/repo", "Goblin", "goblin@example.test")), DateTimeOffset.UtcNow);
        Assert.True(work.TryClaimExecution(attemptId, 1, reference, DateTimeOffset.UtcNow));
        return work.Snapshot();
    }

    private static JsonObject Json(K.Sandbox manifest) => JsonSerializer.SerializeToNode(manifest, K.KubernetesJson.Options)!.AsObject();

    private sealed class Broker : IRepositoryBroker
    {
        public int Stops { get; private set; }
        public int Releases { get; private set; }
        public Task<string> PrepareAsync(WorkSnapshot work, CancellationToken token) => throw new NotSupportedException();
        public Task<ExecutionObservation?> ObserveAsync(WorkSnapshot work, CancellationToken token) => Task.FromResult<ExecutionObservation?>(null);
        public Task StopAsync(WorkSnapshot work, CancellationToken token) { Stops++; return Task.CompletedTask; }
        public Task ReleaseAsync(WorkSnapshot work, CancellationToken token) { Releases++; return Task.CompletedTask; }
    }

    private sealed class TextHost : IExecutionHost
    {
        public RuntimeCapabilities[] Capabilities => [];
        public string EnvironmentFor(long workId, long attemptId) => throw new NotSupportedException();
        public Task StartAsync(WorkSnapshot work, CancellationToken token) => throw new NotSupportedException();
        public Task<ExecutionObservation> ObserveAsync(WorkSnapshot work, bool stop, CancellationToken token) => throw new NotSupportedException();
        public Task CleanupAsync(WorkSnapshot work, CancellationToken token) => throw new NotSupportedException();
    }
}
