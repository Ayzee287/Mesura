using Mesura.Core.Apps;
using Mesura.Core.Config;
using Mesura.Core.Setup;
using Mesura.Core.Windows;
using Mesura.Setup.Windows;
using Mesura.Setup.Windows.Config;

namespace Mesura.Setup.Tests;

/// <summary>
/// The setup side of the fixes from the 2026-10-23 acceptance review, against temporary files only: an edited catalog
/// an older Mesura made is set aside (visibly) instead of quietly undoing the audit, and an undo puts an app's settings
/// file back only while it is still exactly what the run wrote.
/// </summary>
public sealed class AcceptanceReviewTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mesura-acceptance-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    private LoadedCatalog<WindowsCatalog> LoadWindows() =>
        CatalogFiles.Load(WindowsCatalog.FileName, WindowsCatalog.Default, WindowsCatalog.Parse, [_dir], WindowsCatalog.CurrentSchema);

    [Fact]
    public void An_edited_catalog_from_before_the_audit_is_set_aside_and_the_built_in_one_used()
    {
        var path = Write(WindowsCatalog.FileName, """
            {
              // An edited copy made by Mesura 0.1.0 (schema 1): no tiers, so no Recommended or Maximum performance.
              "kind": "Mesura.WindowsCatalog", "schemaVersion": 1,
              "operations": [], "presets": []
            }
            """);

        var loaded = LoadWindows();

        Assert.Same(WindowsCatalog.Default, loaded.Catalog);
        Assert.Equal("built-in", loaded.Source);
        Assert.Equal(path, loaded.SetAside);
        Assert.Null(loaded.Error);
        Assert.Contains("older Mesura", CatalogFiles.SetAsideNote(WindowsCatalog.FileName, loaded), StringComparison.Ordinal);
        Assert.Empty(CatalogFiles.NewSinceCopied(loaded)); // the built-in catalog is complete
    }

    [Fact]
    public void An_old_copy_the_new_rules_would_refuse_is_still_only_set_aside()
    {
        // Not a valid catalog any more (an operation with no id), but it is an old one: it must not block Mesura.
        Write(WindowsCatalog.FileName, """{"kind":"Mesura.WindowsCatalog","schemaVersion":1,"operations":[{"name":"x"}],"presets":[]}""");
        var loaded = LoadWindows();
        Assert.Same(WindowsCatalog.Default, loaded.Catalog);
        Assert.NotNull(loaded.SetAside);

        // A copy that declares no schema at all is treated as old too.
        Write(WindowsCatalog.FileName, """{"kind":"Mesura.WindowsCatalog","operations":[],"presets":[]}""");
        Assert.NotNull(LoadWindows().SetAside);
    }

    [Fact]
    public void An_edited_copy_of_the_current_schema_is_used_and_its_problems_reported()
    {
        var path = Write(WindowsCatalog.FileName, WindowsCatalog.BuiltInJson());
        var loaded = LoadWindows();
        Assert.Equal(path, loaded.Source);
        Assert.Null(loaded.SetAside);
        Assert.Equal(WindowsCatalog.Default.Operations.Count, loaded.Catalog!.Operations.Count);

        // A broken current copy is still an error, never silently replaced by the built-in catalog.
        Write(WindowsCatalog.FileName, """{"kind":"Mesura.WindowsCatalog","schemaVersion":3,"operations":[{"name":"x"}],"presets":[]}""");
        var broken = LoadWindows();
        Assert.Null(broken.Catalog);
        Assert.NotNull(broken.Error);
        Assert.Null(broken.SetAside);
    }

    [Fact]
    public void A_schema_that_isnt_a_number_is_reported_not_thrown()
    {
        Write(WindowsCatalog.FileName, """{"kind":"Mesura.WindowsCatalog","schemaVersion":"2","operations":[],"presets":[]}""");
        var loaded = LoadWindows(); // must not throw: an edited copy with a mistake is reported, and Mesura still starts
        Assert.Null(loaded.Catalog);
        Assert.NotNull(loaded.Error);
    }

    [Fact]
    public void Without_a_signed_in_user_only_an_unelevated_process_counts_as_the_users_own()
    {
        var nobody = new UserContext(null, "", "Couldn't tell which user is signed in.");
        Assert.True(ProcessUser.Is(nobody, elevated: false));  // a remote shell: its own account is the only one involved
        Assert.False(ProcessUser.Is(nobody, elevated: true));  // elevated, it can't be told whose settings these would be
        Assert.False(ProcessUser.Is(nobody));
    }

    [Fact]
    public void An_app_catalog_from_before_the_redesign_is_set_aside_too()
    {
        // Same ids as today, but without listed, context and detector: nothing in the ids would tell it apart.
        var path = Write(AppCatalog.FileName, """{"kind":"Mesura.AppCatalog","schemaVersion":1,"apps":[{"id":"7zip","wingetId":"7zip.7zip","name":"7-Zip","category":"Utility"}],"bundles":[]}""");
        var loaded = CatalogFiles.Load(AppCatalog.FileName, AppCatalog.Default, AppCatalog.Parse, [_dir], AppCatalog.CurrentSchema);
        Assert.Same(AppCatalog.Default, loaded.Catalog);
        Assert.Equal(path, loaded.SetAside);
    }

    private readonly ConfigPortDefinition _brave = ConfigCatalog.ById("brave")!;

    private string Prefs => Path.Combine(_dir, "Preferences");

    private IReadOnlyList<LocatedConfigFile> Locate(ConfigPortDefinition port) =>
        [new LocatedConfigFile("main", Format.Json, Prefs, File.Exists(Prefs))];

    private (SetupStore Store, ConfigImportOutcome Import) ImportDefaults()
    {
        File.WriteAllText(Prefs, """{"brave":{"brave_ads":{"sponsored":{"enabled":true}}},"profile":{"name":"keep"}}""");
        var store = new SetupStore(Path.Combine(_dir, "store"));
        var import = ConfigService.Import(ConfigDefaults.Bundle("2026-10-23T00:00:00Z"), [_brave], store, "20261023T000000Z-config-import", Locate, _ => false).Single();
        Assert.Equal(ConfigImportStatus.Applied, import.Status);
        return (store, import);
    }

    [Fact]
    public void An_import_records_a_fingerprint_of_what_it_wrote()
    {
        var (_, import) = ImportDefaults();
        Assert.Equal(ConfigService.Fingerprint(File.ReadAllBytes(Prefs)), import.Written.Single().Sha256);
    }

    [Fact]
    public void Undo_leaves_a_file_changed_since_the_import_as_it_is()
    {
        var (store, import) = ImportDefaults();
        // The app ran since and rewrote its file (or the user changed a setting): the undo must not roll that back.
        const string Later = """{"brave":{"brave_ads":{"sponsored":{"enabled":false}}},"profile":{"name":"changed later"}}""";
        File.WriteAllText(Prefs, Later);

        var undo = ConfigService.RestoreBackups(_brave, "20261023T000000Z-config-import", import.Written, store, "20261023T000100Z-config-restore", Locate, _ => false);

        Assert.Equal(ConfigImportStatus.ChangedSince, undo.Status);
        Assert.Empty(undo.Written);
        Assert.Equal(Later, File.ReadAllText(Prefs));
    }

    [Fact]
    public void Undo_of_a_record_without_a_fingerprint_leaves_the_file_alone()
    {
        var (store, import) = ImportDefaults();
        var imported = File.ReadAllText(Prefs);
        var legacy = import.Written.Select(w => w with { Sha256 = null }).ToList(); // as written by Mesura 0.1.0

        var undo = ConfigService.RestoreBackups(_brave, "20261023T000000Z-config-import", legacy, store, "20261023T000100Z-config-restore", Locate, _ => false);

        Assert.Equal(ConfigImportStatus.ChangedSince, undo.Status);
        Assert.Equal(imported, File.ReadAllText(Prefs));
    }

    [Fact]
    public void A_restore_is_fingerprinted_so_it_can_be_undone_the_same_way()
    {
        var (store, import) = ImportDefaults();
        var imported = File.ReadAllBytes(Prefs);
        var undo = ConfigService.RestoreBackups(_brave, "20261023T000000Z-config-import", import.Written, store, "20261023T000100Z-config-restore", Locate, _ => false);
        Assert.Equal(ConfigImportStatus.Applied, undo.Status);
        Assert.Equal(ConfigService.Fingerprint(File.ReadAllBytes(Prefs)), undo.Written.Single().Sha256);

        var redo = ConfigService.RestoreBackups(_brave, "20261023T000100Z-config-restore", undo.Written, store, "20261023T000200Z-config-restore", Locate, _ => false);
        Assert.Equal(ConfigImportStatus.Applied, redo.Status);
        Assert.Equal(imported, File.ReadAllBytes(Prefs));
    }

    [Fact]
    public void An_undo_that_puts_one_file_back_and_leaves_a_changed_one_says_it_was_partial()
    {
        // A port with two files, like Steam's (the client's and the account's).
        var port = new ConfigPortDefinition("twofile", "Two files",
            [new ConfigSource("a", Format.Json, ""), new ConfigSource("b", Format.Json, "")],
            [
                new ConfigSetting("twofile.x", "X", "/x", ConfigScope.Application, TransferConfidence.Reliable, "", Source: "a"),
                new ConfigSetting("twofile.y", "Y", "/y", ConfigScope.Application, TransferConfidence.Reliable, "", Source: "b"),
            ],
            []);
        var a = Path.Combine(_dir, "a.json");
        var b = Path.Combine(_dir, "b.json");
        File.WriteAllText(a, """{"x":1}""");
        File.WriteAllText(b, """{"y":1}""");
        IReadOnlyList<LocatedConfigFile> Two(ConfigPortDefinition _) =>
            [new LocatedConfigFile("a", Format.Json, a, File.Exists(a)), new LocatedConfigFile("b", Format.Json, b, File.Exists(b))];
        var values = new Dictionary<string, string> { ["twofile.x"] = "2", ["twofile.y"] = "2" };
        var bundle = ConfigBundleFile.Load(ConfigBundleFile.Save([new AppConfigBundle("twofile", "Two files", values, [])], "2026-10-23T00:00:00Z"));
        var store = new SetupStore(Path.Combine(_dir, "store"));
        var import = ConfigService.Import(bundle, [port], store, "20261023T000000Z-config-import", Two, _ => false).Single();
        Assert.Equal(2, import.Written.Count);
        File.WriteAllText(b, """{"y":3}"""); // changed since

        var undo = ConfigService.RestoreBackups(port, "20261023T000000Z-config-import", import.Written, store, "20261023T000100Z-config-restore", Two, _ => false);

        Assert.Equal(ConfigImportStatus.ChangedSince, undo.Status);
        Assert.Equal("a", undo.Written.Single().SourceId);
        Assert.Equal("""{"x":1}""", File.ReadAllText(a));
        Assert.Equal("""{"y":3}""", File.ReadAllText(b));
    }

    [Fact]
    public void A_bundle_value_the_file_cannot_take_fails_that_app_without_throwing()
    {
        const string Original = """{"brave":{"brave_ads":{"sponsored":{"enabled":true}}}}""";
        File.WriteAllText(Prefs, Original);
        var edited = new AppConfigBundle("brave", "Brave", new Dictionary<string, string> { ["brave.ads.sponsored"] = "yes please" }, []);
        var bundle = ConfigBundleFile.Load(ConfigBundleFile.Save([edited], "2026-10-23T00:00:00Z"));

        var outcome = ConfigService.Import(bundle, [_brave], new SetupStore(Path.Combine(_dir, "store")), "20261023T000000Z-config-import", Locate, _ => false).Single();

        Assert.Equal(ConfigImportStatus.Failed, outcome.Status);
        Assert.Empty(outcome.Written);
        Assert.Equal(Original, File.ReadAllText(Prefs));
    }
}
