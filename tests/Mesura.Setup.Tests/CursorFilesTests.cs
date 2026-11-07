using Mesura.Core.Bonus;
using Mesura.Setup.Windows;
using Mesura.Setup.Windows.Bonus;

namespace Mesura.Setup.Tests;

/// <summary>The embedded cursor pack (0.7): every file the pack names is in the build, lands whole in Mesura's own folder, and goes away again.</summary>
public sealed class CursorFilesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mesura-cursor-files-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private CursorFiles Files => new(new SetupStore(_dir, legacyRoot: null));

    [Fact]
    public void The_build_carries_exactly_the_files_the_pack_names()
    {
        Assert.Equal([.. CursorPack.Files.Order(StringComparer.OrdinalIgnoreCase)], [.. CursorFiles.EmbeddedNames().Order(StringComparer.OrdinalIgnoreCase)]);
        foreach (var name in CursorPack.Files)
        {
            var bytes = CursorFiles.Embedded(name);
            Assert.NotNull(bytes);
            Assert.True(bytes!.Length > 100, name);
        }
    }

    [Fact]
    public void Install_writes_every_file_byte_for_byte_into_the_data_folder_and_remove_takes_them_away()
    {
        var files = Files;
        Assert.False(files.AllPresent());

        files.Install();

        Assert.True(files.AllPresent());
        Assert.StartsWith(Path.GetFullPath(_dir), Path.GetFullPath(files.Folder), StringComparison.OrdinalIgnoreCase);
        foreach (var name in CursorPack.Files)
        {
            Assert.Equal(CursorFiles.Embedded(name), File.ReadAllBytes(Path.Combine(files.Folder, name)));
        }

        Assert.DoesNotContain(Directory.EnumerateFiles(files.Folder), f => f.EndsWith(".mesura-tmp", StringComparison.Ordinal));

        files.Remove();

        Assert.False(files.AllPresent());
        Assert.False(Directory.Exists(files.Folder));
    }

    [Fact]
    public void A_damaged_file_is_not_present()
    {
        var files = Files;
        files.Install();
        File.WriteAllBytes(Path.Combine(files.Folder, "pointer.cur"), [1, 2, 3]);

        Assert.False(files.AllPresent());
    }

    [Fact]
    public void The_saved_cursors_are_written_read_and_deleted()
    {
        var files = Files;
        Assert.Null(files.ReadSaved());

        files.WriteSaved("{\"a\":1}");
        Assert.Equal("{\"a\":1}", files.ReadSaved());

        files.DeleteSaved();
        Assert.Null(files.ReadSaved());
    }
}
