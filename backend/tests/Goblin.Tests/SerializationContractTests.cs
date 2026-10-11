using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Goblin.Application.Work;
using Goblin.Contracts;
using Xunit;
using Api = Goblin.Web.Http.Contracts;

namespace Goblin.Tests;

public sealed class SerializationContractTests
{
    // Distinct property values exercise JSON shape, defaults, and command fingerprints.
    public static IEnumerable<object[]> Cases()
    {
        using Stream stream = typeof(SerializationContractTests).Assembly
            .GetManifestResourceStream("Goblin.Tests.Contracts.serialization-contracts.json")!;
        using JsonDocument document = JsonDocument.Parse(stream);
        foreach (JsonElement entry in document.RootElement.EnumerateArray())
            yield return new object[]
            {
                entry.GetProperty("Type").GetString()!, entry.GetProperty("Web").GetBoolean(),
                entry.GetProperty("Scenario").GetString()!, entry.GetProperty("Input").GetString()!,
                entry.GetProperty("Expected").GetString()!
            };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ContractsHaveExpectedSerializedBytes(string name, bool web, string scenario, string input, string expected)
    {
        Type type = Type.GetType(name + ", " + string.Join('.', name.Split('.').Take(2)), throwOnError: true)!;
        JsonSerializerOptions options = web ? new JsonSerializerOptions(ContractJson.Options) : GitRepositoryJson.CreateOptions();
        object value = JsonSerializer.Deserialize(input, type, options)!;
        string actual = JsonSerializer.Serialize(value, type, options);
        Assert.True(string.Equals(expected, actual, StringComparison.Ordinal),
            $"{name} ({scenario}, Web={web}) changed serialized bytes.\nExpected: {expected}\nActual: {actual}");
        if (web && value is Api.WorkCommand command)
        {
            // Repository selections cannot submit executable grants or requester identity.
            JsonNode mapped = JsonNode.Parse(expected)!;
            if (mapped["repository"] is JsonObject repository)
            {
                repository.Remove("grant");
                repository.Remove("requestedBy");
            }
            Assert.Equal(mapped.ToJsonString(), JsonSerializer.Serialize(command.ToApplication(), ContractJson.Options));
        }
    }
}
