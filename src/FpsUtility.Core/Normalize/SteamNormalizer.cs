using System.Globalization;
using FpsUtility.Core.Facts;
using FpsUtility.Core.Raw;

namespace FpsUtility.Core.Normalize;

/// <summary>
/// Steam's registration, libraries and installed apps. Four states stay apart: registered with libraries,
/// registered with the library list unreadable, not registered (a known fact), and the source failed (missing).
/// </summary>
internal static class SteamNormalizer
{
    private const string Source = "/sources/steam";
    private const string Libraries = "/sources/steam/data/libraries/value";

    public static SteamFacts Normalize(SourceEntry<SteamData> source)
    {
        if (!Evidence.TryData(source, "steam", out var steam, out var unavailable))
        {
            if (unavailable.Gap != FactGap.NotPresent)
            {
                return new SteamFacts(unavailable.Fact<SteamRegistration>(), unavailable.Fact<string>(), unavailable.Fact<IReadOnlyList<SteamLibraryFacts>>());
            }

            // The source answered that Steam isn't registered for this user: a fact, not a gap.
            const string notRegistered = "Steam isn't registered for this user";
            return new SteamFacts(
                Fact.Derived(SteamRegistration.NotRegistered, Basis.Authoritative, Rules.SteamRegistration, Source),
                Fact.Missing<string>(FactGap.NotPresent, notRegistered, Source),
                Fact.Missing<IReadOnlyList<SteamLibraryFacts>>(FactGap.NotPresent, notRegistered, Source));
        }

        var libraries = Evidence.Map(
            Evidence.Observed(steam.Libraries, Basis.Declared, "/sources/steam/data/libraries"),
            list => (IReadOnlyList<SteamLibraryFacts>)[.. list.Select(Library)]);

        return new SteamFacts(
            Fact.Derived(SteamRegistration.Registered, Basis.Authoritative, Rules.SteamRegistration, Source),
            Fact.Observed(steam.InstallPath, Basis.Declared, "/sources/steam/data/installPath"),
            libraries);
    }

    private static SteamLibraryFacts Library(SteamLibrary library, int index)
    {
        var at = $"{Libraries}/{index}";
        return new SteamLibraryFacts(
            Id: string.Create(CultureInfo.InvariantCulture, $"steam-library-{index + 1}"),
            Path: Fact.Observed(library.Path, Basis.Declared, $"{at}/path"),
            ListedBySteam: Fact.Observed(library.ListedByLibraryFolders, Basis.Declared, $"{at}/listedByLibraryFolders"),
            IsInstallFolder: Fact.Observed(library.IsInstallFolder, Basis.Declared, $"{at}/isInstallFolder"),
            Apps: Evidence.Map(
                Evidence.Observed(library.Manifests, Basis.Declared, $"{at}/manifests"),
                manifests => (IReadOnlyList<SteamAppFacts>)[.. manifests.Select((manifest, manifestIndex) => App(manifest, $"{at}/manifests/value/{manifestIndex}/text"))]));
    }

    /// <summary><see cref="Rules.SteamAppManifest"/>: one manifest's app, every field missing if the file can't be read.</summary>
    private static SteamAppFacts App(SteamFile manifest, string at)
    {
        VdfNode state;
        try
        {
            state = Vdf.Parse(manifest.Text);
        }
        catch (FormatException ex)
        {
            return Unreadable($"The manifest isn't a KeyValues document: {ex.Message}", at);
        }

        if (!state.Key.Equals("AppState", StringComparison.OrdinalIgnoreCase) || !state.IsBlock)
        {
            return Unreadable("The manifest's root key is not an AppState block", at);
        }

        return new SteamAppFacts(
            AppId: Number<int>(state, "appid", at, static text => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null),
            Name: Text(state, "name", at),
            InstallDirectory: Text(state, "installdir", at),
            SizeOnDiskBytes: Number<ulong>(state, "SizeOnDisk", at, static text => ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var size) ? size : null));
    }

    private static SteamAppFacts Unreadable(string why, string at) => new(
        Fact.Missing<int>(FactGap.ParseFailed, why, at),
        Fact.Missing<string>(FactGap.ParseFailed, why, at),
        Fact.Missing<string>(FactGap.ParseFailed, why, at),
        Fact.Missing<ulong>(FactGap.ParseFailed, why, at));

    private static Fact<string> Text(VdfNode state, string key, string at) =>
        state.Child(key) is { IsBlock: false } node
            ? Fact.Derived(node.Text!, Basis.Declared, Rules.SteamAppManifest, at)
            : Fact.Missing<string>(FactGap.NotPresent, $"The manifest has no \"{key}\" value", at);

    private static Fact<T> Number<T>(VdfNode state, string key, string at, Func<string, T?> parse)
        where T : struct
    {
        if (state.Child(key) is not { IsBlock: false } node)
        {
            return Fact.Missing<T>(FactGap.NotPresent, $"The manifest has no \"{key}\" value", at);
        }

        return parse(node.Text!) is { } value
            ? Fact.Derived(value, Basis.Declared, Rules.SteamAppManifest, at)
            : Fact.Missing<T>(FactGap.ParseFailed, $"The manifest's \"{key}\" is not a number of the expected kind", at);
    }
}
