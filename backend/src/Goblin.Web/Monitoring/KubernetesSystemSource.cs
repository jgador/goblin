using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
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
        JsonObject node = await _api.GetAsync("/api/v1/nodes/" + Uri.EscapeDataString(_nodeName), token)
            ?? throw new IOException("Machine observation unavailable.");
        string? ip = node["status"]?["addresses"]?.AsArray()
            .FirstOrDefault(x => Text(x?["type"]) == "InternalIP")?["address"]?.GetValue<string>();
        if (!IPAddress.TryParse(ip, out IPAddress? address)) throw new IOException("Machine address unavailable.");
        string origin = new UriBuilder("https", address.ToString(), 10250).Uri.AbsoluteUri;
        if (_address != origin)
        {
            _kubelet?.Dispose();
            _kubelet = new KubernetesApi(origin, _tokenFile, _caFile);
            _address = origin;
        }
        Task<JsonObject?> summaryTask = _kubelet!.GetAsync("/stats/summary", token);
        Task<JsonObject[]?> servicesTask = ReadServicesAsync(token);
        await Task.WhenAll(summaryTask, servicesTask);
        JsonObject summary = await summaryTask ?? throw new IOException("Machine metrics unavailable.");
        return Project(node, summary, await servicesTask, DateTimeOffset.UtcNow);
    }
    private async Task<JsonObject[]?> ReadServicesAsync(CancellationToken token)
    {
        try
        {
            Task<JsonObject?>[] tasks = [.. new[] { _namespace, _executionNamespace }.Distinct().Select(ns => _api!.GetAsync("/api/v1/namespaces/" + Uri.EscapeDataString(ns) + "/pods", token))];
            JsonObject?[] lists = await Task.WhenAll(tasks);
            if (lists.Any(x => x?["items"] is not JsonArray)) return null;
            return [.. lists.SelectMany(x => x!["items"]!.AsArray()).OfType<JsonObject>().Where(p => Text(p["spec"]?["nodeName"]) is "" || Text(p["spec"]?["nodeName"]) == _nodeName)];
        }
        catch when (!token.IsCancellationRequested) { return null; }
    }
    public static MachineSnapshot Project(JsonObject node, JsonObject summary, JsonObject[]? pods, DateTimeOffset now)
    {
        JsonNode stats = summary["node"] ?? throw new IOException("Machine metrics unavailable.");
        string name = Text(node["metadata"]?["name"]);
        if (name.Length == 0 || Text(stats["nodeName"]) != name) throw new IOException("Machine metrics do not match.");
        JsonNode? capacity = node["status"]?["capacity"];
        double cpuTotal = Quantity(Text(capacity?["cpu"]));
        double memoryTotal = Quantity(Text(capacity?["memory"]));
        double diskTotal = Number(stats["fs"]?["capacityBytes"]) ?? 0;
        if (cpuTotal <= 0 || memoryTotal <= 0) throw new IOException("Machine capacity unavailable.");
        double? cpuUsed = Number(stats["cpu"]?["usageNanoCores"]) / 1_000_000_000;
        double? memoryUsed = Number(stats["memory"]?["workingSetBytes"]);
        var cpu = new ResourceUsage(cpuTotal, cpuUsed, cpuUsed is null ? null : Math.Max(0, cpuTotal - cpuUsed.Value));
        var memory = new ResourceUsage(memoryTotal, memoryUsed, Number(stats["memory"]?["availableBytes"]));
        var disk = new ResourceUsage(diskTotal, Number(stats["fs"]?["usedBytes"]), Number(stats["fs"]?["availableBytes"]));
        DateTimeOffset observed = new[] { stats["cpu"]?["time"], stats["memory"]?["time"], stats["fs"]?["time"] }
            .Where(x => x is not null).Select(x => DateTimeOffset.Parse(Text(x), CultureInfo.InvariantCulture)).DefaultIfEmpty(DateTimeOffset.MinValue).Min();
        var warnings = new List<string>();
        JsonArray conditions = node["status"]?["conditions"]?.AsArray() ?? [];
        if (!conditions.Any(c => Text(c?["type"]) == "Ready" && Text(c?["status"]) == "True")) warnings.Add("The machine is not reporting ready.");
        foreach ((string type, string message) in new[] {
            ("MemoryPressure", "Kubernetes reports memory pressure."), ("DiskPressure", "Kubernetes reports disk pressure."),
            ("PIDPressure", "Kubernetes reports too many processes."), ("NetworkUnavailable", "The cluster network is unavailable.") })
        {
            string state = Text(conditions.FirstOrDefault(c => Text(c?["type"]) == type)?["status"]);
            if (state == "True") warnings.Add(message);
            else if (state == "Unknown") warnings.Add("A machine health check is unknown.");
        }
        if (node["spec"]?["unschedulable"]?.GetValue<bool>() == true) warnings.Add("This machine is paused for new workloads.");
        if (cpu.Percent >= 85) warnings.Add("CPU usage is high.");
        if (memory.Percent >= 85) warnings.Add("Memory usage is high.");
        if (disk.Total > 0 && disk.Available / disk.Total < .1) warnings.Add("Less than 10% of disk space is available.");
        if (cpuUsed is null || memoryUsed is null || disk.Total <= 0 || disk.Available is null) warnings.Add("Some resource readings are unavailable.");
        ServiceHealth[]? services = pods?.Where(p => Text(p["status"]?["phase"]) != "Succeeded").Select(p =>
        {
            string phase = Text(p["status"]?["phase"]);
            bool ready = p["status"]?["conditions"]?.AsArray().Any(c => Text(c?["type"]) == "Ready" && Text(c?["status"]) == "True") == true;
            if (p["metadata"]?["deletionTimestamp"] is not null) { phase = "Stopping"; ready = false; }
            JsonNode? usage = summary["pods"]?.AsArray().FirstOrDefault(s => Text(s?["podRef"]?["uid"]) == Text(p["metadata"]?["uid"]));
            int restarts = (p["status"]?["containerStatuses"]?.AsArray() ?? []).Sum(c => (int)(Number(c?["restartCount"]) ?? 0));
            return new ServiceHealth(Text(p["metadata"]?["name"]), Text(p["metadata"]?["namespace"]), phase, ready, restarts,
                Number(usage?["cpu"]?["usageNanoCores"]) / 1_000_000_000, Number(usage?["memory"]?["workingSetBytes"]));
        }).OrderBy(p => p.Ready).ThenBy(p => p.Namespace).ThenBy(p => p.Name).ToArray();
        if (services is null) warnings.Add("Service health could not be checked.");
        else if (services.Any(p => !p.Ready)) warnings.Add("Some Goblin services or agent sandboxes are not ready.");
        double? uptime = DateTimeOffset.TryParse(Text(stats["startTime"]), CultureInfo.InvariantCulture, out DateTimeOffset start)
            ? Math.Max(0, (now - start).TotalSeconds) : null;
        string kernel = Text(node["status"]?["nodeInfo"]?["kernelVersion"]);
        return new(observed, name, kernel.Contains("microsoft", StringComparison.OrdinalIgnoreCase) ? "WSL" : "Linux VM",
            Text(node["status"]?["nodeInfo"]?["osImage"]), uptime, cpu, memory, disk, warnings.Distinct().ToArray(), services);
    }
    private static string Text(JsonNode? value) => value?.GetValue<string>() ?? "";
    private static double? Number(JsonNode? value) => value is not null && value.AsValue().TryGetValue<double>(out double number)
        && double.IsFinite(number) && number >= 0 ? number : null;
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
