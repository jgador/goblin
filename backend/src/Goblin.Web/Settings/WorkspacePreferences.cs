using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Goblin.Web;

public sealed class WorkspacePreferenceState
{
    [JsonConstructor]
    public WorkspacePreferenceState(string? timeZone)
    {
        TimeZone = timeZone;
    }

    [JsonPropertyName("timeZone")]
    public string? TimeZone { get; init; }
}

// Workspace UI configuration lives on the application's persistent data volume,
// alongside workspace access configuration. It is independent of Work history.
public sealed class WorkspacePreferences
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WorkspacePreferences(string directory) => _path = Path.Combine(directory, "workspace-preferences.json");

    public static string[] TimeZones { get; } = [.. TimeZoneInfo.GetSystemTimeZones()
        .Select(zone => zone.HasIanaId ? zone.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out string? id) ? id : null)
        .OfType<string>().Append("UTC").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    public async Task<WorkspacePreferenceState> ReadAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try { return await ReadFileAsync(token); }
        finally { _gate.Release(); }
    }

    public async Task<WorkspacePreferenceState> SetTimeZoneAsync(string? timeZone, string? expectedTimeZone, CancellationToken token = default)
    {
        if (!ValidTimeZone(timeZone))
            throw new PublicError("invalid_timezone", "Choose a valid timezone from the list.", 400);
        await _gate.WaitAsync(token);
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WorkspacePreferenceState current = await ReadFileAsync(token);
            if (current.TimeZone != expectedTimeZone)
                throw new PublicError("timezone_changed", "The workspace timezone changed in another session. Review it and save again.", 409);
            var next = new WorkspacePreferenceState(timeZone);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, next, Json, token);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
            return next;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new PublicError("preferences_unavailable", "Workspace preferences could not be saved. Try again.", 503);
        }
        finally
        {
            try { File.Delete(temporary); }
            finally { _gate.Release(); }
        }
    }

    private async Task<WorkspacePreferenceState> ReadFileAsync(CancellationToken token)
    {
        try
        {
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            WorkspacePreferenceState? state = await JsonSerializer.DeserializeAsync<WorkspacePreferenceState>(stream, Json, token);
            if (state is null || !ValidTimeZone(state.TimeZone)) throw new JsonException();
            return state;
        }
        catch (FileNotFoundException) { return new(null); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new PublicError("preferences_unavailable", "Workspace preferences could not be loaded. Try again.", 503);
        }
    }

    private static bool ValidTimeZone(string? value) => value is { Length: > 0 and <= 100 } &&
        (value == "UTC" || value.Contains('/')) &&
        TimeZoneInfo.TryFindSystemTimeZoneById(value, out _);
}
