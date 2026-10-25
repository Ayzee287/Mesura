using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Mesura.Core.Privacy;

/// <summary>A string in a JSON document, with where it is.</summary>
/// <param name="Pointer">Its JSON Pointer (RFC 6901), e.g. <c>/sources/steam/data/libraries/value/0/path</c>.</param>
/// <param name="Property">The property that holds it: its own name, or, for an array element, the array's.</param>
/// <param name="Value">The decoded string.</param>
/// <param name="Holder">
/// The property the value belongs to, where a reading's <c>value</c> belongs to the property that holds the reading
/// (<c>"monitorDevicePath": {"value": …}</c> belongs to <c>monitorDevicePath</c>), as the pseudonymiser reads it.
/// </param>
public sealed record JsonString(string Pointer, string? Property, string Value, string? Holder);

/// <summary>
/// Lists every string value in a JSON document with its JSON Pointer, so a privacy finding can say where it is
/// without repeating what it is. Property names aren't listed: every JSON file the product writes has fixed names.
/// </summary>
public static class JsonStrings
{
    /// <exception cref="JsonException">The input isn't JSON.</exception>
    public static IReadOnlyList<JsonString> Of(ReadOnlySpan<byte> json)
    {
        var found = new List<JsonString>();
        var reader = new Utf8JsonReader(json);
        var segments = new List<(string Text, bool IsIndex)>();
        var containers = new List<(bool IsArray, int Next)>();
        string? name = null;

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    name = reader.GetString();
                    break;
                case JsonTokenType.StartObject or JsonTokenType.StartArray:
                    if (containers.Count > 0)
                    {
                        segments.Add(NextSegment());
                    }

                    containers.Add((reader.TokenType == JsonTokenType.StartArray, 0));
                    break;
                case JsonTokenType.EndObject or JsonTokenType.EndArray:
                    containers.RemoveAt(containers.Count - 1);
                    if (containers.Count > 0)
                    {
                        segments.RemoveAt(segments.Count - 1);
                    }

                    break;
                case JsonTokenType.String:
                    (string Text, bool IsIndex)? own = containers.Count > 0 ? NextSegment() : null;
                    var property = own is { IsIndex: false } named ? named.Text : NearestName();
                    found.Add(new JsonString(Pointer(own), property, reader.GetString()!, property == "value" ? NearestName(skipValue: true) : property));
                    break;
                default:
                    if (containers.Count > 0)
                    {
                        NextSegment();
                    }

                    break;
            }
        }

        return found;

        // The segment of the value about to be read: the pending property name, or the array's next index.
        (string Text, bool IsIndex) NextSegment()
        {
            var (isArray, next) = containers[^1];
            if (!isArray)
            {
                return (name!, false);
            }

            containers[^1] = (true, next + 1);
            return (next.ToString(CultureInfo.InvariantCulture), true);
        }

        // The name of the nearest enclosing property, for an array element (or, skipping reading wrappers, for a value).
        string? NearestName(bool skipValue = false)
        {
            for (var i = segments.Count - 1; i >= 0; i--)
            {
                if (!segments[i].IsIndex && !(skipValue && segments[i].Text == "value"))
                {
                    return segments[i].Text;
                }
            }

            return null;
        }

        string Pointer((string Text, bool IsIndex)? last)
        {
            var pointer = new StringBuilder();
            foreach (var (text, _) in segments)
            {
                pointer.Append('/').Append(Escape(text));
            }

            if (last is { } segment)
            {
                pointer.Append('/').Append(Escape(segment.Text));
            }

            return pointer.ToString();
        }
    }

    /// <summary>RFC 6901 escaping: <c>~</c> becomes <c>~0</c> and <c>/</c> becomes <c>~1</c>.</summary>
    public static string Escape(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    }
}
