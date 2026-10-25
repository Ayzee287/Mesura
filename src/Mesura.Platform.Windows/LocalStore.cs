using System.Text.RegularExpressions;

namespace Mesura.Platform.Windows;

/// <summary>
/// The only class that writes files (architecture §2.3). It writes what the user asked for, where the user said: a
/// snapshot or fixture to the path the user gave, and a Track A capture or acceptance inside the folder the user named.
/// </summary>
public static partial class LocalStore
{
    /// <summary>
    /// Writes (or replaces) exactly the file the user named. No directory is created. The bytes go to a temporary file
    /// beside it first and then replace it in one step, so an interrupted write (a USB stick pulled, a full disk) leaves
    /// the previous file whole instead of an empty one.
    /// </summary>
    public static void WriteUserExport(string path, byte[] contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

        var temp = path + ".mesura-tmp";
#pragma warning disable RS0030 // The one allowed file write: a user-requested export (architecture §2.3).
        File.WriteAllBytes(temp, contents);
        File.Move(temp, path, overwrite: true);
#pragma warning restore RS0030
    }

    /// <summary>
    /// Creates folders for a user-requested export inside a folder the user named (architecture §2.3, §4.7): a Track A
    /// session's folders under the kit's output folder, or an accepted session's folder under the corpus. The root must
    /// exist already. Each segment is one plain folder name (letters, digits, <c>.</c>, <c>_</c>, <c>-</c>; no separator,
    /// no <c>..</c>), so nothing outside the root can be created.
    /// </summary>
    /// <returns>The full path of the innermost folder.</returns>
    /// <exception cref="ArgumentException">A segment isn't a plain folder name.</exception>
    /// <exception cref="DirectoryNotFoundException">The root doesn't exist.</exception>
    public static string CreateExportFolder(string root, params IReadOnlyList<string> segments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(segments);

        var full = Path.GetFullPath(root);
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"The folder '{root}' doesn't exist.");
        }

        var path = full;
        foreach (var segment in segments)
        {
            if (segment is null || !FolderName().IsMatch(segment) || segment.Trim('.').Length == 0)
            {
                throw new ArgumentException($"'{segment}' is not a plain folder name.", nameof(segments));
            }

            path = Path.Combine(path, segment);
        }

#pragma warning disable RS0030 // Folders for a user-requested export, inside the folder the user named (architecture §2.3).
        Directory.CreateDirectory(path);
#pragma warning restore RS0030
        return path;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex FolderName();
}
