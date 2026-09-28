using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace FpsUtility.Core.Raw.Serialization;

/// <summary>
/// Reads and writes every <see cref="Reading{T}"/> as <c>{"value": …, "native": …}</c> or
/// <c>{"native": …, "missing": "…", "detail": "…"}</c>. When reading, a reading must name its state:
/// an object with neither <c>value</c> nor <c>missing</c> is rejected rather than taken as a known
/// default (a bare <c>{}</c> would otherwise read as a known <c>0</c> or <c>false</c>).
/// </summary>
internal sealed class ReadingJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Reading<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(ReadingJsonConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;
}

internal sealed class ReadingJsonConverter<T> : JsonConverter<Reading<T>>
{
    public override Reading<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("A reading is a JSON object.");
        }

        var hasValue = false;
        T? value = default;
        long? native = null;
        MissingReason? missing = null;
        string? detail = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString()!;
            if (!seen.Add(name))
            {
                throw new JsonException($"A reading has '{name}' twice.");
            }

            reader.Read();
            switch (name)
            {
                case "value":
                    hasValue = true;
                    value = JsonSerializer.Deserialize(ref reader, TypeInfo<T>(options));
                    break;
                case "native":
                    native = reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var code)
                        ? code
                        : throw new JsonException("A reading's 'native' is an integer.");
                    break;
                case "missing":
                    missing = JsonSerializer.Deserialize(ref reader, TypeInfo<MissingReason>(options));
                    break;
                case "detail":
                    detail = reader.TokenType is JsonTokenType.String or JsonTokenType.Null
                        ? reader.GetString()
                        : throw new JsonException("A reading's 'detail' is a string.");
                    break;
                default:
                    throw new JsonException($"A reading has no property '{name}'.");
            }
        }

        if (reader.TokenType != JsonTokenType.EndObject)
        {
            throw new JsonException("A reading object is not closed.");
        }

        if (hasValue == (missing is not null))
        {
            throw new JsonException("A reading has exactly one of 'value' and 'missing'.");
        }

        try
        {
            return new Reading<T>(value, native, missing, detail);
        }
        catch (ArgumentException ex)
        {
            throw new JsonException(ex.Message, ex);
        }
    }

    public override void Write(Utf8JsonWriter writer, Reading<T> reading, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        if (reading.IsKnown)
        {
            writer.WritePropertyName("value");
            JsonSerializer.Serialize(writer, reading.Value!, TypeInfo<T>(options));
        }

        if (reading.Native is { } native)
        {
            writer.WriteNumber("native", native);
        }

        if (!reading.IsKnown)
        {
            writer.WritePropertyName("missing");
            JsonSerializer.Serialize(writer, reading.Missing!.Value, TypeInfo<MissingReason>(options));
            writer.WriteString("detail", reading.Detail);
        }

        writer.WriteEndObject();
    }

    private static JsonTypeInfo<TValue> TypeInfo<TValue>(JsonSerializerOptions options) =>
        (JsonTypeInfo<TValue>)options.GetTypeInfo(typeof(TValue));
}
