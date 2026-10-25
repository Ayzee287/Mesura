using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mesura.Core.Raw.Serialization;

/// <summary>
/// Writes an enum as its camelCase member name (<c>notSupported</c>, <c>displayPort</c>) and reads only
/// that exact spelling. The accepted syntax is one JSON string equal, ordinally, to the camelCase name of
/// one defined member. Everything else is rejected with a <see cref="JsonException"/> (which
/// <see cref="SnapshotJson"/> reports as a <see cref="SnapshotFormatException"/>): numbers, other casing,
/// padding, comma-joined names, names this build doesn't define, and <c>null</c> where the model has no
/// null. A value the reader can't place is never turned into a different valid member, and an undefined
/// member can never be written or read.
/// </summary>
public sealed class CamelCaseEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private static readonly FrozenDictionary<string, TEnum> ByName =
        Enum.GetValues<TEnum>().ToFrozenDictionary(value => JsonNamingPolicy.CamelCase.ConvertName(value.ToString()), StringComparer.Ordinal);

    private static readonly FrozenDictionary<TEnum, JsonEncodedText> ByValue =
        ByName.ToFrozenDictionary(pair => pair.Value, pair => JsonEncodedText.Encode(pair.Key));

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"A {typeof(TEnum).Name} is a JSON string naming one member.");
        }

        var name = reader.GetString()!;
        return ByName.TryGetValue(name, out var value)
            ? value
            : throw new JsonException($"'{name}' is not a {typeof(TEnum).Name} member.");
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (!ByValue.TryGetValue(value, out var name))
        {
            throw new JsonException($"{value} is not a defined {typeof(TEnum).Name} member.");
        }

        writer.WriteStringValue(name);
    }
}
