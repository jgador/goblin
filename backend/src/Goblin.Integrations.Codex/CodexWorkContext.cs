using System.Text.Json.Serialization;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;

namespace Goblin.Integrations.Codex;

internal sealed class CodexWorkContext
{
    public CodexWorkContext(string objective, WorkMessage[] messages, WorkDecision[] decisions,
        WorkResult[] results, WorkArtifact[] artifacts)
    {
        Objective = objective;
        Messages = messages;
        Decisions = decisions;
        Results = results;
        Artifacts = artifacts;
    }

    [JsonPropertyName("Objective")]
    public string Objective { get; init; }

    [JsonPropertyName("Messages")]
    public WorkMessage[] Messages { get; init; }

    [JsonPropertyName("Decisions")]
    public WorkDecision[] Decisions { get; init; }

    [JsonPropertyName("Results")]
    public WorkResult[] Results { get; init; }

    [JsonPropertyName("Artifacts")]
    public WorkArtifact[] Artifacts { get; init; }

    [JsonPropertyName("RepositorySetupMemory")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RepositorySetupMemory[]? RepositorySetupMemory { get; init; }
}
