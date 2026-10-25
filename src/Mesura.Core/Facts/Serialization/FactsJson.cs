using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Mesura.Core.Raw.Serialization;

namespace Mesura.Core.Facts.Serialization;

/// <summary>
/// Writes a <see cref="NormalizedSnapshot"/> as JSON under the raw snapshot's rules (architecture §4.4
/// "Serialisation"): source-generated metadata, camelCase names, enums as camelCase strings, properties in
/// declaration order, two-space indent, <c>\n</c> line ends, UTF-8 without BOM, one trailing newline, the default
/// encoder. The output depends only on the facts, so the same raw snapshot always gives the same bytes.
/// </summary>
/// <remarks>
/// A fact is <c>{"value": …, "basis": …, "rule": …, "from": […]}</c> (no <c>rule</c> for a direct observation) or
/// <c>{"missing": …, "detail": …, "from": […]}</c>. In Milestone 2 the facts are written, not read back: nothing
/// consumes them from a file yet, so there is no reader to keep strict.
/// </remarks>
public static class FactsJson
{
    /// <summary>The property that holds a known fact's value, and so every list of subjects (<c>/displays/value/0</c>).</summary>
    internal const string ValueProperty = "value";

    private static readonly JsonSerializerOptions Options = CreateOptions();

    private static readonly JsonTypeInfo<NormalizedSnapshot> TypeInfo =
        (JsonTypeInfo<NormalizedSnapshot>)Options.GetTypeInfo(typeof(NormalizedSnapshot));

    public static byte[] Serialize(NormalizedSnapshot facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var json = JsonSerializer.SerializeToUtf8Bytes(facts, TypeInfo);
        var output = new byte[json.Length + 1];
        json.CopyTo(output, 0);
        output[^1] = (byte)'\n';
        return output;
    }

    /// <summary>
    /// A fact value as the facts JSON writes it, for a check's evidence to copy (architecture §4.6): the same bytes, so
    /// evidence can be compared with the fact it cites. <typeparamref name="T"/> is a value type of the facts model.
    /// </summary>
    internal static JsonElement ValueElement<T>(T value) =>
        JsonSerializer.SerializeToElement(value, ValueTypeInfo<T>());

    /// <summary>The facts' serialisation metadata for one of their value types (a rate, a size, an enum), so other writers render it identically.</summary>
    internal static JsonTypeInfo<T> ValueTypeInfo<T>() => (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(FactsJsonContext.Default.Options);
        options.Converters.Add(new FactJsonConverterFactory());
        options.MakeReadOnly();
        return options;
    }
}

/// <summary>Metadata-only generation, so the <see cref="Fact{T}"/> converter always applies.</summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    IndentSize = 2,
    NewLine = "\n")]
[JsonSerializable(typeof(NormalizedSnapshot))]
internal sealed partial class FactsJsonContext : JsonSerializerContext;

internal sealed class FactJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Fact<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(FactJsonConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;
}

internal sealed class FactJsonConverter<T> : JsonConverter<Fact<T>>
{
    public override Fact<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("Facts are written, not read, in this version.");

    public override void Write(Utf8JsonWriter writer, Fact<T> fact, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        if (fact.IsKnown)
        {
            writer.WritePropertyName(FactsJson.ValueProperty);
            JsonSerializer.Serialize(writer, fact.Value!, TypeInfoOf<T>(options));
            writer.WritePropertyName("basis");
            JsonSerializer.Serialize(writer, fact.Basis!.Value, TypeInfoOf<Basis>(options));
            if (fact.Rule is not null)
            {
                writer.WriteString("rule", fact.Rule);
            }
        }
        else
        {
            writer.WritePropertyName("missing");
            JsonSerializer.Serialize(writer, fact.Missing!.Value, TypeInfoOf<FactGap>(options));
            writer.WriteString("detail", fact.Detail);
        }

        writer.WriteStartArray("from");
        foreach (var pointer in fact.From)
        {
            writer.WriteStringValue(pointer);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static JsonTypeInfo<TValue> TypeInfoOf<TValue>(JsonSerializerOptions options) =>
        (JsonTypeInfo<TValue>)options.GetTypeInfo(typeof(TValue));
}
