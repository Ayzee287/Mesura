using System.Text;

namespace Mesura.Setup.Windows;

// The bonus cursor pack (0.7): the pack's files for this user and what the bonus saved about the cursors from before, under <root>\bonus\cursors. Mesura's own folder only; nothing is written outside the root.
public sealed partial class SetupStore
{
    /// <summary>Where the cursor pack's files live for this user: <c>bonus\cursors\&lt;pack id&gt;</c> under the root.</summary>
    public string BonusCursorFolder(string packId) => Path.Combine(Root, "bonus", "cursors", PlainName(packId));

    private string BonusSavedPath => Path.Combine(Root, "bonus", "cursors", "saved.json");

    /// <summary>Writes one file of a pack, whole and atomically (a temporary file, then a move).</summary>
    public void WriteBonusFile(string packId, string name, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var folder = BonusCursorFolder(packId);
        var path = Path.Combine(folder, PlainName(name));
        var temp = path + TempSuffix;
#pragma warning disable RS0030 // The setup component's one file writer: a bonus pack's files in Mesura's own folder (class remarks).
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(temp, content);
        File.Move(temp, path, overwrite: true);
#pragma warning restore RS0030
    }

    /// <summary>The length of a pack's file on disk, or <c>null</c> when it isn't there.</summary>
    public long? BonusFileLength(string packId, string name)
    {
        var path = Path.Combine(BonusCursorFolder(packId), PlainName(name));
        return File.Exists(path) ? new FileInfo(path).Length : null;
    }

    /// <summary>Deletes a pack's files and its folder. A file Windows still holds is left (the next call removes it); nothing here throws for that.</summary>
    public void RemoveBonusFolder(string packId)
    {
        var folder = BonusCursorFolder(packId);
        if (!Directory.Exists(folder))
        {
            return;
        }

#pragma warning disable RS0030 // The setup component's one file writer: removing a bonus pack it wrote (class remarks).
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Windows may hold a cursor file until the session has reloaded its cursors.
            }
        }

        try
        {
            Directory.Delete(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not empty yet: a file is still held.
        }
#pragma warning restore RS0030
    }

    /// <summary>What the cursor bonus saved from before, or <c>null</c>.</summary>
    public string? ReadBonusSaved() => File.Exists(BonusSavedPath) ? File.ReadAllText(BonusSavedPath) : null;

    public void WriteBonusSaved(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var path = BonusSavedPath;
        var temp = path + TempSuffix;
#pragma warning disable RS0030 // The setup component's one file writer: the bonus's own saved state (class remarks).
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(temp, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json));
        File.Move(temp, path, overwrite: true);
#pragma warning restore RS0030
    }

    public void DeleteBonusSaved()
    {
#pragma warning disable RS0030 // The setup component's one file writer: the bonus's own saved state (class remarks).
        File.Delete(BonusSavedPath);
#pragma warning restore RS0030
    }
}
