using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts.Runtime;

namespace Goblin.Execution;

public static class ExecutionFiles
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    public static async Task WriteAsync<T>(string path, T value, CancellationToken token = default)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N");
        await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough))
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await JsonSerializer.SerializeAsync(file, value, Json, token);
            await file.FlushAsync(token);
            file.Flush(true);
        }
        File.Move(temporary, path, overwrite: true);
    }
    public static async Task<T?> ReadAsync<T>(string path, CancellationToken token = default)
    {
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return await JsonSerializer.DeserializeAsync<T>(file, Json, token);
        }
        catch (FileNotFoundException) { return default; }
        catch (DirectoryNotFoundException) { return default; }
    }
}

public sealed class FileDispatchFailureJournal : IDispatchFailureJournal
{
    private readonly string _directory;

    public FileDispatchFailureJournal(string directory) => _directory = directory;

    public async Task RecordAsync(DispatchFailureEvidence evidence, CancellationToken token)
    {
        Directory.CreateDirectory(_directory);
        await ExecutionFiles.WriteAsync(Path.Combine(_directory, evidence.AttemptId.ToString(CultureInfo.InvariantCulture) + ".json"), evidence, token);
    }
    public async Task<DispatchFailureEvidence[]> ReadAsync(CancellationToken token)
    {
        if (!Directory.Exists(_directory)) return [];
        var entries = new System.Collections.Generic.List<DispatchFailureEvidence>();
        foreach (string file in Directory.GetFiles(_directory, "*.json"))
            if (await ExecutionFiles.ReadAsync<DispatchFailureEvidence>(file, token) is { } evidence) entries.Add(evidence);
        return [.. entries];
    }
    public Task RemoveAsync(long attemptId, CancellationToken token)
    {
        File.Delete(Path.Combine(_directory, attemptId.ToString(CultureInfo.InvariantCulture) + ".json"));
        return Task.CompletedTask;
    }
}
