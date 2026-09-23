using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Goblin.Execution.Kubernetes;

public static class KubernetesJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        options.MakeReadOnly();
        return options;
    }
}

public sealed class KubernetesStringEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    private static readonly Dictionary<T, string> Names = Enum.GetValues<T>().ToDictionary(
        value => value,
        value => typeof(T).GetField(value.ToString())!
            .GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name
            ?? throw new InvalidOperationException($"Missing wire name on {typeof(T).Name}.{value}."));
    private static readonly Dictionary<string, T> Values = Names.ToDictionary(entry => entry.Value, entry => entry.Key, StringComparer.Ordinal);

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String && Values.TryGetValue(reader.GetString()!, out T value)
            ? value : throw new JsonException($"Invalid {typeof(T).Name} wire value.");

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (!Names.TryGetValue(value, out string? name)) throw new JsonException($"Invalid {typeof(T).Name} enum value.");
        writer.WriteStringValue(name);
    }
}

[JsonConverter(typeof(KubernetesIntOrStringConverter))]
public sealed class KubernetesIntOrString
{
    public string? Text { get; }
    public long? Number { get; }

    public KubernetesIntOrString(string text) => Text = text ?? throw new ArgumentNullException(nameof(text));
    public KubernetesIntOrString(long number) => Number = number;

    public static implicit operator KubernetesIntOrString(string text) => new(text);
    public static implicit operator KubernetesIntOrString(long number) => new(number);
}

public sealed class KubernetesIntOrStringConverter : JsonConverter<KubernetesIntOrString>
{
    public override KubernetesIntOrString Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => new(reader.GetString()!),
            JsonTokenType.Number when reader.TryGetInt64(out long number) => new(number),
            _ => throw new JsonException("Expected a Kubernetes string or integer value.")
        };

    public override void Write(Utf8JsonWriter writer, KubernetesIntOrString value, JsonSerializerOptions options)
    {
        if (value.Text is { } text) writer.WriteStringValue(text);
        else if (value.Number is { } number) writer.WriteNumberValue(number);
        else throw new JsonException("An empty Kubernetes string or integer value cannot be serialized.");
    }
}
