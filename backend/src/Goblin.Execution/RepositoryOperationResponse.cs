using System.Text.Json.Serialization;
using Goblin.Contracts;
using Goblin.Core.Work;

namespace Goblin.Execution;

public sealed class RepositoryOperationResponse
{
    [JsonIgnore]
    public RepositoryOperationState Status => ContractValue.Parse<RepositoryOperationState>(State);

    [JsonPropertyName("state")]
    [JsonRequired]
    public string State { get; init; } = null!;

    [JsonPropertyName("url")]
    [JsonRequired]
    public string? Url { get; init; }
}
