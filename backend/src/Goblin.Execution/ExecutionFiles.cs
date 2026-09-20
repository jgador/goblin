using System;
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

public sealed class FileDispatchFailureJournal(string directory) : IDispatchFailureJournal
{
    public async Task RecordAsync(DispatchFailureEvidence evidence, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        await ExecutionFiles.WriteAsync(Path.Combine(directory, evidence.AttemptId.ToString("N") + ".json"), evidence, token);
    }
    public async Task<DispatchFailureEvidence[]> ReadAsync(CancellationToken token)
    {
        if (!Directory.Exists(directory)) return [];
        var entries = new System.Collections.Generic.List<DispatchFailureEvidence>();
        foreach (string file in Directory.GetFiles(directory, "*.json"))
            if (await ExecutionFiles.ReadAsync<DispatchFailureEvidence>(file, token) is { } evidence) entries.Add(evidence);
        return [.. entries];
    }
    public Task RemoveAsync(Guid attemptId, CancellationToken token)
    {
        File.Delete(Path.Combine(directory, attemptId.ToString("N") + ".json"));
        return Task.CompletedTask;
    }
}
