using System.Text.Json;
using System.Text.Json.Serialization;
using Mesura.Core.Support;

namespace Mesura.Core.Apps;

/// <summary>
/// The installable-application catalog and the bundles that group them, read from JSON (<c>apps.catalog.json</c>).
/// The built-in copy is embedded in this assembly; an edited copy in <c>%LOCALAPPDATA%\Mesura</c> (shared by the app,
/// the tray and the CLI) changes the catalog without a rebuild. Every winget id in the built-in copy was verified against the live winget
/// source on 2026-10-18. Core reads no files: the caller supplies the text of an edited copy.
/// </summary>
public sealed class AppCatalog
{
    public const string FileKind = "Mesura.AppCatalog";

    /// <summary>
    /// 2 since the redesign of 2026-10 (listed, context, detector). A build before it refuses a schema 2 copy instead of
    /// misreading it, and this build sets aside an edited copy of an older schema (<c>CatalogFiles</c>).
    /// </summary>
    public const int CurrentSchema = 2;
    public const string FileName = "apps.catalog.json";

    private static readonly Lazy<AppCatalog> BuiltIn = new(LoadBuiltIn);

    private AppCatalog(IReadOnlyList<AppPackage> packages, IReadOnlyList<AppBundle> bundles)
    {
        Packages = packages;
        Bundles = bundles;
    }

    /// <summary>The catalog compiled into this build.</summary>
    public static AppCatalog Default => BuiltIn.Value;

    public IReadOnlyList<AppPackage> Packages { get; }

    public IReadOnlyList<AppBundle> Bundles { get; }

    public AppPackage? ById(string id) =>
        Packages.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public AppBundle? BundleById(string id) =>
        Bundles.FirstOrDefault(b => b.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Resolves a bundle's app ids to packages. <see cref="Parse"/> guarantees every id resolves.</summary>
    public IReadOnlyList<AppPackage> Resolve(AppBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        return [.. bundle.AppIds.Select(ById).OfType<AppPackage>()];
    }

    /// <summary>Reads and validates a catalog file.</summary>
    /// <exception cref="FormatException">
    /// The text is not a catalog, is a newer schema, or is inconsistent (duplicate ids, a missing winget id, a bundle
    /// naming an unknown app). The message names the offending entry so a hand edit is easy to fix.
    /// </exception>
    public static AppCatalog Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        CatalogFile? file;
        try
        {
            file = JsonSerializer.Deserialize(json, CatalogJsonContext.Default.CatalogFile);
        }
        catch (JsonException e)
        {
            throw new FormatException($"The app catalog is not valid JSON: {e.Message}", e);
        }

        if (file is null || !LegacyIdentity.IsKind(file.Kind, FileKind))
        {
            throw new FormatException($"This file is not an app catalog (expected kind '{FileKind}').");
        }

        if (file.SchemaVersion > CurrentSchema)
        {
            throw new FormatException($"The app catalog uses schema {file.SchemaVersion}; this build reads up to {CurrentSchema}.");
        }

        var packages = new List<AppPackage>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in file.Apps ?? [])
        {
            if (string.IsNullOrWhiteSpace(app.Id) || string.IsNullOrWhiteSpace(app.Name))
            {
                throw new FormatException("Every app needs an 'id' and a 'name'.");
            }

            if (!ids.Add(app.Id))
            {
                throw new FormatException($"App id '{app.Id}' appears more than once.");
            }

            var source = app.Source?.ToLowerInvariant() switch
            {
                null or "winget" => AppSource.Winget,
                "msstore" => AppSource.MsStore,
                "manual" => AppSource.Manual,
                _ => throw new FormatException($"App '{app.Id}': unknown source '{app.Source}' (use winget, msstore or manual)."),
            };

            if (source != AppSource.Manual && string.IsNullOrWhiteSpace(app.WingetId))
            {
                throw new FormatException($"App '{app.Id}' needs a 'wingetId'.");
            }

            if (source == AppSource.Manual && string.IsNullOrWhiteSpace(app.Url))
            {
                throw new FormatException($"Manual app '{app.Id}' needs a 'url'.");
            }

            var context = app.Context?.ToLowerInvariant() switch
            {
                null or "any" => AppRunContext.Any,
                "user" => AppRunContext.SignedInUser,
                _ => throw new FormatException($"App '{app.Id}': unknown context '{app.Context}' (use any or user)."),
            };

            packages.Add(new AppPackage(app.Id, app.WingetId ?? "", app.Name, app.Category ?? "Other", source,
                app.Note ?? "", app.Url ?? "", app.Detectable ?? source != AppSource.Manual, app.Listed ?? true, context,
                app.Detector ?? "", app.Customizes ?? "", app.InstallLocation ?? false));
        }

        if (packages.FirstOrDefault(p => p.Customizes.Length > 0 && !ids.Contains(p.Customizes)) is { } orphan)
        {
            throw new FormatException($"App '{orphan.Id}' customises '{orphan.Customizes}', which is not in the catalog.");
        }

        var bundles = new List<AppBundle>();
        var bundleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bundle in file.Bundles ?? [])
        {
            if (string.IsNullOrWhiteSpace(bundle.Id) || !bundleIds.Add(bundle.Id))
            {
                throw new FormatException($"Bundle id '{bundle.Id}' is missing or appears more than once.");
            }

            var members = bundle.Apps ?? [];
            if (members.Count == 0)
            {
                throw new FormatException($"Bundle '{bundle.Id}' lists no apps.");
            }

            var unknown = members.FirstOrDefault(id => !ids.Contains(id));
            if (unknown is not null)
            {
                throw new FormatException($"Bundle '{bundle.Id}' names app '{unknown}', which is not in the catalog.");
            }

            bundles.Add(new AppBundle(bundle.Id, bundle.Name ?? bundle.Id, bundle.Description ?? "", members));
        }

        return new AppCatalog(packages, bundles);
    }

    /// <summary>The built-in catalog's JSON text, as embedded at build time.</summary>
    public static string BuiltInJson()
    {
        using var stream = typeof(AppCatalog).Assembly.GetManifestResourceStream(FileName)
            ?? throw new InvalidOperationException($"The built-in {FileName} resource is missing from this build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static AppCatalog LoadBuiltIn() => Parse(BuiltInJson());

    internal sealed record CatalogFile(string? Kind, int SchemaVersion, List<AppEntry>? Apps, List<BundleEntry>? Bundles);

    internal sealed record AppEntry(
        string? Id, string? WingetId, string? Name, string? Category, string? Source, string? Note, string? Url, bool? Detectable,
        bool? Listed = null, string? Context = null, string? Detector = null, string? Customizes = null, bool? InstallLocation = null);

    internal sealed record BundleEntry(string? Id, string? Name, string? Description, List<string>? Apps);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppCatalog.CatalogFile))]
internal sealed partial class CatalogJsonContext : JsonSerializerContext;
