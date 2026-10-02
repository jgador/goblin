using System.Text.Json.Serialization;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;

namespace Goblin.Integrations.Codex;

internal sealed record CodexWorkContext(
    [property: JsonPropertyName("Objective")] string Objective,
    [property: JsonPropertyName("Messages")] WorkMessage[] Messages,
    [property: JsonPropertyName("Decisions")] WorkDecision[] Decisions,
    [property: JsonPropertyName("Results")] WorkResult[] Results,
    [property: JsonPropertyName("Artifacts")] WorkArtifact[] Artifacts)
{
    [JsonPropertyName("RepositorySetupMemory")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RepositorySetupMemory[]? RepositorySetupMemory { get; init; }
}
