using System.Text.Json;

namespace Mesura.Core.Firmware;

/// <summary>A text in the languages the product speaks; a missing language falls back to English.</summary>
public sealed record LocalizedText(string En, string Ru)
{
    public string In(string language) => string.Equals(language, "ru", StringComparison.OrdinalIgnoreCase) && Ru.Length > 0 ? Ru : En;
}

public sealed record KnowledgeLink(LocalizedText Title, string Url);

/// <summary>What the page says about one firmware setting, besides what it found on this PC: what it is, why it may matter, what it can cost, the steps, how to check.</summary>
public sealed record FirmwareSetting(
    string Id,
    LocalizedText Title,
    LocalizedText What,
    LocalizedText Why,
    LocalizedText Risk,
    IReadOnlyDictionary<string, IReadOnlyList<LocalizedText>> Steps,
    LocalizedText Verify,
    IReadOnlyList<KnowledgeLink> Links)
{
    /// <summary>The steps for the board's maker when the file has them, the common ones otherwise.</summary>
    public IReadOnlyList<LocalizedText> StepsFor(FirmwareVendor vendor) =>
        Steps.TryGetValue(FirmwareVendors.Key(vendor), out var own) ? own : Steps.TryGetValue("common", out var common) ? common : [];

    /// <summary>Whether the file carries this maker's own menu path (not just the common steps).</summary>
    public bool HasOwnSteps(FirmwareVendor vendor) => FirmwareVendors.Key(vendor) != "common" && Steps.ContainsKey(FirmwareVendors.Key(vendor));
}

/// <summary>
/// The firmware knowledge file (<c>firmware.knowledge.json</c>, embedded): one entry per setting the analyzer reports on, in English and
/// Russian. Menu names are the ones makers' boards commonly use and are labelled as such; none was checked on hardware.
/// </summary>
public sealed class FirmwareKnowledge
{
    public const string FileName = "firmware.knowledge.json";
    private static readonly Lazy<FirmwareKnowledge> BuiltIn = new(() => Parse(BuiltInJson()));

    private FirmwareKnowledge(LocalizedText note, IReadOnlyDictionary<string, FirmwareSetting> settings)
    {
        Note = note;
        Settings = settings;
    }

    public static FirmwareKnowledge Default => BuiltIn.Value;

    /// <summary>The file's own caveat about menu names.</summary>
    public LocalizedText Note { get; }

    public IReadOnlyDictionary<string, FirmwareSetting> Settings { get; }

    public FirmwareSetting? Get(string id) => Settings.GetValueOrDefault(id);

    public static string BuiltInJson()
    {
        using var stream = typeof(FirmwareKnowledge).Assembly.GetManifestResourceStream(FileName)
            ?? throw new InvalidOperationException($"The built-in {FileName} resource is missing from this build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <exception cref="FormatException">The text is not a valid knowledge file.</exception>
    public static FirmwareKnowledge Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            if (!root.TryGetProperty("kind", out var kind) || kind.GetString() != "Mesura.FirmwareKnowledge")
            {
                throw new FormatException("Not a Mesura firmware knowledge file.");
            }

            var settings = new Dictionary<string, FirmwareSetting>(StringComparer.Ordinal);
            foreach (var s in root.GetProperty("settings").EnumerateArray())
            {
                var id = s.GetProperty("id").GetString() ?? throw new FormatException("A setting has no id.");
                var steps = new Dictionary<string, IReadOnlyList<LocalizedText>>(StringComparer.Ordinal);
                foreach (var vendor in s.GetProperty("steps").EnumerateObject())
                {
                    steps[vendor.Name] = vendor.Value.EnumerateArray().Select(Text).ToList();
                }

                var links = s.TryGetProperty("links", out var l) ? l.EnumerateArray().Select(x => new KnowledgeLink(Text(x.GetProperty("title")), x.GetProperty("url").GetString() ?? "")).ToList() : [];
                settings[id] = new FirmwareSetting(id, Text(s.GetProperty("title")), Text(s.GetProperty("what")), Text(s.GetProperty("why")), Text(s.GetProperty("risk")), steps, Text(s.GetProperty("verify")), links);
            }

            return new FirmwareKnowledge(root.TryGetProperty("note", out var note) ? Text(note) : new LocalizedText("", ""), settings);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new FormatException("The firmware knowledge file is not valid: " + e.Message, e);
        }
    }

    private static LocalizedText Text(JsonElement e) => new(
        e.TryGetProperty("en", out var en) ? en.GetString() ?? "" : "",
        e.TryGetProperty("ru", out var ru) ? ru.GetString() ?? "" : "");
}
