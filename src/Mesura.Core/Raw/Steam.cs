using System.Text.Json.Serialization;

namespace Mesura.Core.Raw;

/// <summary>
/// The <c>steam</c> section: Steam's library files as text (architecture §4.4). The user's profile
/// path is rewritten as <c>%USERPROFILE%</c>, and account, activity and library-identity values are
/// redacted, at collection (§13; see <c>Privacy.SteamTextScrubber</c>). Parsing happens in the normaliser.
/// </summary>
/// <param name="InstallPath">Where Steam is installed, as Steam registered it for this user.</param>
/// <param name="LibraryFolders">Steam's list of library folders (<c>steamapps\libraryfolders.vdf</c>).</param>
/// <param name="Libraries">
/// Every folder the collector read app manifests from, once each: the folders <c>libraryfolders.vdf</c> names and
/// Steam's install folder, each marked with why it is here (<see cref="SteamLibrary.ListedByLibraryFolders"/>,
/// <see cref="SteamLibrary.IsInstallFolder"/>). The install folder is read even when the list doesn't name it,
/// because older Steam versions leave it out of the list. Missing if the list couldn't be read.
/// </param>
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

/// <summary>One folder read as a Steam library.</summary>
public sealed record SteamLibrary
{
    [JsonConstructor]
    public SteamLibrary(string path, bool listedByLibraryFolders, bool isInstallFolder, Reading<IReadOnlyList<SteamFile>> manifests)
    {
        if (!listedByLibraryFolders && !isInstallFolder)
        {
            throw new ArgumentException("A library is read because Steam's list names it, because it is Steam's install folder, or both.");
        }

        Path = path;
        ListedByLibraryFolders = listedByLibraryFolders;
        IsInstallFolder = isInstallFolder;
        Manifests = Canonical.Sort(manifests, static (a, b) => Canonical.Ordinal(a.Path, b.Path));
    }

    /// <summary>
    /// The folder as a full path without a trailing separator (a drive root keeps its own, as in <c>F:\</c>).
    /// Two spellings that name the same folder on the platform (separator direction, a trailing or doubled
    /// separator, letter case on Windows) are one library.
    /// </summary>
    public string Path { get; }

    /// <summary>Whether <c>libraryfolders.vdf</c> names this folder.</summary>
    public bool ListedByLibraryFolders { get; }

    /// <summary>Whether this is the folder Steam is installed in.</summary>
    public bool IsInstallFolder { get; }

    /// <summary>The library's <c>steamapps\appmanifest_*.acf</c> files.</summary>
    public Reading<IReadOnlyList<SteamFile>> Manifests { get; }
}

public sealed record SteamFile(string Path, string Text);
