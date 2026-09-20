using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Goblin.Execution;

public sealed record ProcessIdentity(int Pid, long StartedAt, string Machine)
{
    public string? BootId { get; init; }
    public string? PidNamespace { get; init; }
    public ulong? StartTicks { get; init; }

    public static ProcessIdentity Capture(Process process)
    {
        var identity = new ProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks, Environment.MachineName);
        return OperatingSystem.IsLinux() ? identity with
        {
            BootId = ReadBootId(),
            PidNamespace = ReadPidNamespace(),
            StartTicks = ReadStartTicks(process.Id)
        } : identity;
    }

    public bool CanObserve()
    {
        if (Machine != Environment.MachineName) return false;
        // Old Linux journals contain only a wall-clock conversion. It cannot
        // prove identity, including after a controller restart or PID reuse.
        if (OperatingSystem.IsLinux())
            return StartTicks is not null && BootId == ReadBootId() && PidNamespace == ReadPidNamespace();
        return BootId is null && PidNamespace is null && StartTicks is null;
    }

    public bool Matches(Process process) => process.Id == Pid && (OperatingSystem.IsLinux()
        ? StartTicks == ReadStartTicks(process.Id)
        : StartedAt == process.StartTime.ToUniversalTime().Ticks);

    private static string ReadBootId() => File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
    private static string ReadPidNamespace() => new FileInfo("/proc/self/ns/pid").LinkTarget
        ?? throw new IOException("The process namespace identity is unavailable.");

    private static ulong ReadStartTicks(int pid)
    {
        // Field 22 is the kernel start counter, identical in every observer.
        // The parenthesized command in field 2 may itself contain spaces or ')'.
        string stat = File.ReadAllText("/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/stat");
        int commandEnd = stat.LastIndexOf(')');
        if (commandEnd < 0) throw new IOException("The process identity is unavailable.");
        string[] fields = stat[(commandEnd + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length <= 19 || !ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out ulong start))
            throw new IOException("The process identity is unavailable.");
        return start;
    }
}
