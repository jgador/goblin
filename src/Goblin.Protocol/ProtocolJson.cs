using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Goblin.Protocol;

/// <summary>Serialization settings shared by the generated Codex protocol models.</summary>
public static class ProtocolJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

/// <summary>A schema value whose only permitted JSON representation is null.</summary>
[JsonConverter(typeof(ProtocolNullJsonConverter))]
public readonly record struct ProtocolNull;

public sealed class ProtocolNullJsonConverter : JsonConverter<ProtocolNull>
{
    public override ProtocolNull Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? default : throw new JsonException("Expected null.");

    public override void Write(Utf8JsonWriter writer, ProtocolNull value, JsonSerializerOptions options)
        => writer.WriteNullValue();
}

/// <summary>Preserves schema enum spelling and rejects numeric enum values.</summary>
public sealed class ProtocolStringEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    private static readonly Dictionary<T, string> Names = Enum.GetValues<T>().ToDictionary(
        value => value,
        value => typeof(T).GetField(value.ToString())!
            .GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name
            ?? throw new InvalidOperationException($"Missing wire name on {typeof(T).Name}.{value}."));
    private static readonly Dictionary<string, T> Values = Names.ToDictionary(
        entry => entry.Value, entry => entry.Key, StringComparer.Ordinal);

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String && Values.TryGetValue(reader.GetString()!, out var value)
            ? value : throw new JsonException($"Invalid {typeof(T).Name} wire value.");

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (!Names.TryGetValue(value, out var name))
            throw new JsonException($"Invalid {typeof(T).Name} enum value.");
        writer.WriteStringValue(name);
    }
}

/// <summary>A typed branch of an untagged schema union, serialized as its underlying value.</summary>
public interface IProtocolValue<TSelf, TValue> where TSelf : IProtocolValue<TSelf, TValue>
{
    TValue Value { get; }
    static abstract TSelf FromValue(TValue value);
}

public sealed class ProtocolValueConverter<TWrapper, TValue> : JsonConverter<TWrapper>
    where TWrapper : IProtocolValue<TWrapper, TValue>
{
    public override TWrapper Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = JsonSerializer.Deserialize<TValue>(ref reader, options);
        if (value is null) throw new JsonException("Expected a non-null union value.");
        return TWrapper.FromValue(value);
    }

    public override void Write(Utf8JsonWriter writer, TWrapper value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value.Value, options);
}

internal static class ProtocolUnion
{
    // Inspect a copy, leaving the original reader positioned for the selected POCO.
    // The discriminator may occur anywhere in the object, including after nested values.
    public static string ReadDiscriminator(ref Utf8JsonReader reader, string propertyName)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected a protocol object.");

        var probe = reader;
        string? discriminator = null;
        while (probe.Read() && probe.TokenType != JsonTokenType.EndObject)
        {
            if (probe.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Expected a protocol property name.");
            var matches = probe.ValueTextEquals(propertyName);
            if (!probe.Read())
                throw new JsonException("Incomplete protocol object.");
            if (matches)
            {
                if (discriminator is not null || probe.TokenType != JsonTokenType.String)
                    throw new JsonException($"Invalid or duplicate {propertyName} discriminator.");
                discriminator = probe.GetString();
            }
            probe.Skip();
        }

        return discriminator ?? throw new JsonException($"Missing {propertyName} discriminator.");
    }
}
