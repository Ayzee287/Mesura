using System.Text.Json;
using Mesura.Core.Apps;
using Mesura.Core.Support;
using Mesura.Core.Windows;

namespace Mesura.Setup.Windows;

/// <summary>A catalog as loaded: the catalog, where it came from, and the error when an edited copy is broken.</summary>
/// <param name="SetAside">
/// The path of an edited copy this build doesn't use because an older Mesura made it (an older schema): the built-in
/// catalog is used instead, and the app and the CLI say so. <c>null</c> when there is none.
/// </param>
public sealed record LoadedCatalog<T>(T? Catalog, string Source, string? Error, string? SetAside = null, bool IgnoredWhileElevated = false) where T : class;

/// <summary>
/// Loads the catalogs: an edited copy in <c>%LOCALAPPDATA%\Mesura</c> when there is one, else the copy built into Core.
/// That one folder is shared by the app, the tray and the CLI (their executables can't share a folder: <c>Mesura.exe</c>
/// and <c>mesura.exe</c> are the same name to Windows), so they always read the same catalog; no copy ships next to the
/// executables. An edited copy with a mistake is reported, never silently replaced by the built-in one, and one that
/// lacks entries the built-in catalog has says so (<see cref="NewSinceCopied"/>). A copy an older Mesura made (an older
/// schema: before the 2026-10 catalog audit and redesign) is set aside, visibly, and the built-in catalog is used, so an
/// old copy can't quietly bring back definitions the audit corrected (<see cref="LoadedCatalog{T}.SetAside"/>).
/// </summary>
public static class CatalogFiles
{
    /// <summary>The shared folder for edited catalogs, beside the setup history.</summary>
    public static string SharedFolder =>
        DataFolder.Local(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <param name="elevated">
    /// Whether this process runs as administrator. The apps catalog names the winget package each entry installs, and an edited copy is a file any program
    /// running as the user can change: an elevated install of "7-Zip" could become an install of anything as administrator. So, as for the Windows catalog, an
    /// elevated Mesura uses the built-in catalog only and says an edited copy was left unused (docs/security.md).
    /// </param>
    public static LoadedCatalog<AppCatalog> Apps(bool elevated = false) =>
        IgnoreEditedWhenElevated(Load(AppCatalog.FileName, AppCatalog.Default, AppCatalog.Parse, DefaultFolders, AppCatalog.CurrentSchema), AppCatalog.Default, elevated);

    /// <summary>The built-in catalog instead of an edited (or broken) copy, remembering which copy was left unused, when elevated.</summary>
    internal static LoadedCatalog<T> IgnoreEditedWhenElevated<T>(LoadedCatalog<T> loaded, T builtIn, bool elevated) where T : class =>
        elevated && loaded.Source != "built-in" ? new LoadedCatalog<T>(builtIn, "built-in", null, loaded.Source, IgnoredWhileElevated: true) : loaded;

    /// <param name="elevated">
    /// Whether this process runs as administrator. The Windows catalog says which registry values, services and tasks Mesura writes, and an
    /// edited copy is a file in the user's data folder that any program running as the user can change: an elevated Mesura that read it
    /// would write whatever that program put there as administrator. So an elevated Mesura uses the built-in catalog only and says an
    /// edited copy was left unused (docs/security.md); edited copies still work in a normal session, where machine settings are skipped.
    /// </param>
    public static LoadedCatalog<WindowsCatalog> Windows(bool elevated = false)
    {
        return IgnoreEditedWhenElevated(Load(WindowsCatalog.FileName, WindowsCatalog.Default, WindowsCatalog.Parse, DefaultFolders, WindowsCatalog.CurrentSchema), WindowsCatalog.Default, elevated);
    }

    private static IEnumerable<string> DefaultFolders => [SharedFolder];

    /// <summary>
    /// For an edited catalog: the built-in operation and preset ids it doesn't have (an edited copy otherwise hides them
    /// silently). That is what newer builds added since the copy was made, and also anything removed from the copy on
    /// purpose: no record of the copy's origin is kept, so the note says both. Empty for the built-in catalog.
    /// </summary>
    public static IReadOnlyList<string> NewSinceCopied(LoadedCatalog<WindowsCatalog> loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        if (loaded.Catalog is not { } catalog || ReferenceEquals(catalog, WindowsCatalog.Default))
        {
            return [];
        }

        var have = catalog.Operations.Select(o => o.Id).Concat(catalog.Presets.Select(p => p.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. WindowsCatalog.Default.Operations.Select(o => o.Id).Concat(WindowsCatalog.Default.Presets.Select(p => p.Id)).Where(id => !have.Contains(id))];
    }

    /// <summary>For an edited app catalog: the built-in app and bundle ids it doesn't have.</summary>
    public static IReadOnlyList<string> NewSinceCopied(LoadedCatalog<AppCatalog> loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        if (loaded.Catalog is not { } catalog || ReferenceEquals(catalog, AppCatalog.Default))
        {
            return [];
        }

        var have = catalog.Packages.Select(p => p.Id).Concat(catalog.Bundles.Select(b => b.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. AppCatalog.Default.Packages.Select(p => p.Id).Concat(AppCatalog.Default.Bundles.Select(b => b.Id)).Where(id => !have.Contains(id))];
    }

    /// <summary>A sentence for the user when an edited catalog lacks built-in entries, or empty.</summary>
    public static string StaleNote(string file, IReadOnlyList<string> missing) => missing.Count == 0 ? "" :
        $"Your edited {file} lacks {missing.Count} entr{(missing.Count == 1 ? "y" : "ies")} this version of Mesura has ({string.Join(", ", missing.Take(4))}{(missing.Count > 4 ? ", …" : "")}). Unless you removed them on purpose, add them to your copy, or delete it to use the built-in catalog.";

    /// <summary>A sentence for the user when an edited copy was set aside (<see cref="LoadedCatalog{T}.SetAside"/>), or empty.</summary>
    public static string SetAsideNote<T>(string file, LoadedCatalog<T> loaded) where T : class
    {
        ArgumentNullException.ThrowIfNull(loaded);
        return loaded.SetAside is not { } path ? "" :
            loaded.IgnoredWhileElevated ? $"Your edited {file} ({path}) isn't used while Mesura runs as administrator, because any program you run could have changed it. Mesura uses its built-in catalog. Run without administrator rights to use your copy." :
            $"Your edited {file} ({path}) was made by an older Mesura, so this version doesn't use it: it uses its built-in catalog, which has the 2026-10 catalog audit. To edit the catalog again, delete or rename that copy and copy the catalog again (Settings > Catalogs > Copy to edit).";
    }

    /// <summary>Loads the first copy of <paramref name="fileName"/> found in <paramref name="folders"/>, else the built-in one.</summary>
    /// <param name="currentSchema">
    /// When given, a copy that declares an older schema is set aside and the built-in catalog is used
    /// (<see cref="LoadedCatalog{T}.SetAside"/>); it isn't parsed, so an old copy the newer rules would refuse can't
    /// block Mesura either.
    /// </param>
    public static LoadedCatalog<T> Load<T>(string fileName, T builtIn, Func<string, T> parse, IEnumerable<string> folders, int? currentSchema = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(parse);
        ArgumentNullException.ThrowIfNull(folders);
        var path = folders.Select(f => Path.Combine(f, fileName)).FirstOrDefault(File.Exists);
        if (path is null)
        {
            return new LoadedCatalog<T>(builtIn, "built-in", null);
        }

        try
        {
            var text = File.ReadAllText(path);
            if (currentSchema is { } current && SchemaOf(text) is { } schema && schema < current)
            {
                return new LoadedCatalog<T>(builtIn, "built-in", null, path);
            }

            return new LoadedCatalog<T>(parse(text), path, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException
            or ArgumentException or NullReferenceException)
        {
            // An empty file or a null entry in a hand-edited copy surfaces as these from the parser.
            return new LoadedCatalog<T>(null, path, $"Could not load {path}: {e.Message}");
        }
    }

    /// <summary>The schema a catalog file declares (0 when it declares none), or <c>null</c> when it isn't JSON at all.</summary>
    private static int? SchemaOf(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            // A value that isn't a whole number is left to the parser, which reports it.
            return doc.RootElement.ValueKind != JsonValueKind.Object ? null
                : !doc.RootElement.TryGetProperty("schemaVersion", out var version) ? 0
                : version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var n) ? n
                : null;
        }
        catch (JsonException)
        {
            return null; // the parser reports what is wrong with it
        }
    }
}
