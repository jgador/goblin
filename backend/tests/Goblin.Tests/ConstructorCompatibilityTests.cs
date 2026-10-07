using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Goblin.Application.Work;
using Goblin.Contracts;
using Xunit;
using Api = Goblin.Web.Http.Contracts;

namespace Goblin.Tests;

public sealed class ConstructorCompatibilityTests
{
    // Captured at 9de7d83 before changing constructors, with distinct values for
    // each property. Exact bytes protect command fingerprints as well as JSON shape.
    public static IEnumerable<object[]> Cases()
    {
        using Stream stream = typeof(ConstructorCompatibilityTests).Assembly
            .GetManifestResourceStream("Goblin.Tests.Contracts.constructor-compatibility.json")!;
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
    public void ConstructionChangesPreserveSerializedBytes(string name, bool web, string scenario, string input, string expected)
    {
        string currentName = name.Replace("Repository", "GitRepository", StringComparison.Ordinal);
        Type type = Type.GetType(currentName + ", " + string.Join('.', name.Split('.').Take(2)), throwOnError: true)!;
        JsonSerializerOptions options = web ? new JsonSerializerOptions(ContractJson.Options) : GitRepositoryJson.CreateOptions();
        object value = JsonSerializer.Deserialize(input, type, options)!;
        string actual = JsonSerializer.Serialize(value, type, options);
        Assert.True(string.Equals(expected, actual, StringComparison.Ordinal),
            $"{name} ({scenario}, Web={web}) changed serialized bytes.\nExpected: {expected}\nActual: {actual}");
        if (web && value is Api.WorkCommand command)
            Assert.Equal(expected, JsonSerializer.Serialize(command.ToApplication(), ContractJson.Options));
    }
}
