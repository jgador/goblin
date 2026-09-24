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

namespace Goblin.Application.Tests;

public sealed class PersistentSandboxTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task RetryReusesStorageAndStaleStartSuspendAndCleanupCannotAffectIt()
    {
        await using ControlPlane plane = await ControlPlane.StartAsync();
        WorkItem work = Running(1, 10, plane.Host);
        WorkSnapshot first = work.Snapshot();
        await plane.Host.StartAsync(first, default);
        string volumeUid = plane.VolumeUid;
        await StopAsync(work, plane.Host);
        int stoppedMutations = plane.Mutations;
        Assert.Equal(ObservationKind.Stopped, (await plane.Host.ObserveAsync(first, true, default)).Kind);
        Assert.Equal(stoppedMutations, plane.Mutations);
        work.RetryExecution(11, work.CurrentAttempt!.Target, Now);
        work.TryClaimExecution(11, 101, plane.Host.EnvironmentFor(1, 11), Now);
        await plane.Host.StartAsync(work.Snapshot(), default);
        Assert.Equal(volumeUid, plane.VolumeUid);
        Assert.Single(plane.Resources.Keys, x => x.Contains("/persistentvolumeclaims/", StringComparison.Ordinal));
        Assert.Contains("/api/v1/namespaces/agents/secrets/input-1-11-1", plane.Resources.Keys);
        Assert.DoesNotContain("/api/v1/namespaces/agents/secrets/input-1-10-1", plane.Resources.Keys);
        int mutations = plane.Mutations, stops = plane.Broker.Stops;
        await plane.Host.StartAsync(first, default);
        await plane.Host.CleanupAsync(first, default);
        Assert.Equal(ObservationKind.Uncertain, (await plane.Host.ObserveAsync(first, true, default)).Kind);
        Assert.Equal(mutations, plane.Mutations); Assert.Equal(stops, plane.Broker.Stops);
        Assert.Equal("Running", plane.Sandbox["spec"]!["operatingMode"]!.GetValue<string>());
    }

    [Fact]
    public async Task SuspensionAndReasoningTurnResumeSameSandboxWithoutNativeSession()
    {
        await using ControlPlane plane = await ControlPlane.StartAsync();
        WorkItem work = Running(1, 10, plane.Host);
        await plane.Host.StartAsync(work.Snapshot(), default);
        string volumeUid = plane.VolumeUid;
        work.SaveWorkspace(10, 100, 1, Now);
        work.PauseForInput(10, 100, 1, "Continue?", true, Now);
        work.RequireCleanup(10, 100, Now);
        await plane.Host.CleanupAsync(work.Snapshot(), default);
        work.ConfirmCleanup(10, 100, Now);
        work.AnswerDecision(1, "Continue", Now);
        work.TryClaimExecution(10, 101, "text/10/turn/2", Now);
        work.RequireRepositoryExecution(10, 101, Now);
        work.TryClaimExecution(10, 102, plane.Host.EnvironmentFor(1, 10), Now);
        Assert.Null(work.CurrentAttempt!.Session);
        await plane.Host.StartAsync(work.Snapshot(), default);
        Assert.Equal(volumeUid, plane.VolumeUid);
        Assert.Equal("2", plane.Sandbox["metadata"]!["labels"]!["goblin-workspace"]!.GetValue<string>());
        Assert.Single(work.Attempts);
    }

    [Fact]
    public async Task MissingRetainedStorageNeverRestoresAnOlderCheckpointAutomatically()
    {
        await using ControlPlane plane = await ControlPlane.StartAsync();
        WorkItem work = Running(1, 10, plane.Host);
        await plane.Host.StartAsync(work.Snapshot(), default);
        await StopAsync(work, plane.Host);
        plane.Resources.Remove("/api/v1/namespaces/agents/persistentvolumeclaims/work-1");
        work.RetryExecution(11, work.CurrentAttempt!.Target, Now); work.TryClaimExecution(11, 101, plane.Host.EnvironmentFor(1, 11), Now);
        await Assert.ThrowsAsync<IOException>(() => plane.Host.StartAsync(work.Snapshot(), default));
        Assert.DoesNotContain(plane.Resources.Keys, x => x.Contains("/persistentvolumeclaims/", StringComparison.Ordinal));
        Assert.Equal("Suspended", plane.Sandbox["spec"]!["operatingMode"]!.GetValue<string>());
    }

    [Fact]
    public async Task StorageCapacityProtectsUnfinishedWorksButAllowsTheirExistingVolumes()
    {
        await using ControlPlane plane = await ControlPlane.StartAsync();
        WorkItem work = Running(1, 10, plane.Host);
        await plane.Host.StartAsync(work.Snapshot(), default); await StopAsync(work, plane.Host);
        WorkItem other = Running(2, 20, plane.Host);
        await Assert.ThrowsAsync<IOException>(() => plane.Host.StartAsync(other.Snapshot(), default));
        Assert.Single(plane.Resources.Keys, x => x.Contains("/persistentvolumeclaims/", StringComparison.Ordinal));
        work.RetryExecution(11, work.CurrentAttempt!.Target, Now); work.TryClaimExecution(11, 101, plane.Host.EnvironmentFor(1, 11), Now);
        await plane.Host.StartAsync(work.Snapshot(), default);
        Assert.Equal("Running", plane.Sandbox["spec"]!["operatingMode"]!.GetValue<string>());
    }

    [Fact]
    public async Task ConcurrentVersionChangePreventsActivationAndRemovesItsCredentials()
    {
        await using ControlPlane plane = await ControlPlane.StartAsync();
        plane.ConflictOnActivation = true;
        await Assert.ThrowsAsync<IOException>(() => plane.Host.StartAsync(Running(1, 10, plane.Host).Snapshot(), default));
        Assert.Equal("Suspended", plane.Sandbox["spec"]!["operatingMode"]!.GetValue<string>());
        Assert.DoesNotContain(plane.Resources.Keys, x => x.Contains("/secrets/", StringComparison.Ordinal));
        int mutations = plane.Mutations;
        await plane.Host.StartAsync(Running(1, 10, plane.Host).Snapshot(), default);
        Assert.Equal(mutations, plane.Mutations);
    }

    [Fact]
    public async Task UnconfirmedSuspensionKeepsFilesAndCannotPermitAnotherAllocation()
    {
        await using ControlPlane plane = await ControlPlane.StartAsync();
        WorkItem work = Running(1, 10, plane.Host);
        await plane.Host.StartAsync(work.Snapshot(), default);
        string volumeUid = plane.VolumeUid;
        work.ExecutionFailed(10, 100, FailureKind.ExecutionFailed, Now);
        work.RequireCleanup(10, 100, Now);
        plane.FailPodReads = true;
        await Assert.ThrowsAsync<IOException>(() => plane.Host.CleanupAsync(work.Snapshot(), default));
        work.ReportCleanupFailure(10, 100, Now);
        Assert.Throws<WorkRuleException>(() => work.RetryExecution(11, work.CurrentAttempt!.Target, Now));
        Assert.Equal(volumeUid, plane.VolumeUid);
        Assert.Equal("stopping", plane.Sandbox["metadata"]!["labels"]!["goblin-phase"]!.GetValue<string>());
        Assert.Contains("/api/v1/namespaces/agents/secrets/input-1-10-1", plane.Resources.Keys);
    }

    [Fact]
    public async Task ReconciliationCanSuspendAPodThatRefusedToRepeatAClaimedTurn()
    {
        await using ControlPlane plane = await ControlPlane.StartAsync();
        WorkItem work = Running(1, 10, plane.Host);
        await plane.Host.StartAsync(work.Snapshot(), default);
        plane.Logs = "GOBLIN_RESULT {\"kind\":\"Uncertain\",\"failure\":\"HostUnavailable\",\"turnNumber\":1}";
        Assert.Equal(ObservationKind.Uncertain, (await plane.Host.ObserveAsync(work.Snapshot(), false, default)).Kind);
        work.ExecutionUncertain(10, 100, FailureKind.HostUnavailable, Now);
        Assert.Equal(ObservationKind.Stopped, (await plane.Host.ObserveAsync(work.Snapshot(), false, default)).Kind);
        work.ConfirmExecutionStopped(10, 100, Now); work.RequireCleanup(10, 100, Now);
        await plane.Host.CleanupAsync(work.Snapshot(), default); work.ConfirmCleanup(10, 100, Now);
        Assert.Equal(AttentionReason.Failure, work.Attention!.Reason);
        Assert.Single(work.Attempts);
        work.RetryExecution(11, work.CurrentAttempt!.Target, Now);
        Assert.Equal(AttemptStatus.Queued, work.CurrentAttempt!.Status);
    }

    private static WorkItem Running(long id, long attempt, SandboxHost host)
    {
        var work = new WorkItem(id, "Edit repository", Now); work.Assign(1, Now);
        work.QueueExecution(attempt, new("codex", 1, repository: new("owner/repo", "Goblin", "goblin@example.test")), Now);
        work.TryClaimExecution(attempt, 100, host.EnvironmentFor(id, attempt), Now); return work;
    }
    private static async Task StopAsync(WorkItem work, SandboxHost host)
    {
        ExecutionAttempt attempt = work.CurrentAttempt!;
        work.ExecutionFailed(attempt.Id, attempt.OwnerId!.Value, FailureKind.ExecutionFailed, Now);
        work.RequireCleanup(attempt.Id, attempt.OwnerId.Value, Now);
        await host.CleanupAsync(work.Snapshot(), default);
        work.ConfirmCleanup(attempt.Id, attempt.OwnerId.Value, Now);
    }

    private sealed class ControlPlane : IAsyncDisposable
    {
        private readonly WebApplication _server;
        private readonly string _auth;
        private readonly KubernetesApi _api;
        private int _version;
        public Dictionary<string, JsonNode> Resources { get; } = [];
        public Broker Broker { get; } = new();
        public SandboxHost Host { get; }
        public bool ConflictOnActivation { get; set; }
        public bool FailPodReads { get; set; }
        public string? Logs { get; set; }
        public int Mutations { get; private set; }
        public JsonNode Sandbox => Resources["/apis/agents.x-k8s.io/v1beta1/namespaces/agents/sandboxes/work-1"];
        public string VolumeUid => Resources["/api/v1/namespaces/agents/persistentvolumeclaims/work-1"]["metadata"]!["uid"]!.GetValue<string>();
        private ControlPlane(WebApplication server)
        {
            _server = server; _auth = Path.Combine(Path.GetTempPath(), "goblin-sandbox-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_auth); File.WriteAllText(Path.Combine(_auth, "auth.json"), "{}");
            _api = new(server.Urls.Single());
            Host = new(_api, new("agents", "image", _auth, "http://repository"), new TextHost(), Broker, new Archives(), new(MaxCachedVolumes: 1));
        }
        public static async Task<ControlPlane> StartAsync()
        {
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
            WebApplication server = builder.Build(); server.Urls.Add("http://127.0.0.1:0");
            ControlPlane? plane = null; server.Run(context => plane!.HandleAsync(context));
            await server.StartAsync(); plane = new(server); return plane;
        }
        private async Task HandleAsync(HttpContext context)
        {
            string path = context.Request.Path.Value!, method = context.Request.Method;
            JsonNode? body = context.Request.ContentLength > 0 ? await JsonNode.ParseAsync(context.Request.Body) : null;
            if (method == "GET")
            {
                if (FailPodReads && path.Contains("/pods/", StringComparison.Ordinal)) { context.Response.StatusCode = 503; return; }
                if (Logs is not null && path.EndsWith("/log", StringComparison.Ordinal)) { await context.Response.WriteAsync(Logs); return; }
                if (Resources.TryGetValue(path, out JsonNode? resource)) await context.Response.WriteAsJsonAsync(resource);
                else if (path.EndsWith("/pods", StringComparison.Ordinal) || path.EndsWith("/persistentvolumeclaims", StringComparison.Ordinal))
                    await context.Response.WriteAsJsonAsync(new { items = Resources.Where(x => x.Key.StartsWith(path + "/", StringComparison.Ordinal)).Select(x => x.Value).ToArray() });
                else context.Response.StatusCode = 404;
                return;
            }
            if (method == "POST")
            {
                path += "/" + body!["metadata"]!["name"]!.GetValue<string>();
                if (Resources.ContainsKey(path)) { context.Response.StatusCode = 409; return; }
                Resources[path] = body; body["metadata"]!["uid"] = Guid.NewGuid().ToString("N");
            }
            else if (method == "PATCH")
            {
                JsonNode saved = Resources[path];
                string? expected = body?["metadata"]?["resourceVersion"]?.GetValue<string>();
                if ((expected is not null && expected != saved["metadata"]!["resourceVersion"]!.GetValue<string>()) ||
                    (ConflictOnActivation && body?["spec"]?["operatingMode"]?.GetValue<string>() == "Running"))
                { context.Response.StatusCode = 409; return; }
                Merge(saved, body!);
            }
            else if (method == "DELETE") { Resources.Remove(path); Mutations++; await context.Response.WriteAsync("{}"); return; }
            JsonNode value = Resources[path];
            value["metadata"]!["resourceVersion"] = (++_version).ToString(); Mutations++;
            if (path.Contains("/sandboxes/", StringComparison.Ordinal))
            {
                string pod = "/api/v1/namespaces/agents/pods/" + value["metadata"]!["name"]!.GetValue<string>();
                if (value["spec"]!["operatingMode"]!.GetValue<string>() == "Running")
                    Resources[pod] = new JsonObject { ["metadata"] = value["metadata"]!.DeepClone(), ["spec"] = value["spec"]!["podTemplate"]!["spec"]!.DeepClone(), ["status"] = new JsonObject { ["phase"] = "Running" } };
                else Resources.Remove(pod);
            }
            await context.Response.WriteAsJsonAsync(value);
        }
        private static void Merge(JsonNode target, JsonNode patch)
        {
            foreach (KeyValuePair<string, JsonNode?> entry in patch.AsObject())
                if (entry.Value is JsonObject && target[entry.Key] is JsonObject child) Merge(child, entry.Value);
                else target[entry.Key] = entry.Value?.DeepClone();
        }
        public async ValueTask DisposeAsync() { _api.Dispose(); await _server.DisposeAsync(); Directory.Delete(_auth, true); }
    }
    private sealed class Broker : IRepositoryBroker
    {
        public int Stops { get; private set; }
        public Task<string> PrepareAsync(WorkSnapshot work, CancellationToken token) => Task.FromResult("fixture-capability");
        public Task<ExecutionObservation?> ObserveAsync(WorkSnapshot work, CancellationToken token) => Task.FromResult<ExecutionObservation?>(null);
        public Task StopAsync(WorkSnapshot work, CancellationToken token) { Stops++; return Task.CompletedTask; }
        public Task ReleaseAsync(WorkSnapshot work, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class Archives : IWorkspaceArchive
    {
        public Task<WorkspaceCheckpoint?> LatestAsync(long workId, string repository, CancellationToken token) => Task.FromResult<WorkspaceCheckpoint?>(null);
        public Task<bool> VerifiedAsync(long id, long attemptId, int turnNumber, CancellationToken token) => Task.FromResult(true);
        public Task<bool> CanDiscardAsync(long attemptId, int workspaceNumber, CancellationToken token) => Task.FromResult(false);
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
