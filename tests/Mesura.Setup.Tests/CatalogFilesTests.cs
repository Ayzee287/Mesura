using Mesura.Core.Windows;
using Mesura.Setup.Windows;

namespace Mesura.Setup.Tests;

/// <summary>Which edited catalog wins, and how a broken one is reported (all under a temporary folder).</summary>
public sealed class CatalogFilesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mesura-catalogs-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Folder(string name)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static LoadedCatalog<WindowsCatalog> Load(params string[] folders) =>
        CatalogFiles.Load(WindowsCatalog.FileName, WindowsCatalog.Default, WindowsCatalog.Parse, folders);

    [Fact]
    public void The_shared_copy_wins_over_the_one_next_to_the_executable_and_no_copy_means_built_in()
    {
        var shared = Folder("shared");
        var exe = Folder("exe");
        var text = WindowsCatalog.BuiltInJson();
        File.WriteAllText(Path.Combine(exe, WindowsCatalog.FileName), text);

        Assert.Equal(Path.Combine(exe, WindowsCatalog.FileName), Load(shared, exe).Source);

        File.WriteAllText(Path.Combine(shared, WindowsCatalog.FileName), text);
        Assert.Equal(Path.Combine(shared, WindowsCatalog.FileName), Load(shared, exe).Source);

        var none = Load(Folder("empty"));
        Assert.Equal("built-in", none.Source);
        Assert.Same(WindowsCatalog.Default, none.Catalog);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ \"kind\": \"Mesura.WindowsCatalog\", \"schemaVersion\": 1, \"operations\": [ null ] }")]
    [InlineData("not json")]
    public void A_broken_edited_copy_is_reported_never_replaced_by_the_built_in_one(string text)
    {
        var folder = Folder("broken");
        File.WriteAllText(Path.Combine(folder, WindowsCatalog.FileName), text);

        var loaded = Load(folder);

        Assert.Null(loaded.Catalog);
        Assert.StartsWith("Could not load", loaded.Error, StringComparison.Ordinal);
    }
}
