using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Goblin.Web;

// JavaScript numbers cannot exactly represent the full PostgreSQL bigint range.
public sealed class LongJsonConverter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out long number)) return number;
        if (reader.TokenType == JsonTokenType.String && long.TryParse(reader.GetString(),
            NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value)) return value;
        throw new JsonException("Expected a 64-bit integer.");
    }

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
}
