using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Mesura.Core.TrackA;

/// <summary>
/// The JSON of the Track A documents (architecture §4.7), under the raw snapshot's rules (§4.4 "Serialisation"):
/// source-generated metadata, camelCase names, enums as camelCase strings, declaration order, two-space indent,
/// <c>\n</c> line ends, UTF-8 without BOM, one trailing newline. Reading is strict, like the snapshot's: unknown or
/// duplicated properties, missing required ones, nulls where the model has none, undefined enum names and values a
/// model constructor refuses all fail as <see cref="TrackAFormatException"/>, and the whole file is rejected.
/// </summary>
public static class TrackAJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static byte[] Serialize<T>(T document)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(document);

        var json = JsonSerializer.SerializeToUtf8Bytes(document, TypeInfo<T>());
        var output = new byte[json.Length + 1];
        json.CopyTo(output, 0);
        output[^1] = (byte)'\n';
        return output;
    }

    /// <param name="expectedSchemaVersion">The only <c>schemaVersion</c> this build reads for <typeparamref name="T"/>.</param>
    /// <exception cref="TrackAFormatException">The input isn't a valid document of that version.</exception>
    public static T Deserialize<T>(ReadOnlySpan<byte> utf8Json, int expectedSchemaVersion)
        where T : class
    {
        var version = ReadSchemaVersion(utf8Json, typeof(T).Name);
        if (version != expectedSchemaVersion)
        {
            throw new TrackAFormatException($"Unsupported {typeof(T).Name} schemaVersion {version}; this build reads version {expectedSchemaVersion}.");
        }

        try
        {
            return JsonSerializer.Deserialize(utf8Json, TypeInfo<T>()) ?? throw new TrackAFormatException($"The {typeof(T).Name} is JSON null.");
        }
        catch (JsonException ex)
        {
            throw new TrackAFormatException($"The {typeof(T).Name} is not valid: {ex.Message}", ex);
        }
        catch (ArgumentException ex)
        {
            throw new TrackAFormatException($"The {typeof(T).Name} breaks a model rule: {ex.Message}", ex);
        }
    }

    private static JsonTypeInfo<T> TypeInfo<T>() => (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));

    private static int ReadSchemaVersion(ReadOnlySpan<byte> utf8Json, string what)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8Json);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                throw new TrackAFormatException($"A {what} is a JSON object.");
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("schemaVersion"u8))
                {
                    reader.Read();
                    return reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var version)
                        ? version
                        : throw new TrackAFormatException($"The {what}'s schemaVersion is not an integer.");
                }

                reader.Skip();
            }
        }
        catch (JsonException ex)
        {
            throw new TrackAFormatException($"The {what} is not valid JSON: {ex.Message}", ex);
        }

        throw new TrackAFormatException($"The {what} has no schemaVersion.");
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(TrackAJsonContext.Default.Options);
        options.Converters.Add(new ObservedValueJsonConverter());
        options.MakeReadOnly();
        return options;
    }
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    IndentSize = 2,
    NewLine = "\n",
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false)]
[JsonSerializable(typeof(OperatorObservations))]
[JsonSerializable(typeof(CaptureManifest))]
[JsonSerializable(typeof(AcceptanceRecord))]
[JsonSerializable(typeof(PrivacyFailureReport))]
[JsonSerializable(typeof(CohortIndex))]
internal sealed partial class TrackAJsonContext : JsonSerializerContext;

/// <summary>An observation's value: a string (a choice, a label or a note), an array of choice strings, or an integer.</summary>
internal sealed class ObservedValueJsonConverter : JsonConverter<ObservedValue>
{
    public override ObservedValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return new ObservedText(reader.GetString()!);
            case JsonTokenType.Number when reader.TryGetInt32(out var number):
                return new ObservedNumber(number);
            case JsonTokenType.StartArray:
                var items = new List<string>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    items.Add(reader.TokenType == JsonTokenType.String ? reader.GetString()! : throw new JsonException("A list of choices holds strings only."));
                }

                return new ObservedChoices(items);
            default:
                throw new JsonException("An observation value is a string, a list of strings or a whole number.");
        }
    }

    public override void Write(Utf8JsonWriter writer, ObservedValue value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case ObservedText text:
                writer.WriteStringValue(text.Text);
                break;
            case ObservedNumber number:
                writer.WriteNumberValue(number.Number);
                break;
            case ObservedChoices choices:
                writer.WriteStartArray();
                foreach (var item in choices.Items)
                {
                    writer.WriteStringValue(item);
                }

                writer.WriteEndArray();
                break;
            default:
                throw new JsonException($"{value.GetType().Name} is not an observation value kind this writer knows.");
        }
    }
}

/// <summary>
/// The list rule the raw snapshot's reader has too (§4.4): a list in a Track A document never holds a null item, so a
/// malformed file is refused whole instead of failing later.
/// </summary>
internal static class Guard
{
    public static bool NoNulls<T>(IReadOnlyList<T>? list, string what) =>
        list is not null && list.All(item => item is not null) ? true : throw new ArgumentException($"{what} is a list without null items.", what);
}

public sealed class TrackAFormatException : Exception
{
    public TrackAFormatException(string message)
        : base(message)
    {
    }

    public TrackAFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
