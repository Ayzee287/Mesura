using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FpsUtility.Core.Raw;
using FpsUtility.Core.Raw.Serialization;

namespace FpsUtility.Core.Privacy;

/// <summary>
/// Makes a snapshot fit to leave its machine as a fixture (architecture §4.4 <c>capture-fixture</c>,
/// §13). Collection has already scrubbed serials and the profile path. This step replaces the opaque
/// platform ids, which rule 3 allows only as join keys within one snapshot, with consistent stand-ins:
/// <list type="bullet">
/// <item>adapter LUIDs (<c>adapterId</c>) become <c>adapter-N</c>;</item>
/// <item>the instance part of PnP instance ids (<c>instanceId</c>) becomes <c>instance-N</c>. On some
/// PCIe devices Windows builds it from the device's serial number. The enumerator and hardware-ID parts
/// are kept, because they are model data;</item>
/// <item>the instance part of monitor device paths becomes <c>instance-N</c> (the monitor's model code
/// and the interface class are kept);</item>
/// <item>volume GUIDs become <c>{volume-N}</c>.</item>
/// </list>
/// Every string in the snapshot gets the same replacement, including failure details, so every join
/// still works. Byte payloads (EDID, SMBIOS) are not text and are left alone. An id is found by the
/// property that holds it, also when a reading wraps it (<c>"monitorDevicePath": {"value": …}</c>).
/// Within each kind, stand-ins are numbered in the ordinal order of the original ids, not in document
/// order, so an id that survives a restart (a volume GUID, a PnP instance) keeps its stand-in even when
/// run-dependent values such as disk numbers reorder the document. Adapter LUIDs change on reboot, so
/// their numbering can change too. Hardware facts are never changed.
/// </summary>
public static partial class FixturePseudonymizer
{
    private static readonly HashSet<string> BytePayloadProperties = new(StringComparer.Ordinal) { "bytes", "table" };

    /// <returns>The pseudonymised snapshot and how many distinct ids were replaced.</returns>
    public static (RawSnapshot Snapshot, int IdsReplaced) Pseudonymize(RawSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var document = JsonNode.Parse(SnapshotJson.Serialize(snapshot))!;
        var found = new Dictionary<string, (string Kind, Func<int, string> Make)>(StringComparer.Ordinal);

        Collect(document, null, found);

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kind in found.GroupBy(pair => pair.Value.Kind, StringComparer.Ordinal))
        {
            var number = 0;
            foreach (var (original, (_, make)) in kind.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                map[original] = make(++number);
            }
        }

        var replacements = map.OrderByDescending(pair => pair.Key.Length).ThenBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        Replace(document, null, replacements);

        var pseudonymised = SnapshotJson.Deserialize(Encoding.UTF8.GetBytes(document.ToJsonString()));
        return (pseudonymised, map.Count);
    }

    /// <summary>
    /// Walks the JSON and records each opaque id with its kind and how to build its stand-in. A reading's
    /// <c>value</c> belongs to the property that holds the reading.
    /// </summary>
    private static void Collect(JsonNode? node, string? property, Dictionary<string, (string Kind, Func<int, string> Make)> found)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, child) in obj)
                {
                    Collect(child, name == "value" ? property : name, found);
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    Collect(child, property, found);
                }

                break;
            case JsonValue value when property is not null && !BytePayloadProperties.Contains(property) && value.TryGetValue<string>(out var text):
                AddIds(property, text, found);
                break;
        }
    }

    private static void AddIds(string property, string text, Dictionary<string, (string Kind, Func<int, string> Make)> found)
    {
        switch (property)
        {
            case "adapterId":
                Assign(found, text, "adapter", static n => $"adapter-{n}");
                break;
            case "instanceId":
                var cut = text.LastIndexOf('\\');
                Assign(found, text, "instance", n => cut > 0 ? $"{text[..(cut + 1)]}instance-{n}" : $"instance-{n}");
                break;
            case "monitorDevicePath":
                var parts = text.Split('#');
                Assign(found, text, "instance", n =>
                {
                    if (parts.Length < 4)
                    {
                        return $"monitor-instance-{n}";
                    }

                    parts[2] = $"instance-{n}";
                    return string.Join('#', parts);
                });
                break;
        }

        foreach (Match match in VolumeGuid().Matches(text))
        {
            Assign(found, match.Value, "volume", static n => $"{{volume-{n}}}");
        }
    }

    private static void Assign(Dictionary<string, (string Kind, Func<int, string> Make)> found, string original, string kind, Func<int, string> make) =>
        found.TryAdd(original, (kind, make));

    private static void Replace(JsonNode? node, string? property, KeyValuePair<string, string>[] replacements)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(pair => pair.Key).ToArray())
                {
                    var child = obj[name];
                    if (child is JsonValue value && !BytePayloadProperties.Contains(name) && value.TryGetValue<string>(out var text))
                    {
                        obj[name] = ReplaceAll(text, replacements);
                    }
                    else
                    {
                        Replace(child, name, replacements);
                    }
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        array[i] = ReplaceAll(text, replacements);
                    }
                    else
                    {
                        Replace(array[i], property, replacements);
                    }
                }

                break;
        }
    }

    private static string ReplaceAll(string text, KeyValuePair<string, string>[] replacements)
    {
        foreach (var (original, pseudonym) in replacements)
        {
            text = text.Replace(original, pseudonym, StringComparison.OrdinalIgnoreCase);
        }

        return text;
    }

    /// <summary>A braced GUID following "Volume", as in <c>\\?\Volume{…}\</c>.</summary>
    [GeneratedRegex(@"(?<=Volume)\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}", RegexOptions.CultureInvariant)]
    private static partial Regex VolumeGuid();
}
