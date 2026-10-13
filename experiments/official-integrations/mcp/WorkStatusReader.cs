using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Goblin.Core.Work;

namespace Goblin.Mcp.Prototype;

public sealed record WorkStatusResult(string WorkId, string Version, string Status, DateTimeOffset UpdatedAt);

public sealed class WorkStatusReader
{
    private readonly HttpClient _http;
    private readonly PrototypeOptions _options;

    public WorkStatusReader(HttpClient http, PrototypeOptions options)
    {
        _http = http;
        _options = options;
    }

    public async Task<WorkStatusResult> ReadAsync(long workId, CancellationToken token)
    {
        // Authorize before fetching anything, including existence checks.
        if (!_options.CanRead(workId)) throw new WorkReadFailure("Work read access is not granted.");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri(_options.Origin, "api/work/" + workId.ToString(CultureInfo.InvariantCulture)));
        request.Headers.Add("Cookie", "goblin_auth_session=" + _options.Session);
        try
        {
            using HttpResponseMessage response = await _http.SendAsync(request, token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new WorkReadFailure("Local Goblin authorization is unavailable or expired.");
            if (!response.IsSuccessStatusCode) throw new WorkReadFailure("Work status is unavailable.");
            StatusEnvelope? value = await response.Content.ReadFromJsonAsync<StatusEnvelope>(JsonOptions, token);
            if (value?.Work is null || value.Work.Id != workId || !Enum.IsDefined(value.Work.Status) ||
                value.Version < 0 || value.UpdatedAt == default)
                throw new WorkReadFailure("Goblin returned an invalid Work status.");
            // Never serialize the upstream Work history, objective, messages or artifacts.
            return new(workId.ToString(CultureInfo.InvariantCulture), value.Version.ToString(CultureInfo.InvariantCulture),
                value.Work.Status.ToString(), value.UpdatedAt);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or NotSupportedException)
        {
            throw new WorkReadFailure("Work status could not be read.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new WorkReadFailure("Work status request timed out.");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter<WorkStatus>(allowIntegerValues: false) }
    };

    private sealed class StatusEnvelope
    {
        public required long Version { get; init; }
        public required DateTimeOffset UpdatedAt { get; init; }
        public required StatusSnapshot? Work { get; init; }
    }

    private sealed class StatusSnapshot
    {
        public required long Id { get; init; }
        public required WorkStatus Status { get; init; }
    }
}

public sealed class WorkReadFailure : Exception
{
    public WorkReadFailure(string message) : base(message) { }
}
