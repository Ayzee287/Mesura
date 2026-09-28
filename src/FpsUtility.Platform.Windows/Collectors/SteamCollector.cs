using FpsUtility.Core.Privacy;
using FpsUtility.Core.Raw;
using Microsoft.Win32;

namespace FpsUtility.Platform.Windows.Collectors;

/// <summary>
/// <c>steam</c>: Steam's <c>libraryfolders.vdf</c> and every library's <c>appmanifest_*.acf</c>, read-only,
/// as text (architecture §4.4, §7). The install path comes from <c>HKCU\Software\Valve\Steam\SteamPath</c>.
/// Text and paths are scrubbed before they leave this class (§13).
/// </summary>
internal static class SteamCollector
{
    public const string Source = @"registry HKCU\Software\Valve\Steam\SteamPath; files steamapps\libraryfolders.vdf, steamapps\appmanifest_*.acf";

    public static SteamData Collect(UserProfileRedactor profile)
    {
        string? steamPath;
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
        {
            steamPath = key?.GetValue("SteamPath") as string;
        }

        if (string.IsNullOrWhiteSpace(steamPath))
        {
            throw new SourceUnavailableException(MissingReason.NotPresent, @"Steam is not installed for this user: HKCU\Software\Valve\Steam\SteamPath is not set");
        }

        var scrubber = new SteamTextScrubber(profile);
        var installPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(steamPath));
        var foldersPath = Path.Combine(installPath, "steamapps", "libraryfolders.vdf");
        var foldersText = ReadText(foldersPath);

        return new SteamData(
            installPath: profile.Redact(installPath),
            libraryFolders: foldersText.IsKnown
                ? Reading.Known(new SteamFile(profile.Redact(foldersPath), scrubber.Scrub(foldersText.Value!)))
                : Reading.Missing<SteamFile>(foldersText.Missing!.Value, foldersText.Detail!),
            libraries: ReadLibraries(installPath, foldersText, profile, scrubber));
    }

    private static Reading<IReadOnlyList<SteamLibrary>> ReadLibraries(string installPath, Reading<string> foldersText, UserProfileRedactor profile, SteamTextScrubber scrubber)
    {
        if (!foldersText.IsKnown)
        {
            return Reading.Missing<IReadOnlyList<SteamLibrary>>(foldersText.Missing!.Value, $"The library list could not be read: {foldersText.Detail}");
        }

        IReadOnlyList<string> listed;
        try
        {
            listed = SteamLibraryFolders.ParsePaths(foldersText.Value!);
        }
        catch (FormatException ex)
        {
            return Reading.Missing<IReadOnlyList<SteamLibrary>>(MissingReason.ParseFailed, $"libraryfolders.vdf: {ex.Message}");
        }

        if (listed.FirstOrDefault(path => !Path.IsPathFullyQualified(path)) is { } relative)
        {
            return Reading.Missing<IReadOnlyList<SteamLibrary>>(MissingReason.ParseFailed, profile.Redact($"libraryfolders.vdf lists a library path that is not absolute: {relative}"));
        }

        IReadOnlyList<(string Path, bool Listed, bool IsInstallFolder)> folders;
        try
        {
            folders = LibraryFolders(installPath, listed);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            // A listed path Windows can't use (a NUL character, too long): the list is unusable, the rest of the source is not.
            return Reading.Missing<IReadOnlyList<SteamLibrary>>(MissingReason.ParseFailed, $"libraryfolders.vdf lists a library path Windows can't use: {ex.GetType().Name}");
        }

        return Reading.Known<IReadOnlyList<SteamLibrary>>(
            [.. folders.Select(folder => new SteamLibrary(
                profile.Redact(folder.Path),
                folder.Listed,
                folder.IsInstallFolder,
                ReadManifests(folder.Path, profile, scrubber)))]);
    }

    /// <summary>
    /// The folders to read as libraries: each folder <c>libraryfolders.vdf</c> names, and Steam's install folder
    /// (older Steam doesn't list it), each once, with why it is included. A folder is identified by its full
    /// path without a trailing separator, compared case-insensitively as Windows compares paths; that merges
    /// spellings that differ only in separator direction, a trailing separator, a doubled separator or letter
    /// case. Junctions and symbolic links are not resolved, so two such spellings of one folder stay two
    /// entries. (<see cref="Path.GetFullPath(string)"/> does ask the file system for the long form of an 8.3
    /// short name that exists; it changes nothing.) The kept spelling is the first one met, install folder first.
    /// </summary>
    internal static IReadOnlyList<(string Path, bool Listed, bool IsInstallFolder)> LibraryFolders(string installPath, IEnumerable<string> listed)
    {
        var folders = new List<(string Path, bool Listed, bool IsInstallFolder)>();

        void Add(string path, bool isListed, bool isInstall)
        {
            var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var index = folders.FindIndex(folder => string.Equals(folder.Path, canonical, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                folders.Add((canonical, isListed, isInstall));
            }
            else
            {
                var existing = folders[index];
                folders[index] = (existing.Path, existing.Listed || isListed, existing.IsInstallFolder || isInstall);
            }
        }

        Add(installPath, isListed: false, isInstall: true);
        foreach (var path in listed)
        {
            Add(path, isListed: true, isInstall: false);
        }

        return folders;
    }

    private static Reading<IReadOnlyList<SteamFile>> ReadManifests(string libraryPath, UserProfileRedactor profile, SteamTextScrubber scrubber)
    {
        var steamapps = Path.Combine(libraryPath, "steamapps");
        try
        {
            var manifests = Directory.EnumerateFiles(steamapps, "appmanifest_*.acf", SearchOption.TopDirectoryOnly)
                .Where(file => file.EndsWith(".acf", StringComparison.OrdinalIgnoreCase)) // "*.acf" also matches longer extensions
                .Select(file => new SteamFile(profile.Redact(file), scrubber.Scrub(File.ReadAllText(file))))
                .ToList();

            return Reading.Known<IReadOnlyList<SteamFile>>(manifests);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Reading.Missing<IReadOnlyList<SteamFile>>(ReasonFor(ex), profile.Redact($"{ex.GetType().Name}: {ex.Message}"));
        }
    }

    private static Reading<string> ReadText(string path)
    {
        try
        {
            return Reading.Known(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Reading.Missing<string>(ReasonFor(ex), $"{ex.GetType().Name} reading {Path.GetFileName(path)}");
        }
    }

    private static MissingReason ReasonFor(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => MissingReason.NotPresent,
        UnauthorizedAccessException => MissingReason.AccessDenied,
        _ => MissingReason.SourceFailed,
    };
}
