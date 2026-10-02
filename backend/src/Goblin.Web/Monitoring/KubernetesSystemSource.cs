using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Execution;

namespace Goblin.Web.Monitoring;

// Reads this machine's kubelet directly with nodes/stats permission. Never grants
// nodes/proxy (which would also allow execution), mounts host files, or exposes a proxy.
public sealed class KubernetesSystemSource : ISystemSource, IDisposable
{
    private readonly string? _apiAddress;
    private readonly string _nodeName;
    private readonly string _namespace;
    private readonly string _executionNamespace;
    private readonly string _tokenFile;
    private readonly string _caFile;
    private KubernetesApi? _api;
    private KubernetesApi? _kubelet;
    private string? _address;

    public KubernetesSystemSource(string? apiAddress, string nodeName, string appNamespace, string executionNamespace,
        string tokenFile, string caFile)
    {
        _apiAddress = apiAddress;
        _nodeName = nodeName;
        _namespace = appNamespace;
        _executionNamespace = executionNamespace;
        _tokenFile = tokenFile;
        _caFile = caFile;
    }

    public async Task<MachineSnapshot> ReadAsync(CancellationToken token)
    {
        // TLS/configuration errors are monitoring failures, never startup failures.
        _api ??= new KubernetesApi(_apiAddress, _tokenFile, _caFile);
        MonitoringNode node = await _api.GetAsync<MonitoringNode>("/api/v1/nodes/" + Uri.EscapeDataString(_nodeName), token)
            ?? throw new IOException("Machine observation unavailable.");
        string? ip = node.Status?.Addresses?.FirstOrDefault(x => x.Type == "InternalIP")?.Address;
        if (!IPAddress.TryParse(ip, out IPAddress? address)) throw new IOException("Machine address unavailable.");
        string origin = new UriBuilder("https", address.ToString(), 10250).Uri.AbsoluteUri;
        if (_address != origin)
        {
            _kubelet?.Dispose();
            _kubelet = new KubernetesApi(origin, _tokenFile, _caFile);
            _address = origin;
        }
        Task<KubeletSummary?> summaryTask = _kubelet!.GetAsync<KubeletSummary>("/stats/summary", token);
        Task<MonitoringPod[]?> servicesTask = ReadServicesAsync(token);
        await Task.WhenAll(summaryTask, servicesTask);
        KubeletSummary summary = await summaryTask ?? throw new IOException("Machine metrics unavailable.");
        return Project(node, summary, await servicesTask, DateTimeOffset.UtcNow);
    }

    private async Task<MonitoringPod[]?> ReadServicesAsync(CancellationToken token)
    {
        try
        {
            Task<MonitoringPodList?>[] tasks = [.. new[] { _namespace, _executionNamespace }.Distinct().Select(ns => _api!.GetAsync<MonitoringPodList>("/api/v1/namespaces/" + Uri.EscapeDataString(ns) + "/pods", token))];
            MonitoringPodList?[] lists = await Task.WhenAll(tasks);
            if (lists.Any(x => x?.Items is null)) return null;
            return [.. lists.SelectMany(x => x!.Items!).OfType<MonitoringPod>().Where(p => string.IsNullOrEmpty(p.Spec?.NodeName) || p.Spec.NodeName == _nodeName)];
        }
        catch when (!token.IsCancellationRequested) { return null; }
    }

    public static MachineSnapshot Project(MonitoringNode node, KubeletSummary summary, MonitoringPod[]? pods, DateTimeOffset now)
    {
        KubeletNodeStats stats = summary.Node ?? throw new IOException("Machine metrics unavailable.");
        string name = node.Metadata?.Name ?? "";
        if (name.Length == 0 || stats.NodeName != name) throw new IOException("Machine metrics do not match.");
        MonitoringCapacity? capacity = node.Status?.Capacity;
        double cpuTotal = Quantity(capacity?.Cpu ?? "");
        double memoryTotal = Quantity(capacity?.Memory ?? "");
        double diskTotal = Number(stats.Fs?.CapacityBytes) ?? 0;
        if (cpuTotal <= 0 || memoryTotal <= 0) throw new IOException("Machine capacity unavailable.");
        double? cpuUsed = Number(stats.Cpu?.UsageNanoCores) / 1_000_000_000;
        double? memoryUsed = Number(stats.Memory?.WorkingSetBytes);
        var cpu = new ResourceUsage(cpuTotal, cpuUsed, cpuUsed is null ? null : Math.Max(0, cpuTotal - cpuUsed.Value));
        var memory = new ResourceUsage(memoryTotal, memoryUsed, Number(stats.Memory?.AvailableBytes));
        var disk = new ResourceUsage(diskTotal, Number(stats.Fs?.UsedBytes), Number(stats.Fs?.AvailableBytes));
        DateTimeOffset observed = new[] { stats.Cpu?.Time, stats.Memory?.Time, stats.Fs?.Time }
            .Where(x => x is not null).Select(x => DateTimeOffset.Parse(x!, CultureInfo.InvariantCulture)).DefaultIfEmpty(DateTimeOffset.MinValue).Min();
        var warnings = new List<string>();
        MonitoringCondition[] conditions = node.Status?.Conditions ?? [];
        if (!conditions.Any(c => c.Type == "Ready" && c.Status == "True")) warnings.Add("The machine is not reporting ready.");
        foreach ((string type, string message) in new[] {
            ("MemoryPressure", "Kubernetes reports memory pressure."), ("DiskPressure", "Kubernetes reports disk pressure."),
            ("PIDPressure", "Kubernetes reports too many processes."), ("NetworkUnavailable", "The cluster network is unavailable.") })
        {
            string state = conditions.FirstOrDefault(c => c.Type == type)?.Status ?? "";
            if (state == "True") warnings.Add(message);
            else if (state == "Unknown") warnings.Add("A machine health check is unknown.");
        }
        if (node.Spec?.Unschedulable == true) warnings.Add("This machine is paused for new workloads.");
        if (cpu.Percent >= 85) warnings.Add("CPU usage is high.");
        if (memory.Percent >= 85) warnings.Add("Memory usage is high.");
        if (disk.Total > 0 && disk.Available / disk.Total < .1) warnings.Add("Less than 10% of disk space is available.");
        if (cpuUsed is null || memoryUsed is null || disk.Total <= 0 || disk.Available is null) warnings.Add("Some resource readings are unavailable.");
        ServiceHealth[]? services = pods?.Where(p => (p.Status?.Phase ?? "") != "Succeeded").Select(p =>
        {
            string phase = p.Status?.Phase ?? "";
            bool ready = p.Status?.Conditions?.Any(c => c.Type == "Ready" && c.Status == "True") == true;
            if (p.Metadata?.DeletionTimestamp is not null) { phase = "Stopping"; ready = false; }
            KubeletPodStats? usage = summary.Pods?.FirstOrDefault(s => (s.PodRef?.Uid ?? "") == (p.Metadata?.Uid ?? ""));
            int restarts = (p.Status?.ContainerStatuses ?? []).Sum(c => (int)(Number(c.RestartCount) ?? 0));
            return new ServiceHealth(p.Metadata?.Name ?? "", p.Metadata?.Namespace ?? "", phase, ready, restarts,
                Number(usage?.Cpu?.UsageNanoCores) / 1_000_000_000, Number(usage?.Memory?.WorkingSetBytes));
        }).OrderBy(p => p.Ready).ThenBy(p => p.Namespace).ThenBy(p => p.Name).ToArray();
        if (services is null) warnings.Add("Service health could not be checked.");
        else if (services.Any(p => !p.Ready)) warnings.Add("Some Goblin services or agent sandboxes are not ready.");
        double? uptime = DateTimeOffset.TryParse(stats.StartTime, CultureInfo.InvariantCulture, out DateTimeOffset start)
            ? Math.Max(0, (now - start).TotalSeconds) : null;
        string kernel = node.Status?.NodeInfo?.KernelVersion ?? "";
        return new(observed, name, kernel.Contains("microsoft", StringComparison.OrdinalIgnoreCase) ? "WSL" : "Linux VM",
            node.Status?.NodeInfo?.OsImage ?? "", uptime, cpu, memory, disk, warnings.Distinct().ToArray(), services);
    }

    private static double? Number(double? value) => value is { } number && double.IsFinite(number) && number >= 0 ? number : null;

    public static double Quantity(string value)
    {
        Match match = Regex.Match(value, @"^(?<value>\d+(?:\.\d+)?)(?<unit>n|u|m|[KMGTPE]i?|k)?$", RegexOptions.CultureInvariant);
        if (!match.Success) throw new FormatException("Unrecognized resource quantity.");
        string unit = match.Groups["unit"].Value;
        double factor = unit switch
        {
            "n" => 1e-9,
            "u" => 1e-6,
            "m" => 1e-3,
            "" => 1,
            _ => Math.Pow(unit.EndsWith('i') ? 1024 : 1000, "KMGTPE".IndexOf(char.ToUpperInvariant(unit[0])) + 1)
        };
        return double.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture) * factor;
    }

    public void Dispose() { _kubelet?.Dispose(); _api?.Dispose(); }
}
