using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace LocalCpuEmbeddings;

internal sealed record ResourceSample(double Seconds, long RssBytes, long GcHeapBytes, int Threads, double ProcessCpuPercent, double CapacityCpuPercent, long AvailableRamBytes, double CpuPsiPercent, double MemoryPsiPercent);
internal static class LinuxInfo
{
    private const string Cgroup = "/sys/fs/cgroup/";
    public static readonly double CpuCapacity = ReadCapacity();
    public static Dictionary<string, long> MemInfo() => File.ReadLines("/proc/meminfo").ToDictionary(l => l.Split(':')[0], l => long.Parse(l.Split(':')[1].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0]) * 1024);
    public static long MemoryLimit => long.TryParse(Read("memory.max"), out long n) ? n : MemInfo()["MemTotal"];
    public static long MemoryCurrent => long.TryParse(Read("memory.current"), out long n) ? n : 0;
    public static long InactiveFileBytes => long.Parse(Read("memory.stat").Split('\n').First(l => l.StartsWith("inactive_file ")).Split(' ')[1]);
    public static long AvailableRam => Math.Max(0, Math.Min(MemInfo()["MemAvailable"], MemoryLimit - MemoryCurrent + InactiveFileBytes));
    public static long UsageUsec => long.Parse(Read("cpu.stat").Split('\n').First(l => l.StartsWith("usage_usec ")).Split(' ')[1]);
    public static string? PsiPath(string resource) => File.Exists("/proc/pressure/" + resource) ? "/proc/pressure/" + resource : File.Exists(Cgroup + resource + ".pressure") ? Cgroup + resource + ".pressure" : null;
    public static long PsiTotal(string resource) => PsiPath(resource) is string path ? long.Parse(File.ReadLines(path).First(l => l.StartsWith("some ")).Split(' ').First(s => s.StartsWith("total=")).Split('=')[1]) : 0;
    public static string Read(string name) => File.ReadAllText(Cgroup + name).Trim();
    private static double ReadCapacity()
    {
        var parts = Read("cpu.max").Split(' ');
        return parts[0] == "max" ? Environment.ProcessorCount : Math.Min(Environment.ProcessorCount, double.Parse(parts[0], CultureInfo.InvariantCulture) / double.Parse(parts[1], CultureInfo.InvariantCulture));
    }
    public static object EnvironmentReport()
    {
        var memory = MemInfo(); var drive = new DriveInfo(Directory.GetCurrentDirectory());
        return new { Timestamp = DateTimeOffset.UtcNow, OS = RuntimeInformation.OSDescription, OSRelease = File.ReadAllText("/etc/os-release"), LogicalCpus = Environment.ProcessorCount, CpuCapacity, HostTotalRamBytes = memory["MemTotal"], HostAvailableRamBytes = memory["MemAvailable"], CgroupMemoryLimitBytes = MemoryLimit, CgroupMemoryCurrentBytes = MemoryCurrent, CgroupInactiveFileBytes = InactiveFileBytes, EffectiveAvailableRamBytes = AvailableRam, DiskTotalBytes = drive.TotalSize, DiskAvailableBytes = drive.AvailableFreeSpace, Runtime = RuntimeInformation.FrameworkDescription, Sdk = Shell("dotnet", "--version"), CpuModel = File.ReadLines("/proc/cpuinfo").FirstOrDefault(x => x.StartsWith("model name")), CpuMax = Read("cpu.max"), CpuPsi = PsiPath("cpu") is string cp ? File.ReadAllText(cp) : null, MemoryPsi = PsiPath("memory") is string mp ? File.ReadAllText(mp) : null, PsiAvailable = PsiPath("cpu") is not null, AffinityCpuList = File.ReadLines("/proc/self/status").First(l => l.StartsWith("Cpus_allowed_list:")) };
    }
    private static string Shell(string file, string args) { using var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true }); return p!.StandardOutput.ReadToEnd().Trim(); }
}
internal sealed class ResourceSampler : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _task;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly List<ResourceSample> _samples = [];
    public ResourceSampler()
    {
        _task = Task.Run(async () =>
        {
            var previous = Read();
            while (!_stop.IsCancellationRequested)
            {
                try { await Task.Delay(50, _stop.Token); } catch (OperationCanceledException) { break; }
                var now = Read(); var dt = now.Time - previous.Time;
                _process.Refresh();
                var sample = new ResourceSample(now.Time, _process.WorkingSet64, GC.GetTotalMemory(false), _process.Threads.Count, (now.ProcessCpu - previous.ProcessCpu) / dt * 100, (now.Usage - previous.Usage) / 1e6 / dt / LinuxInfo.CpuCapacity * 100, LinuxInfo.AvailableRam, (now.CpuPsi - previous.CpuPsi) / 1e6 / dt * 100, (now.MemPsi - previous.MemPsi) / 1e6 / dt * 100);
                lock (_samples) _samples.Add(sample);
                previous = now;
            }
        });
    }
    private (double Time, double ProcessCpu, long Usage, long CpuPsi, long MemPsi) Read() => (_clock.Elapsed.TotalSeconds, _process.TotalProcessorTime.TotalSeconds, LinuxInfo.UsageUsec, LinuxInfo.PsiTotal("cpu"), LinuxInfo.PsiTotal("memory"));
    public ResourceSample[] Samples { get { lock (_samples) return _samples.ToArray(); } }
    public double Elapsed => _clock.Elapsed.TotalSeconds;
    public object Summary(double since = 0)
    {
        var s = Samples.Where(x => x.Seconds >= since).ToArray();
        if (s.Length == 0) return new { SampleCount = 0 };
        return new { SampleCount = s.Length, PeakRssBytes = s.Max(x => x.RssBytes), MedianRssBytes = s.OrderBy(x => x.RssBytes).ElementAt(s.Length / 2).RssBytes, AverageProcessCpuPercent = s.Average(x => x.ProcessCpuPercent), PeakProcessCpuPercent = s.Max(x => x.ProcessCpuPercent), AverageCapacityCpuPercent = s.Average(x => x.CapacityCpuPercent), PeakCapacityCpuPercent = s.Max(x => x.CapacityCpuPercent), PeakGcHeapBytes = s.Max(x => x.GcHeapBytes), PeakThreads = s.Max(x => x.Threads), MinimumAvailableRamBytes = s.Min(x => x.AvailableRamBytes) };
    }
    public void WriteCsv(string path)
    {
        using var w = new StreamWriter(path); w.WriteLine("seconds,rss_bytes,gc_heap_bytes,threads,process_cpu_percent,capacity_cpu_percent,available_ram_bytes,cpu_psi_percent,memory_psi_percent");
        foreach (var s in Samples) w.WriteLine(FormattableString.Invariant($"{s.Seconds:F6},{s.RssBytes},{s.GcHeapBytes},{s.Threads},{s.ProcessCpuPercent:F4},{s.CapacityCpuPercent:F4},{s.AvailableRamBytes},{s.CpuPsiPercent:F4},{s.MemoryPsiPercent:F4}"));
    }
    public void Dispose() { _stop.Cancel(); _task.GetAwaiter().GetResult(); _stop.Dispose(); _process.Dispose(); }
}
internal sealed class CapacityGate
{
    private long _usage = LinuxInfo.UsageUsec, _cpuPsi = LinuxInfo.PsiTotal("cpu"), _memPsi = LinuxInfo.PsiTotal("memory");
    private long _time = Stopwatch.GetTimestamp();
    public (bool HasCapacity, double CpuPercent, long AvailableRam, double CpuPsi, double MemoryPsi) Check()
    {
        long time = Stopwatch.GetTimestamp(), usage = LinuxInfo.UsageUsec, cpuPsi = LinuxInfo.PsiTotal("cpu"), memPsi = LinuxInfo.PsiTotal("memory");
        double dt = (time - _time) / (double)Stopwatch.Frequency;
        double cpu = (usage - _usage) / 1e6 / dt / LinuxInfo.CpuCapacity * 100, cp = (cpuPsi - _cpuPsi) / 1e6 / dt * 100, mp = (memPsi - _memPsi) / 1e6 / dt * 100;
        long ram = LinuxInfo.AvailableRam;
        _time = time; _usage = usage; _cpuPsi = cpuPsi; _memPsi = memPsi;
        // /proc PSI is host-wide; cgroup PSI is the fallback. Missing PSI does not block processing.
        // Gate primarily uses our actual cgroup capacity and memory working-set headroom.
        return (cpu < 80 && ram > 512L * 1024 * 1024 && mp < 10, cpu, ram, cp, mp);
    }
}
