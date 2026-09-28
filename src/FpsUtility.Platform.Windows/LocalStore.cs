namespace FpsUtility.Platform.Windows;

/// <summary>
/// The only class that writes files (architecture §2.3). In Milestone 1 it writes one thing: a
/// snapshot the user asked for, to the path the user gave.
/// </summary>
public static class LocalStore
{
    /// <summary>Writes (or replaces) exactly the file the user named. No directory is created.</summary>
    public static void WriteUserExport(string path, byte[] contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

#pragma warning disable RS0030 // The one allowed file write: a user-requested export (architecture §2.3).
        File.WriteAllBytes(path, contents);
#pragma warning restore RS0030
    }
}
