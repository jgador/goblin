using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Goblin.Application.GitRepositories;

// Owns the filesystem evidence used to recover repository operations after the
// controller or its database connection fails. This evidence is never replayed.
internal sealed class GitRepositoryOperationEvidence
{
    private readonly string _directory;

    internal GitRepositoryOperationEvidence(string directory) => _directory = directory;

    internal Task RecordDispatchFailureAsync(long id) =>
        File.WriteAllTextAsync(PathFor(id, "failed"), "failed", CancellationToken.None);

    internal bool HasDispatchFailure(long id) => File.Exists(PathFor(id, "failed"));

    internal void ClearDispatchFailure(long id) => File.Delete(PathFor(id, "failed"));

    internal Task RecordExternalLaunchAsync(long id, CancellationToken token) =>
        File.WriteAllLinesAsync(PathFor(id, "external"),
            [ReadBootId(), ReadUptime()], token);

    internal bool ExternalProcessStopped(long id)
    {
        string marker = PathFor(id, "external");
        if (!File.Exists(marker)) return true; // The external launch was never authorized.
        if (!OperatingSystem.IsLinux()) return false;
        string[] lifetime = File.ReadAllLines(marker);
        if (lifetime.Length != 2) return false;
        if (lifetime[0] != ReadBootId()) return true;
        return double.TryParse(lifetime[1], CultureInfo.InvariantCulture, out double started) &&
            double.TryParse(ReadUptime(), CultureInfo.InvariantCulture, out double now) && now - started > 300;
    }

    private string PathFor(long id, string suffix) =>
        Path.Combine(_directory, id.ToString(CultureInfo.InvariantCulture) + "." + suffix);

    private static string ReadBootId() => File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();

    private static string ReadUptime() => File.ReadAllText("/proc/uptime").Split(' ')[0];
}
