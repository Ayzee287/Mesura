using System.Text.Json;
using System.Text.Json.Serialization;
using Mesura.Core.Support;

namespace Mesura.Core.Config;

/// <summary>
/// The on-disk form of an exported environment: one portable file holding each app's captured settings. It carries no
/// file paths, usernames, credentials or account identifiers — only allowlisted preference values — so it restores on
/// a fresh install under a different Windows user. The time stamp is supplied by the caller (Core reads no clock).
/// </summary>
public sealed record ConfigBundleFile(
    string Kind,
    int SchemaVersion,
    string CreatedUtc,
    IReadOnlyDictionary<string, ConfigBundleFile.AppEntry> Apps)
{
    public const string FileKind = "Mesura.ConfigBundle";
    public const int CurrentSchema = 1;

    public sealed record AppEntry(string AppName, IReadOnlyDictionary<string, string> Values);

    public static string Save(IEnumerable<AppConfigBundle> bundles, string createdUtc)
    {
        ArgumentNullException.ThrowIfNull(bundles);
        ArgumentException.ThrowIfNullOrWhiteSpace(createdUtc);

        var apps = new Dictionary<string, AppEntry>(StringComparer.Ordinal);
        foreach (var b in bundles)
        {
            if (b.Values.Count > 0)
            {
                apps[b.PortId] = new AppEntry(b.AppName, new Dictionary<string, string>(b.Values, StringComparer.Ordinal));
            }
        }

        var file = new ConfigBundleFile(FileKind, CurrentSchema, createdUtc, apps);
        return JsonSerializer.Serialize(file, BundleJsonContext.Default.ConfigBundleFile);
    }

    /// <summary>Reads a bundle file written by <see cref="Save"/>.</summary>
    /// <exception cref="FormatException">The text is not a bundle file, or is a newer schema.</exception>
    public static ConfigBundleFile Load(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (string.IsNullOrWhiteSpace(json))
        {
            // An empty file (an interrupted save) is a bad file, reported like any other, not a programming error.
            throw new FormatException("The file is empty.");
        }

        ConfigBundleFile? file;
        try
        {
            file = JsonSerializer.Deserialize(json, BundleJsonContext.Default.ConfigBundleFile);
        }
        catch (JsonException e)
        {
            throw new FormatException("The configuration bundle is not valid JSON.", e);
        }

        if (file is null || !LegacyIdentity.IsKind(file.Kind, FileKind))
        {
            throw new FormatException("This file is not a Mesura configuration bundle.");
        }

        if (file.SchemaVersion > CurrentSchema)
        {
            throw new FormatException($"The bundle is schema {file.SchemaVersion}; this build reads up to {CurrentSchema}.");
        }

        return file with { Apps = file.Apps ?? new Dictionary<string, AppEntry>(StringComparer.Ordinal) };
    }

    /// <summary>The captured bundle for one app, ready for <see cref="ConfigEngine.Import"/>, or <c>null</c> if absent.</summary>
    public AppConfigBundle? ForApp(string portId) =>
        Apps.TryGetValue(portId, out var entry)
            ? new AppConfigBundle(portId, entry.AppName, entry.Values, [])
            : null;
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ConfigBundleFile))]
internal sealed partial class BundleJsonContext : JsonSerializerContext;
