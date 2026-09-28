using System.Text.Json.Serialization;

namespace FpsUtility.Core.Raw;

/// <summary>
/// The <c>steam</c> section: Steam's library files as text (architecture §4.4). The user's profile
/// path is rewritten as <c>%USERPROFILE%</c>, and account and activity values are redacted, at
/// collection (§13; see <c>Privacy.SteamTextScrubber</c>). Parsing happens in the normaliser.
/// </summary>
/// <param name="InstallPath">Where Steam is installed.</param>
/// <param name="LibraryFolders">Steam's list of library folders (<c>steamapps\libraryfolders.vdf</c>).</param>
/// <param name="Libraries">Each library folder that list names, with its app manifests; missing if the list couldn't be read.</param>
public sealed record SteamData
{
    [JsonConstructor]
    public SteamData(string installPath, Reading<SteamFile> libraryFolders, Reading<IReadOnlyList<SteamLibrary>> libraries)
    {
        InstallPath = installPath;
        LibraryFolders = libraryFolders;
        Libraries = Canonical.Sort(libraries, static (a, b) => Canonical.Ordinal(a.Path, b.Path));
    }

    public string InstallPath { get; }

    public Reading<SteamFile> LibraryFolders { get; }

    public Reading<IReadOnlyList<SteamLibrary>> Libraries { get; }
}

public sealed record SteamLibrary
{
    [JsonConstructor]
    public SteamLibrary(string path, Reading<IReadOnlyList<SteamFile>> manifests)
    {
        Path = path;
        Manifests = Canonical.Sort(manifests, static (a, b) => Canonical.Ordinal(a.Path, b.Path));
    }

    public string Path { get; }

    /// <summary>The library's <c>steamapps\appmanifest_*.acf</c> files.</summary>
    public Reading<IReadOnlyList<SteamFile>> Manifests { get; }
}

public sealed record SteamFile(string Path, string Text);
