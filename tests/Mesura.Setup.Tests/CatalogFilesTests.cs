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
    // --- an elevated Mesura never reads an edited copy (docs/security.md) ---

    private static LoadedCatalog<Mesura.Core.Apps.AppCatalog> LoadApps(string folder) =>
        CatalogFiles.Load(Mesura.Core.Apps.AppCatalog.FileName, Mesura.Core.Apps.AppCatalog.Default, Mesura.Core.Apps.AppCatalog.Parse, [folder]);

    [Fact]
    public void An_elevated_Mesura_uses_the_built_in_catalogs_and_remembers_which_edited_copy_it_left_alone()
    {
        var folder = Folder("edited");
        File.WriteAllText(Path.Combine(folder, WindowsCatalog.FileName), WindowsCatalog.BuiltInJson());
        File.WriteAllText(Path.Combine(folder, Mesura.Core.Apps.AppCatalog.FileName), Mesura.Core.Apps.AppCatalog.BuiltInJson());

        var windows = CatalogFiles.IgnoreEditedWhenElevated(Load(folder), WindowsCatalog.Default, elevated: true);
        var apps = CatalogFiles.IgnoreEditedWhenElevated(LoadApps(folder), Mesura.Core.Apps.AppCatalog.Default, elevated: true);

        Assert.Same(WindowsCatalog.Default, windows.Catalog);
        Assert.Equal(("built-in", true, Path.Combine(folder, WindowsCatalog.FileName)), (windows.Source, windows.IgnoredWhileElevated, windows.SetAside));
        Assert.Same(Mesura.Core.Apps.AppCatalog.Default, apps.Catalog);
        Assert.True(apps.IgnoredWhileElevated);
        Assert.Contains("administrator", CatalogFiles.SetAsideNote(WindowsCatalog.FileName, windows), StringComparison.Ordinal);
    }

    [Fact]
    public void Without_elevation_the_edited_copy_is_used_as_before()
    {
        var folder = Folder("edited2");
        File.WriteAllText(Path.Combine(folder, WindowsCatalog.FileName), WindowsCatalog.BuiltInJson());

        var loaded = CatalogFiles.IgnoreEditedWhenElevated(Load(folder), WindowsCatalog.Default, elevated: false);

        Assert.Equal(Path.Combine(folder, WindowsCatalog.FileName), loaded.Source);
        Assert.False(loaded.IgnoredWhileElevated);
    }

    [Fact]
    public void A_broken_edited_copy_cannot_block_or_steer_an_elevated_Mesura()
    {
        var folder = Folder("broken-elevated");
        File.WriteAllText(Path.Combine(folder, WindowsCatalog.FileName), "not json");

        var loaded = CatalogFiles.IgnoreEditedWhenElevated(Load(folder), WindowsCatalog.Default, elevated: true);

        Assert.Null(loaded.Error);
        Assert.Same(WindowsCatalog.Default, loaded.Catalog);
    }

    [Fact]
    public void The_built_in_catalog_is_left_as_it_is_when_elevated()
    {
        var loaded = CatalogFiles.IgnoreEditedWhenElevated(Load(Folder("none")), WindowsCatalog.Default, elevated: true);

        Assert.False(loaded.IgnoredWhileElevated);
        Assert.Null(loaded.SetAside);
    }
}
