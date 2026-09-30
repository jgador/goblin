using System.Text.Json.Serialization;

namespace Goblin.Web;

public sealed class InspectionRequest
{
    [JsonConstructor]
    public InspectionRequest(long id, long attemptId)
    {
        Id = id;
        AttemptId = attemptId;
    }

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("attemptId")]
    public long AttemptId { get; init; }
}
