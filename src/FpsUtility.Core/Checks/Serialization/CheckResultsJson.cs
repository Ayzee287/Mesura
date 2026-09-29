using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FpsUtility.Core.Facts;
using FpsUtility.Core.Facts.Serialization;

namespace FpsUtility.Core.Checks.Serialization;

/// <summary>
/// Writes <see cref="CheckResults"/> as JSON under the same rules as the raw snapshot and the facts (architecture §4.4
/// "Serialisation"): source-generated metadata, camelCase names, enums as camelCase strings, declaration order, two-space
/// indent, <c>\n</c> line ends, UTF-8 without BOM, one trailing newline. Absent optional values (a certainty on Not
/// applicable, a missing fact's value) are left out. Parameters and trace values are objects keyed by name, in the
/// order the check recorded them. The same results always give the same bytes.
/// </summary>
/// <remarks>Results are written, not read back: nothing consumes them from a file yet.</remarks>
public static class CheckResultsJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    private static readonly JsonTypeInfo<CheckResults> TypeInfo =
        (JsonTypeInfo<CheckResults>)Options.GetTypeInfo(typeof(CheckResults));

    public static byte[] Serialize(CheckResults results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var json = JsonSerializer.SerializeToUtf8Bytes(results, TypeInfo);
        var output = new byte[json.Length + 1];
        json.CopyTo(output, 0);
        output[^1] = (byte)'\n';
        return output;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(CheckResultsJsonContext.Default.Options);
        options.Converters.Add(new CheckValueJsonConverter());
        options.Converters.Add(new NamedValuesJsonConverter());
        options.MakeReadOnly();
        return options;
    }
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    IndentSize = 2,
    NewLine = "\n")]
[JsonSerializable(typeof(CheckResults))]
internal sealed partial class CheckResultsJsonContext : JsonSerializerContext;

/// <summary>A <see cref="CheckValue"/> as the facts write the same value: a number, a rational, a size, a string or a list of strings.</summary>
internal sealed class CheckValueJsonConverter : JsonConverter<CheckValue>
{
    public override CheckValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("Check results are written, not read, in this version.");

    public override void Write(Utf8JsonWriter writer, CheckValue value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case IntegerValue integer:
                writer.WriteNumberValue(integer.Value);
                break;
            case RateValue rate:
                JsonSerializer.Serialize(writer, rate.Rate, FactsJson.ValueTypeInfo<RefreshRate>());
                break;
            case SizeValue size:
                JsonSerializer.Serialize(writer, size.Size, FactsJson.ValueTypeInfo<PixelSize>());
                break;
            case SymbolValue symbol:
                writer.WriteStringValue(symbol.Value);
                break;
            case SymbolsValue symbols:
                writer.WriteStartArray();
                foreach (var item in symbols.Values)
                {
                    writer.WriteStringValue(item);
                }

                writer.WriteEndArray();
                break;
            default:
                throw new JsonException($"{value.GetType().Name} is not a check value kind this writer knows.");
        }
    }
}

/// <summary>Named values as one JSON object, in recorded order (names are unique by construction).</summary>
internal sealed class NamedValuesJsonConverter : JsonConverter<IReadOnlyList<NamedValue>>
{
    private static readonly CheckValueJsonConverter Values = new();

    public override IReadOnlyList<NamedValue> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("Check results are written, not read, in this version.");

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<NamedValue> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var named in value)
        {
            writer.WritePropertyName(named.Name);
            Values.Write(writer, named.Value, options);
        }

        writer.WriteEndObject();
    }
}
