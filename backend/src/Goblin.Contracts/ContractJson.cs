using System.Text.Json;

namespace Goblin.Contracts;

// One JSON contract is shared by public requests, durable application state,
// command fingerprints, and integration hand-offs. Its ownership belongs with
// those contracts rather than with any store that happens to serialize them.
public static class ContractJson
{
    public static JsonSerializerOptions Options { get; } =
        GitRepositoryJson.CreateOptions(JsonSerializerDefaults.Web);
}
