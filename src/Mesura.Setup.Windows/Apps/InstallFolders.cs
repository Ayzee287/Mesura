namespace Mesura.Setup.Windows.Apps;

/// <summary>Which folders Mesura passes to an installer as its install location: a full local path on a drive that exists, never Windows' own folders or a bare drive root.</summary>
public static class InstallFolders
{
    /// <summary>The folder to pass, normalised, or <c>null</c> when none was chosen or the choice isn't acceptable.</summary>
    public static string? Valid(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || folder.Contains('"', StringComparison.Ordinal) || folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return null;
        }

        try
        {
            if (!Path.IsPathFullyQualified(folder) || folder.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return null; // relative paths and network shares are not offered
            }

            var full = Path.GetFullPath(folder).TrimEnd('\\');
            var root = Path.GetPathRoot(full)?.TrimEnd('\\');
            if (string.IsNullOrEmpty(root) || string.Equals(full, root, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(root + "\\"))
            {
                return null;
            }

            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
            return full.Equals(windows, StringComparison.OrdinalIgnoreCase) || full.StartsWith(windows + @"\", StringComparison.OrdinalIgnoreCase) ? null : full;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }
    }
}
