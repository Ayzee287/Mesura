using System.Text;

namespace Mesura.Updating;

/// <summary>
/// The one writer of update files (architecture §2.3): everything lands in <c>&lt;data folder&gt;\updates\</c>, never in the
/// program folder. A download is written as <c>*.partial</c> and renamed only when it is whole, so a half file is never taken
/// for a package.
/// </summary>
#pragma warning disable RS0030 // The one file writer of the update component: staging under the data folder's updates\ folder (class remarks).
public sealed class UpdateStore
{
    public const string ManifestFile = "update-manifest.json";
    public const string SignatureFile = "update-manifest.sig";
    public const string ResultFile = "update-result.json";

    public UpdateStore(string dataFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataFolder);
        Folder = Path.GetFullPath(Path.Combine(dataFolder, "updates"));
    }

    public string Folder { get; }

    public string PathOf(string name) => Path.Combine(Folder, name);

    public void Ensure() => Directory.CreateDirectory(Folder);

    public bool Exists(string name) => File.Exists(PathOf(name));

    public long Length(string name) => File.Exists(PathOf(name)) ? new FileInfo(PathOf(name)).Length : 0;

    public byte[]? Read(string name) => File.Exists(PathOf(name)) ? File.ReadAllBytes(PathOf(name)) : null;

    public void Write(string name, byte[] content)
    {
        Ensure();
        if (Environment.IsPrivilegedProcess && AnyRedirected(Folder, PathOf(name), PathOf(name) + ".tmp"))
        {
            // The updates folder is the user's: a link planted there would send an elevated updater's write somewhere else (docs/security.md).
            throw new IOException("The updates folder or a file in it is a link, not a plain file; nothing was written.");
        }

        var temp = PathOf(name) + ".tmp";
        File.WriteAllBytes(temp, content);
        File.Move(temp, PathOf(name), overwrite: true);
    }

    internal static bool AnyRedirected(params string[] paths) => paths.Any(IsRedirected);

    private static bool IsRedirected(string path) => (File.Exists(path) || Directory.Exists(path)) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    public void WriteText(string name, string text) => Write(name, new UTF8Encoding(false).GetBytes(text));

    public FileStream OpenPartial(string name, bool append)
    {
        Ensure();
        return new FileStream(PathOf(name), append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read);
    }

    public FileStream OpenRead(string name) => new(PathOf(name), FileMode.Open, FileAccess.Read, FileShare.Read);

    public void Commit(string partial, string final) => File.Move(PathOf(partial), PathOf(final), overwrite: true);

    public void Delete(string name)
    {
        try
        {
            File.Delete(PathOf(name));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left for the next cleanup.
        }
    }

    /// <summary>Copies a file into the updates folder (the updater's own copy of the command line).</summary>
    public string CopyIn(string source, string relativeTarget)
    {
        var target = PathOf(relativeTarget);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: true);
        return target;
    }

    /// <summary>The free space of the drive the updates folder is on, or <c>null</c> when it can't be read.</summary>
    public long? FreeBytes()
    {
        try
        {
            Ensure();
            return new DriveInfo(Path.GetPathRoot(Folder)!).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Removes packages, partial files and updater copies except the named files; the result file and logs stay.</summary>
    public void CleanExcept(params string[] keep)
    {
        if (!Directory.Exists(Folder))
        {
            return;
        }

        var keepSet = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase) { ResultFile };
        foreach (var path in Directory.EnumerateFileSystemEntries(Folder))
        {
            var name = Path.GetFileName(path);
            if (keepSet.Contains(name) || name.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                else
                {
                    File.Delete(path);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A running updater copy or a locked file: next time.
            }
        }
    }
}
