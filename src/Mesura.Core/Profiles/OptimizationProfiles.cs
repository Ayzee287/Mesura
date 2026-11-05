using System.Text.Json;

namespace Mesura.Core.Profiles;

/// <summary>
/// The built-in profile definitions (<c>profiles.json</c>), read and checked for their own consistency: ids, groups, roles, hardware conditions and a reason
/// for every operation. That every operation exists in the catalog is not checked here (an edited catalog may lack one; the planner reports it as not
/// available), the tests check it against the built-in catalog.
/// </summary>
public sealed class OptimizationProfiles
{
    public const string FileKind = "Mesura.OptimizationProfiles";
    public const int CurrentSchema = 1;

    private static readonly Lazy<OptimizationProfiles> BuiltIn = new(() => Parse(ReadResource()));

    private OptimizationProfiles(IReadOnlyList<ProfileGroup> groups, IReadOnlyList<OptimizationProfile> profiles)
    {
        Groups = groups;
        Profiles = profiles;
    }

    public static OptimizationProfiles Default => BuiltIn.Value;

    /// <summary>Every group in the order the app lists them.</summary>
    public IReadOnlyList<ProfileGroup> Groups { get; }

    public IReadOnlyList<OptimizationProfile> Profiles { get; }

    public OptimizationProfile? ById(string id) => Profiles.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>A group's definition by id, or <c>null</c>.</summary>
    public ProfileGroup? Group(string id) => Groups.FirstOrDefault(g => g.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A profile for a catalog preset no built-in profile has the id of (an edited catalog's own primary preset): its operations as default entries of one
    /// group, optional ones as optional entries, with no reasons or hardware conditions. It keeps such a preset working in the planner.
    /// </summary>
    public OptimizationProfile FromPreset(Windows.WindowsPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var group = Group("windows") ?? new ProfileGroup("windows", "Windows", "");
        return new OptimizationProfile(preset.Id, [group], [.. preset.OperationIds.Select(id => new ProfileEntry(id, group.Id, EntryRole.Default, "", false, null))
            .Concat(preset.OptionalIds.Select(id => new ProfileEntry(id, group.Id, EntryRole.Optional, "", false, null)))]);
    }

    /// <exception cref="FormatException">The file is not a profile file this build can read, or is inconsistent.</exception>
    public static OptimizationProfiles Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException e)
        {
            throw new FormatException("The profile file is not valid JSON: " + e.Message, e);
        }

        using (document)
        {
            var root = document.RootElement;
            if (Text(root, "kind") != FileKind || !root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || schema.GetInt32() > CurrentSchema)
            {
                throw new FormatException("This is not a profile file this build can read.");
            }

            var why = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("why", out var whyElement) && whyElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in whyElement.EnumerateObject())
                {
                    why[property.Name] = property.Value.GetString() ?? "";
                }
            }

            var groups = new List<ProfileGroup>();
            foreach (var g in Array(root, "groups"))
            {
                var id = Required(g, "id", "a group");
                if (groups.Any(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new FormatException($"Group '{id}' appears more than once.");
                }

                groups.Add(new ProfileGroup(id, Required(g, "name", $"group '{id}'"), Text(g, "summary") ?? ""));
            }

            var profiles = new List<OptimizationProfile>();
            foreach (var p in Array(root, "profiles"))
            {
                var id = Required(p, "id", "a profile");
                if (profiles.Any(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new FormatException($"Profile '{id}' appears more than once.");
                }

                var entries = new List<ProfileEntry>();
                foreach (var e in Array(p, "entries"))
                {
                    entries.Add(Entry(id, e, groups, why));
                }

                if (entries.GroupBy(x => x.OperationId, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } twice)
                {
                    throw new FormatException($"Profile '{id}' lists '{twice.Key}' more than once.");
                }

                profiles.Add(new OptimizationProfile(id, groups, entries));
            }

            return new OptimizationProfiles(groups, profiles);
        }
    }

    private static ProfileEntry Entry(string profile, JsonElement e, List<ProfileGroup> groups, Dictionary<string, string> why)
    {
        var op = Required(e, "op", $"an entry of '{profile}'");
        var where = $"'{op}' in '{profile}'";
        var group = Required(e, "group", where);
        if (!groups.Any(g => g.Id.Equals(group, StringComparison.OrdinalIgnoreCase)))
        {
            throw new FormatException($"{where} names the unknown group '{group}'.");
        }

        var role = Text(e, "role")?.ToLowerInvariant() switch
        {
            null or "default" => EntryRole.Default,
            "optional" => EntryRole.Optional,
            var other => throw new FormatException($"{where} has the unknown role '{other}' (default or optional)."),
        };
        if (!why.TryGetValue(op, out var reason) || string.IsNullOrWhiteSpace(reason))
        {
            throw new FormatException($"{where} has no reason in 'why'.");
        }

        var sideEffects = e.TryGetProperty("sideEffects", out var flag) && flag.ValueKind == JsonValueKind.True;
        return new ProfileEntry(op, group, role, reason, sideEffects, e.TryGetProperty("when", out var when) ? Condition(where, when) : null);
    }

    private static HardwareCondition Condition(string where, JsonElement when)
    {
        if (when.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException($"{where}: 'when' must be an object.");
        }

        List<string>? form = null;
        string? cpu = null, gpu = null, disk = null, network = null;
        foreach (var property in when.EnumerateObject())
        {
            switch (property.Name)
            {
                case "form":
                    form = [.. (property.Value.ValueKind == JsonValueKind.Array ? property.Value.EnumerateArray().Select(v => v.GetString() ?? "") : [property.Value.GetString() ?? ""])];
                    foreach (var f in form)
                    {
                        if (!Enum.TryParse<Hardware.DeviceForm>(f, ignoreCase: true, out var parsed) || parsed == Hardware.DeviceForm.Unknown)
                        {
                            throw new FormatException($"{where}: unknown form '{f}' (desktop, compact or portable).");
                        }
                    }

                    break;
                case "cpu":
                    cpu = Checked(where, property, HardwareCondition.CpuValues);
                    break;
                case "gpu":
                    gpu = Checked(where, property, HardwareCondition.GpuValues);
                    break;
                case "disk":
                    disk = Checked(where, property, HardwareCondition.DiskValues);
                    break;
                case "network":
                    network = Checked(where, property, HardwareCondition.NetworkValues);
                    break;
                default:
                    throw new FormatException($"{where}: unknown condition '{property.Name}'.");
            }
        }

        return new HardwareCondition(form, cpu, gpu, disk, network);
    }

    private static string Checked(string where, JsonProperty property, IReadOnlySet<string> allowed)
    {
        var value = property.Value.GetString()?.ToLowerInvariant() ?? "";
        return allowed.Contains(value) ? value : throw new FormatException($"{where}: unknown {property.Name} '{value}' ({string.Join(", ", allowed.Order())}).");
    }

    private static IEnumerable<JsonElement> Array(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : [];

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Required(JsonElement element, string name, string what) =>
        Text(element, name) is { Length: > 0 } text ? text : throw new FormatException($"{what} has no '{name}'.");

    private static string ReadResource()
    {
        using var stream = typeof(OptimizationProfiles).Assembly.GetManifestResourceStream("profiles.json")
            ?? throw new InvalidOperationException("The built-in profiles.json resource is missing from this build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
