using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Goblin.Web.Monitoring;

public sealed record ResourceUsage(double Total, double? Used, double? Available)
{
    public double? Percent => Total > 0 && Used is { } used ? Math.Clamp(used / Total * 100, 0, 100) : null;
}
public sealed record ServiceHealth(string Name, string Namespace, string Phase, bool Ready, int Restarts,
    double? CpuCores, double? MemoryBytes);
public sealed record MachineSnapshot(DateTimeOffset ObservedAt, string Name, string Environment, string OperatingSystem,
    double? UptimeSeconds, ResourceUsage Cpu, ResourceUsage Memory, ResourceUsage Disk,
    IReadOnlyList<string> Warnings, IReadOnlyList<ServiceHealth>? Services);
public sealed record SystemSample(DateTimeOffset At, double? Cpu, double? Memory);
public sealed record SystemOverview(string Status, string? Notice, MachineSnapshot? Machine, IReadOnlyList<SystemSample> History);

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
                ? current with { Status = "stale", Notice = "Updates are interrupted. These are the last reported readings." }
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
            Volatile.Write(ref _current, previous with
            {
                Status = previous.Machine is null ? "unavailable" : "stale",
                Notice = previous.Machine is null
                    ? "System metrics are temporarily unavailable. Goblin will check again shortly."
                    : "Updates are interrupted. These are the last reported readings."
            });
        }
    }
}
