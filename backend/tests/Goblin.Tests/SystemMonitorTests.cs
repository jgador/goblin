using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Web.Monitoring;
using Xunit;

namespace Goblin.Tests;

public sealed class SystemMonitorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static JsonObject Node() => JsonNode.Parse("""
        {"metadata":{"name":"vm"},"status":{"capacity":{"cpu":"4","memory":"8Gi"},
        "nodeInfo":{"kernelVersion":"microsoft-WSL2","osImage":"Ubuntu"},
        "conditions":[{"type":"Ready","status":"True"},{"type":"MemoryPressure","status":"False"}]}}
        """)!.AsObject();
    private static JsonObject Summary() => JsonNode.Parse($$$"""
        {"node":{"nodeName":"vm","startTime":"{{{Now.AddHours(-2):O}}}",
        "cpu":{"time":"{{{Now:O}}}","usageNanoCores":1000000000},
        "memory":{"time":"{{{Now:O}}}","workingSetBytes":6442450944,"availableBytes":2147483648},
        "fs":{"time":"{{{Now:O}}}","capacityBytes":107374182400,"usedBytes":85899345920,"availableBytes":16106127360}},
        "pods":[{"podRef":{"uid":"app"},"cpu":{"usageNanoCores":200000000},"memory":{"workingSetBytes":104857600}}]}
        """)!.AsObject();

    [Fact]
    public void ReportsWholeVmUsageInsteadOfSummingPodsAndPreservesDiskReservations()
    {
        JsonObject pod = JsonNode.Parse("""
            {"metadata":{"name":"goblin","namespace":"goblin","uid":"app"},
            "status":{"phase":"Running","conditions":[{"type":"Ready","status":"True"}],
            "containerStatuses":[{"restartCount":2}]}}
            """)!.AsObject();
        MachineSnapshot view = KubernetesSystemSource.Project(Node(), Summary(), [pod], Now);
        Assert.Equal(25, view.Cpu.Percent);
        Assert.Equal(75, view.Memory.Percent);
        Assert.Equal(80, view.Disk.Percent);
        Assert.Equal(15 * Math.Pow(1024, 3), view.Disk.Available);
        Assert.Equal(7200, view.UptimeSeconds);
        Assert.Equal("WSL", view.Environment);
        Assert.Empty(view.Warnings);
        Assert.Equal(.2, Assert.Single(view.Services!).CpuCores);
        Assert.Equal(2, view.Services![0].Restarts);
    }

    [Fact]
    public void MissingMetricsAreUnknownAndPendingPodsCountWhileCompletedPodsDoNot()
    {
        JsonObject summary = Summary();
        summary["node"]!["cpu"]!.AsObject().Remove("usageNanoCores");
        JsonObject pending = JsonNode.Parse("""{"metadata":{"name":"agent","namespace":"goblin-executions","uid":"pending"},"status":{"phase":"Pending"}}""")!.AsObject();
        JsonObject completed = JsonNode.Parse("""{"metadata":{"name":"finished"},"status":{"phase":"Succeeded"}}""")!.AsObject();
        MachineSnapshot view = KubernetesSystemSource.Project(Node(), summary, [pending, completed], Now);
        Assert.Null(view.Cpu.Used);
        Assert.Null(view.Cpu.Percent);
        Assert.Null(view.Cpu.Available);
        Assert.False(Assert.Single(view.Services!).Ready);
        Assert.Equal(2, view.Warnings.Count);
        Assert.Throws<IOException>(() => KubernetesSystemSource.Project(Node(), JsonNode.Parse("""{"node":{"nodeName":"other"}}""")!.AsObject(), [], Now));
    }

    [Fact]
    public void PressureAndUnreadyConditionsCannotReportHealthy()
    {
        JsonObject node = Node();
        node["status"]!["conditions"]![0]!["status"] = "Unknown";
        node["status"]!["conditions"]![1]!["status"] = "True";
        MachineSnapshot view = KubernetesSystemSource.Project(node, Summary(), null, Now);
        Assert.Contains("The machine is not reporting ready.", view.Warnings);
        Assert.Contains("Kubernetes reports memory pressure.", view.Warnings);
        Assert.Contains("Service health could not be checked.", view.Warnings);
    }

    [Fact]
    public async Task InterruptedOrOldReadingsAreStaleWithoutExposingErrorsAndRecoverWithBoundedHistory()
    {
        var source = new Source(KubernetesSystemSource.Project(Node(), Summary(), [], Now));
        using var monitor = new SystemMonitor(source);
        await monitor.CollectAsync(CancellationToken.None);
        Assert.Equal("live", monitor.Current.Status);
        source.Fail = true;
        await monitor.CollectAsync(CancellationToken.None);
        Assert.Equal("stale", monitor.Current.Status);
        Assert.NotNull(monitor.Current.Machine);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(monitor.Current));
        source.Fail = false;
        source.Snapshot = source.Snapshot with { ObservedAt = Now.AddMinutes(-10) };
        await monitor.CollectAsync(CancellationToken.None);
        Assert.Equal("stale", monitor.Current.Status);
        for (int i = 0; i < 40; i++)
        {
            source.Snapshot = source.Snapshot with { ObservedAt = Now.AddMilliseconds(i) };
            await monitor.CollectAsync(CancellationToken.None);
        }
        Assert.Equal("live", monitor.Current.Status);
        Assert.Equal(21, monitor.Current.History.Count);
    }

    [Fact]
    public async Task UnsupportedAndUnavailableAreDifferentFromZeroUsage()
    {
        using var unsupported = new SystemMonitor(null);
        Assert.Equal("unsupported", unsupported.Current.Status);
        var source = new Source(KubernetesSystemSource.Project(Node(), Summary(), [], Now)) { Fail = true };
        using var monitor = new SystemMonitor(source);
        await monitor.CollectAsync(CancellationToken.None);
        Assert.Equal("unavailable", monitor.Current.Status);
        Assert.Null(monitor.Current.Machine);
        Assert.Empty(monitor.Current.History);
    }

    [Theory]
    [InlineData("12079748Ki", 12369661952)]
    [InlineData("100m", .1)]
    [InlineData("1Gi", 1073741824)]
    [InlineData("1G", 1000000000)]
    public void UnderstandsKubernetesCapacityUnits(string value, double expected) => Assert.Equal(expected, KubernetesSystemSource.Quantity(value));

    [Fact]
    public async Task MissingClusterCredentialsDoNotPreventStartupOrExposePaths()
    {
        using var source = new KubernetesSystemSource(null, "vm", "goblin", "goblin-executions", "/missing/private-token", "/missing/private-ca");
        using var monitor = new SystemMonitor(source);
        await monitor.CollectAsync(CancellationToken.None);
        Assert.Equal("unavailable", monitor.Current.Status);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(monitor.Current));
    }

    private sealed class Source : ISystemSource
    {
        public MachineSnapshot Snapshot { get; set; }
        public bool Fail { get; set; }
        public Source(MachineSnapshot snapshot) { Snapshot = snapshot; }
        public Task<MachineSnapshot> ReadAsync(CancellationToken token) => Fail
            ? throw new IOException("secret upstream body") : Task.FromResult(Snapshot);
    }
}
