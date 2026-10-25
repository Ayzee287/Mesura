using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Mesura.Core.Raw.Serialization;

/// <summary>
/// The one way a <see cref="RawSnapshot"/> becomes JSON and back (architecture §4.4 "Serialisation"):
/// System.Text.Json source-generated metadata, camelCase names, enums as camelCase strings, byte
/// arrays as base64, the default encoder (non-ASCII and HTML-sensitive characters escaped, which also
/// makes a lone surrogate read back and write out identically), properties in declaration order, two-space indent, <c>\n</c> line ends, UTF-8
/// without BOM, one trailing newline. The output depends only on the snapshot: the same snapshot
/// always gives the same bytes, and reading then writing gives the same bytes again.
/// </summary>
public static class SnapshotJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    private static readonly JsonTypeInfo<RawSnapshot> SnapshotTypeInfo =
        (JsonTypeInfo<RawSnapshot>)Options.GetTypeInfo(typeof(RawSnapshot));

    public static byte[] Serialize(RawSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var json = JsonSerializer.SerializeToUtf8Bytes(snapshot, SnapshotTypeInfo);
        var output = new byte[json.Length + 1];
        json.CopyTo(output, 0);
        output[^1] = (byte)'\n';
        return output;
    }

    /// <exception cref="SnapshotFormatException">The input isn't a readable snapshot of this schema version.</exception>
    public static RawSnapshot Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        var version = ReadSchemaVersion(utf8Json);
        if (version != RawSnapshot.CurrentSchemaVersion)
        {
            throw new SnapshotFormatException($"Unsupported snapshot schemaVersion {version}; this build reads version {RawSnapshot.CurrentSchemaVersion}.");
        }

        try
        {
            return JsonSerializer.Deserialize(utf8Json, SnapshotTypeInfo)
                ?? throw new SnapshotFormatException("The snapshot is JSON null.");
        }
        catch (JsonException ex)
        {
            throw new SnapshotFormatException($"The snapshot is not valid: {ex.Message}", ex);
        }
        catch (ArgumentException ex)
        {
            // A model invariant rejected the data (for example a reading that is both known and missing).
            throw new SnapshotFormatException($"The snapshot breaks a model rule: {ex.Message}", ex);
        }
    }

    private static int ReadSchemaVersion(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8Json);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                throw new SnapshotFormatException("A snapshot is a JSON object.");
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("schemaVersion"u8))
                {
                    reader.Read();
                    return reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var version)
                        ? version
                        : throw new SnapshotFormatException("schemaVersion is not an integer.");
                }

                reader.Skip();
            }
        }
        catch (JsonException ex)
        {
            throw new SnapshotFormatException($"The snapshot is not valid JSON: {ex.Message}", ex);
        }

        throw new SnapshotFormatException("The snapshot has no schemaVersion.");
    }

    private static JsonSerializerOptions CreateOptions()
    {
        // The source-generated metadata covers every type in the model, including each T a Reading<T>
        // carries; the Reading converter is added here so that metadata stays generated.
        var options = new JsonSerializerOptions(SnapshotJsonContext.Default.Options);
        options.Converters.Add(new ReadingJsonConverterFactory());
        options.MakeReadOnly();
        return options;
    }
}

/// <summary>Metadata-only generation, so the <see cref="Reading{T}"/> converter always applies (no fast path bypasses it).</summary>
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
[JsonSerializable(typeof(RawSnapshot))]
internal sealed partial class SnapshotJsonContext : JsonSerializerContext;

public sealed class SnapshotFormatException : Exception
{
    public SnapshotFormatException(string message)
        : base(message)
    {
    }

    public SnapshotFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
