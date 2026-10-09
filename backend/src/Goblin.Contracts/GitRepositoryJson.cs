using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Goblin.Core.Work;

namespace Goblin.Contracts;

// C# names describe Git repositories explicitly. Stored and public JSON retain
// their existing names; this translation belongs to adapters, not the Work core.
public static class GitRepositoryJson
{
    public static JsonSerializerOptions CreateOptions(JsonSerializerDefaults defaults = JsonSerializerDefaults.General) =>
        Configure(new JsonSerializerOptions(defaults));

    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = new GitRepositoryPropertyNamingPolicy(options.PropertyNamingPolicy);
        options.Converters.Add(new GitRepositoryEnumJsonConverterFactory());
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        options.TypeInfoResolver = (options.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver()).WithAddedModifier(info =>
        {
            if (info.Type == typeof(GitRepositoryChange))
                foreach (JsonPropertyInfo property in info.Properties)
                    if (property.Name == options.PropertyNamingPolicy.ConvertName(nameof(GitRepositoryChange.RequestedBy)))
                        property.ShouldSerialize = (_, value) => value is not null;
        });
        return options;
    }
}

internal sealed class GitRepositoryPropertyNamingPolicy : JsonNamingPolicy
{
    private readonly JsonNamingPolicy? _policy;

    public GitRepositoryPropertyNamingPolicy(JsonNamingPolicy? policy) => _policy = policy;

    public override string ConvertName(string name)
    {
        string previous = name switch
        {
            "GitRepository" => "Repository",
            "GitRepositories" => "Repositories",
            "GitRepositoryId" => "RepositoryId",
            "GitRepositoryRequest" => "RepositoryRequest",
            "GitRepositoryAuthorization" => "RepositoryAuthorization",
            "EnableGitRepository" => "EnableRepository",
            "GitRepositoryExecution" => "RepositoryExecution",
            "GitRepositorySetupMemory" => "RepositorySetupMemory",
            "GitRepositoryUrl" => "RepositoryUrl",
            _ => name
        };
        return _policy?.ConvertName(previous) ?? previous;
    }
}

internal sealed class GitRepositoryEnumJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum &&
        Enum.GetNames(typeToConvert).Any(name => name.Contains("GitRepository", StringComparison.Ordinal));

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(GitRepositoryEnumJsonConverter<>).MakeGenericType(typeToConvert))!;
}

internal sealed class GitRepositoryEnumJsonConverter<T> : JsonConverter<T> where T : struct, Enum
{
    private static readonly Dictionary<string, T> Values = CreateValues();

    private static Dictionary<string, T> CreateValues()
    {
        var values = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (T value in Enum.GetValues<T>()) values.Add(WireName(value), value);
        return values;
    }

    private static string WireName(T value) => value.ToString().Replace("GitRepository", "Repository", StringComparison.Ordinal);

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException($"Invalid {typeof(T).Name}.");
        // Preserve the string enum converter's case-insensitive, comma-separated
        // name parsing, while continuing to reject numbers and unknown names.
        string[] names = reader.GetString()!.Split(',');
        for (int i = 0; i < names.Length; i++)
        {
            if (!Values.TryGetValue(names[i].Trim(), out T value)) throw new JsonException($"Invalid {typeof(T).Name}.");
            names[i] = value.ToString();
        }
        return Enum.Parse<T>(string.Join(",", names));
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (!Enum.IsDefined(value)) throw new JsonException($"Invalid {typeof(T).Name}.");
        writer.WriteStringValue(WireName(value));
    }
}
