using System.Reflection;
using System.Runtime.InteropServices;
using Mesura.Core.Bonus;

namespace Mesura.Setup.Windows.Bonus;

/// <summary>
/// The cursor pack's files for the signed-in user. The pack is embedded in Mesura (<c>Bonus\Cursors</c>, 17 small files, attribution in
/// THIRD-PARTY-NOTICES.md); installing copies it into Mesura's own folder through <see cref="SetupStore"/>, the setup component's one file writer.
/// </summary>
public sealed class CursorFiles(SetupStore store) : ICursorFiles
{
    private const string ResourcePrefix = "Mesura.Setup.Bonus.Cursors.";

    public string Folder => store.BonusCursorFolder(CursorPack.Id);

    public bool AllPresent() =>
        CursorPack.Files.All(name => store.BonusFileLength(CursorPack.Id, name) == Embedded(name)?.Length);

    public void Install()
    {
        foreach (var name in CursorPack.Files)
        {
            var bytes = Embedded(name) ?? throw new InvalidOperationException($"This copy of Mesura doesn't contain the cursor file {name}.");
            store.WriteBonusFile(CursorPack.Id, name, bytes);
        }
    }

    public void Remove() => store.RemoveBonusFolder(CursorPack.Id);

    public string? ReadSaved() => store.ReadBonusSaved();

    public void WriteSaved(string json) => store.WriteBonusSaved(json);

    public void DeleteSaved() => store.DeleteBonusSaved();

    /// <summary>One file of the embedded pack as a stream (the Configurations card draws a few of the cursors from it), or <c>null</c> when this build doesn't carry it.</summary>
    public static Stream? OpenEmbedded(string name) => typeof(CursorFiles).Assembly.GetManifestResourceStream(ResourcePrefix + name);

    /// <summary>One file of the embedded pack, or <c>null</c> when this build doesn't carry it.</summary>
    internal static byte[]? Embedded(string name)
    {
        using var stream = typeof(CursorFiles).Assembly.GetManifestResourceStream(ResourcePrefix + name);
        if (stream is null)
        {
            return null;
        }

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>The names of the cursor files this build carries (a test and a build check that the pack is whole).</summary>
    internal static IReadOnlyList<string> EmbeddedNames() =>
        [.. typeof(CursorFiles).Assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)).Select(n => n[ResourcePrefix.Length..])];
}

/// <summary>
/// Asks Windows to load the cursors from the registry again (<c>SystemParametersInfo(SPI_SETCURSORS)</c>): it writes nothing itself, it re-reads what the
/// registry says. It reloads the cursors of the user this process runs as, so it is only used when that user is the signed-in one.
/// </summary>
public sealed partial class CursorSession(bool sameUser) : ICursorSession
{
    private const uint SpiSetCursors = 0x0057;
    private const uint SpifSendChange = 0x0002;

    public bool Reload() => sameUser && SystemParametersInfo(SpiSetCursors, 0, IntPtr.Zero, SpifSendChange);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint param, IntPtr pvParam, uint winIni);
}
