using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Goblin.Web.Monitoring;

public sealed class ResourceUsage
{
    [JsonConstructor]
    public ResourceUsage(double total, double? used, double? available)
    {
        Total = total;
        Used = used;
        Available = available;
    }

    [JsonPropertyName("total")]
    public double Total { get; init; }

    [JsonPropertyName("used")]
    public double? Used { get; init; }

    [JsonPropertyName("available")]
    public double? Available { get; init; }

    [JsonPropertyName("percent")]
    public double? Percent => Total > 0 && Used is { } used ? Math.Clamp(used / Total * 100, 0, 100) : null;
}

public sealed class ServiceHealth
{
    [JsonConstructor]
    public ServiceHealth(string name, string @namespace, string phase, bool ready, int restarts, double? cpuCores,
        double? memoryBytes)
    {
        Name = name;
        Namespace = @namespace;
        Phase = phase;
        Ready = ready;
        Restarts = restarts;
        CpuCores = cpuCores;
        MemoryBytes = memoryBytes;
    }

    [JsonPropertyName("name")]
    public string Name { get; init; }

    [JsonPropertyName("namespace")]
    public string Namespace { get; init; }

    [JsonPropertyName("phase")]
    public string Phase { get; init; }

    [JsonPropertyName("ready")]
    public bool Ready { get; init; }

    [JsonPropertyName("restarts")]
    public int Restarts { get; init; }

    [JsonPropertyName("cpuCores")]
    public double? CpuCores { get; init; }

    [JsonPropertyName("memoryBytes")]
    public double? MemoryBytes { get; init; }
}

public sealed class MachineSnapshot
{
    [JsonConstructor]
    public MachineSnapshot(DateTimeOffset observedAt, string name, string environment, string operatingSystem,
        double? uptimeSeconds, ResourceUsage cpu, ResourceUsage memory, ResourceUsage disk,
        IReadOnlyList<string> warnings, IReadOnlyList<ServiceHealth>? services)
    {
        ObservedAt = observedAt;
        Name = name;
        Environment = environment;
        OperatingSystem = operatingSystem;
        UptimeSeconds = uptimeSeconds;
        Cpu = cpu;
        Memory = memory;
        Disk = disk;
        Warnings = warnings;
        Services = services;
    }

    [JsonPropertyName("observedAt")]
    public DateTimeOffset ObservedAt { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; }

    [JsonPropertyName("environment")]
    public string Environment { get; init; }

    [JsonPropertyName("operatingSystem")]
    public string OperatingSystem { get; init; }

    [JsonPropertyName("uptimeSeconds")]
    public double? UptimeSeconds { get; init; }

    [JsonPropertyName("cpu")]
    public ResourceUsage Cpu { get; init; }

    [JsonPropertyName("memory")]
    public ResourceUsage Memory { get; init; }

    [JsonPropertyName("disk")]
    public ResourceUsage Disk { get; init; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; init; }

    [JsonPropertyName("services")]
    public IReadOnlyList<ServiceHealth>? Services { get; init; }
}

public sealed class SystemSample
{
    [JsonConstructor]
    public SystemSample(DateTimeOffset at, double? cpu, double? memory)
    {
        At = at;
        Cpu = cpu;
        Memory = memory;
    }

    [JsonPropertyName("at")]
    public DateTimeOffset At { get; init; }

    [JsonPropertyName("cpu")]
    public double? Cpu { get; init; }

    [JsonPropertyName("memory")]
    public double? Memory { get; init; }
}

public sealed class SystemOverview
{
    [JsonConstructor]
    public SystemOverview(string status, string? notice, MachineSnapshot? machine, IReadOnlyList<SystemSample> history)
    {
        Status = status;
        Notice = notice;
        Machine = machine;
        History = history;
    }

    [JsonPropertyName("status")]
    public string Status { get; init; }

    [JsonPropertyName("notice")]
    public string? Notice { get; init; }

    [JsonPropertyName("machine")]
    public MachineSnapshot? Machine { get; init; }

    [JsonPropertyName("history")]
    public IReadOnlyList<SystemSample> History { get; init; }
}

public interface ISystemSource
{
    Task<MachineSnapshot> ReadAsync(CancellationToken token);
}

// Operational observations only: never writes Work state or changes execution policy.
// One bounded collector serves all browsers. History is deliberately in memory.
public sealed class SystemMonitor : BackgroundService
{
    private readonly ISystemSource? _source;
    private SystemOverview _current;
    public SystemMonitor(ISystemSource? source)
    {
        _source = source;
        _current = source is null
            ? new("unsupported", "System monitoring is available with Goblin’s Kubernetes installation.", null, [])
            : new("loading", "Reading your machine’s resources…", null, []);
    }
    public SystemOverview Current
    {
        get
        {
            SystemOverview current = Volatile.Read(ref _current);
            return current.Status == "live" && current.Machine?.ObservedAt < DateTimeOffset.UtcNow.AddSeconds(-60)
                ? new SystemOverview("stale", "Updates are interrupted. These are the last reported readings.", current.Machine, current.History)
                : current;
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_source is null) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do { await CollectAsync(stoppingToken); }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
    public async Task CollectAsync(CancellationToken token)
    {
        if (_source is null) return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            MachineSnapshot snapshot = await _source.ReadAsync(timeout.Token);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (snapshot.ObservedAt < now.AddSeconds(-60) || snapshot.ObservedAt > now.AddSeconds(30))
                throw new InvalidOperationException("Outdated resource sample.");
            SystemSample[] history = [.. Current.History
                .Where(sample => sample.At >= now.AddMinutes(-5) && sample.At < snapshot.ObservedAt)
                .Append(new SystemSample(snapshot.ObservedAt, snapshot.Cpu.Percent, snapshot.Memory.Percent))
                .TakeLast(21)];
            Volatile.Write(ref _current, new("live", null, snapshot, history));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch
        {
            SystemOverview previous = Current;
            Volatile.Write(ref _current, new SystemOverview(previous.Machine is null ? "unavailable" : "stale",
                previous.Machine is null
                    ? "System metrics are temporarily unavailable. Goblin will check again shortly."
                    : "Updates are interrupted. These are the last reported readings.", previous.Machine, previous.History));
        }
    }
}
