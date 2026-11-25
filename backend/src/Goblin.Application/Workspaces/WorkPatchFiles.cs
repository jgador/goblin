using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Wolverine.Attributes;

namespace Goblin.Application.Workspaces;

// Work IDs and checkpoint IDs come from PostgreSQL. Repository content never
// supplies paths in the controller's private review store.
public sealed class WorkPatchFiles
{
    private readonly string _directory;

    public WorkPatchFiles(string directory) => _directory = Path.GetFullPath(directory);

    private string WorkDirectory(long workId) => Path.Combine(_directory, workId.ToString(CultureInfo.InvariantCulture));
    private static string Name(long checkpointId) => checkpointId.ToString(CultureInfo.InvariantCulture) + ".patch";

    internal async Task SaveAsync(long workId, long checkpointId, byte[] patch, CancellationToken token)
    {
        string directory = WorkDirectory(workId);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else
        {
            const UnixFileMode privateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(_directory, privateDirectory);
            Directory.CreateDirectory(directory, privateDirectory);
        }
        string file = Path.Combine(directory, Name(checkpointId));
        string temporary = file + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, patch, token);
            File.Move(temporary, file);
        }
        finally { File.Delete(temporary); }
    }

    internal async Task<byte[]?> ReadAsync(long workId, long checkpointId, CancellationToken token)
    {
        try { return await File.ReadAllBytesAsync(Path.Combine(WorkDirectory(workId), Name(checkpointId)), token); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    internal void Prune(long workId, long? keep)
    {
        string directory = WorkDirectory(workId);
        if (!Directory.Exists(directory)) return;
        if (keep is null)
        {
            Directory.Delete(directory, recursive: true);
            return;
        }
        string name = Name(keep.Value);
        foreach (string file in Directory.EnumerateFiles(directory))
            if (Path.GetFileName(file) != name) File.Delete(file);
    }
}

public sealed record ReconcileWorkPatches(long WorkId);

public static class ReconcileWorkPatchesHandler
{
    [ScheduleRetry(typeof(Exception), 1, 5, 30)]
    public static Task Handle(ReconcileWorkPatches message, WorkspaceCheckpoints store, CancellationToken token) =>
        store.PrunePatchesAsync(message.WorkId, token);
}
