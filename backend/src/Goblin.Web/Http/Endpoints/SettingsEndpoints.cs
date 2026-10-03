using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Web.Monitoring;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Goblin.Web;

internal static class SettingsEndpoints
{
    public static void Map(WebApplication app)
    {
        HeadlampProxy? headlamp = app.Services.GetService<HeadlampProxy>();
        VictoriaLogsProxy? logs = app.Services.GetService<VictoriaLogsProxy>();
        app.MapGet("/api/cluster", () => Results.Json(new { available = headlamp is not null }));
        app.MapGet("/api/logs", () => Results.Json(new { available = logs is not null }));
        app.MapGet("/api/preferences", ReadPreferencesAsync);
        app.MapGet("/api/preferences/timezones", TimeZones);
        app.MapPost("/api/preferences/timezone", SetTimeZoneAsync);
        app.MapGet("/api/preferences/logs.js", LogsPreferencesAsync);
        app.MapGet("/api/system", System);
    }

    private static Task<WorkspacePreferenceState> ReadPreferencesAsync(WorkspacePreferences preferences, CancellationToken token) =>
        preferences.ReadAsync(token);

    private static IResult TimeZones() => Results.Json(WorkspacePreferences.TimeZones);

    private static Task<WorkspacePreferenceState> SetTimeZoneAsync(HttpContext context, WorkspacePreferences preferences, CancellationToken token)
    {
        TimeZoneRequest request = ApiRequest.Body<TimeZoneRequest>(context);
        return preferences.SetTimeZoneAsync(request.TimeZone, request.ExpectedTimeZone, token);
    }

    private static async Task<IResult> LogsPreferencesAsync(WorkspacePreferences preferences, CancellationToken token)
    {
        WorkspacePreferenceState state = await preferences.ReadAsync(token);
        // This blocking, same-origin script runs before the pinned VLUI bundle.
        // VLUI 1.52 stores values as {value: ...} with the VLUI: prefix.
        return Results.Text("try { localStorage.setItem('VLUI:TIMEZONE', " +
            JsonSerializer.Serialize(JsonSerializer.Serialize(new { value = state.TimeZone ?? "UTC" })) +
            "); } catch {}", "text/javascript; charset=utf-8");
    }

    private static IResult System(SystemMonitor monitor) => Results.Json(monitor.Current);
}
