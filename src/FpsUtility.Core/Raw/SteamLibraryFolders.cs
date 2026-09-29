namespace FpsUtility.Core.Raw;

/// <summary>
/// Reads the library folder paths out of Steam's <c>libraryfolders.vdf</c>, which the collector needs to know which
/// folders to read. Both layouts are read: the current one (<c>"0" { "path" "…" … }</c>) and the older one
/// (<c>"1" "D:\\SteamLibrary"</c>). Everything else in the file is left to the normaliser, which reads the file text
/// kept in the snapshot.
/// </summary>
public static class SteamLibraryFolders
{
    /// <exception cref="FormatException">The text isn't a <c>libraryfolders</c> KeyValues document.</exception>
    public static IReadOnlyList<string> ParsePaths(string vdf)
    {
        var root = Vdf.Parse(vdf);
        if (!root.Key.Equals("libraryfolders", StringComparison.OrdinalIgnoreCase) || !root.IsBlock)
        {
            throw new FormatException("The document's root key is not a \"libraryfolders\" block.");
        }

        var paths = new List<string>();
        foreach (var entry in root.Children)
        {
            // A library's key is a number; other keys (contentstatsid, TimeNextStatsReport) are not libraries.
            if (entry.Key.Length == 0 || !entry.Key.All(char.IsAsciiDigit))
            {
                continue;
            }

            // Older layout: the value is the path. Current layout: a block whose last "path" is the path.
            var path = entry.IsBlock
                ? entry.Children.LastOrDefault(child => !child.IsBlock && child.Key.Equals("path", StringComparison.OrdinalIgnoreCase))?.Text
                : entry.Text;

            if (path is not null)
            {
                paths.Add(path);
            }
        }

        return paths;
    }
}
