using System.Text.Json.Nodes;
using Mesura.Core.Apps;
using Mesura.Core.Config;
using Mesura.Core.Setup;
using Mesura.Setup.Windows;
using Mesura.Setup.Windows.Config;
using Mesura.Setup.Windows.Profiles;

namespace Mesura.Setup.Tests;

/// <summary>
/// Config import against real files in a temporary folder: the user's requirements that unknown settings survive,
/// that a re-run writes nothing, that a running app is left alone, and that every replaced file is backed up.
/// </summary>
public sealed class ConfigImportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mesura-setup-tests-").FullName;
    private readonly ConfigPortDefinition _brave = ConfigCatalog.ById("brave")!;
    private readonly string _prefs;

    public ConfigImportTests()
    {
        _prefs = Path.Combine(_dir, "Preferences");
        File.WriteAllText(_prefs, """{"brave":{"new_tab_page":{"show_clock":false}},"profile":{"name":"keep me"},"unknown":{"x":1}}""");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ConfigBundleFile Bundle() => ConfigBundleFile.Load(ConfigBundleFile.Save(
        [new AppConfigBundle("brave", "Brave", new Dictionary<string, string> { ["brave.ntp.showClock"] = "true" }, [])],
        "2026-10-18T00:00:00Z"));

    private IReadOnlyList<LocatedConfigFile> Locate(ConfigPortDefinition port) =>
        [new LocatedConfigFile("main", Format.Json, _prefs, File.Exists(_prefs))];

    private IReadOnlyList<ConfigImportOutcome> Import(SetupStore? store, string? runId, bool running = false) =>
        ConfigService.Import(Bundle(), [_brave], store, runId, Locate, _ => running);

    [Fact]
    public void Apply_writes_the_allowlisted_key_keeps_everything_else_and_backs_up_the_original()
    {
        var original = File.ReadAllText(_prefs);
        var store = new SetupStore(Path.Combine(_dir, "store"));

        var outcome = Import(store, "20261018T000000Z-test").Single();

        Assert.Equal(ConfigImportStatus.Applied, outcome.Status);
        var json = JsonNode.Parse(File.ReadAllText(_prefs))!;
        Assert.True(json["brave"]!["new_tab_page"]!["show_clock"]!.GetValue<bool>());
        Assert.Equal("keep me", json["profile"]!["name"]!.GetValue<string>());
        Assert.Equal(1, json["unknown"]!["x"]!.GetValue<int>());

        var backup = Path.Combine(store.HistoryFolder, "20261018T000000Z-test", "backups", outcome.Written.Single().BackupName);
        Assert.Equal(original, File.ReadAllText(backup));
        Assert.False(File.Exists(_prefs + ".mesura-tmp"));
    }

    [Fact]
    public void A_second_import_is_unchanged_and_writes_nothing()
    {
        var store = new SetupStore(Path.Combine(_dir, "store"));
        Import(store, "20261018T000000Z-first");
        var afterFirst = File.GetLastWriteTimeUtc(_prefs);

        var second = Import(store, "20261018T000001Z-second").Single();

        Assert.Equal(ConfigImportStatus.Unchanged, second.Status);
        Assert.Empty(second.Written);
        Assert.Equal(afterFirst, File.GetLastWriteTimeUtc(_prefs));
        Assert.False(Directory.Exists(Path.Combine(store.HistoryFolder, "20261018T000001Z-second")));
    }

    [Fact]
    public void A_running_app_is_skipped_and_its_file_left_alone()
    {
        var original = File.ReadAllText(_prefs);

        var outcome = Import(new SetupStore(Path.Combine(_dir, "store")), "20261018T000000Z-test", running: true).Single();

        Assert.Equal(ConfigImportStatus.SkippedAppRunning, outcome.Status);
        Assert.Equal(original, File.ReadAllText(_prefs));
    }

    [Fact]
    public void A_preview_reports_the_change_but_writes_nothing()
    {
        var original = File.ReadAllText(_prefs);

        var outcome = Import(store: null, runId: null).Single();

        Assert.Equal(ConfigImportStatus.Applied, outcome.Status);
        Assert.Contains("brave.ntp.showClock", outcome.Applied);
        Assert.Equal(original, File.ReadAllText(_prefs));
    }

    [Fact]
    public void A_missing_destination_file_is_reported_not_created()
    {
        File.Delete(_prefs);

        var outcome = Import(new SetupStore(Path.Combine(_dir, "store")), "20261018T000000Z-test").Single();

        Assert.Equal(ConfigImportStatus.Unchanged, outcome.Status);
        Assert.Contains("brave.ntp.showClock", outcome.DestinationMissing);
        Assert.False(File.Exists(_prefs));
    }
}

public sealed class SetupStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mesura-setup-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Pre_rename_history_is_read_in_place_never_moved_and_new_runs_go_to_the_Mesura_root()
    {
        // A run recorded before the rename (D022), with its backup, under the old data folder.
        var legacyRoot = Path.Combine(_dir, "FpsUtility", "Setup");
        var oldRun = Path.Combine(legacyRoot, "history", "20261018T080000Z-config-import");
        Directory.CreateDirectory(Path.Combine(oldRun, "backups"));
        var oldJson = SetupRun.Create("20261018T080000Z-config-import", "config-import", null, "a", "b", [], [])
            .ToJson().Replace("\"Mesura.SetupRun\"", "\"FpsUtility.SetupRun\"", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(oldRun, "run.json"), oldJson);
        File.WriteAllText(Path.Combine(oldRun, "backups", "brave-main"), "{}");

        var store = new SetupStore(Path.Combine(_dir, "Mesura", "Setup"), legacyRoot);
        var newRun = SetupRun.Create("20261018T090000Z-profile", "profile", "P", "a", "b", [], []);
        store.WriteRunRecord(newRun.Id, newRun.ToJson());
        store.WriteRunRecord("20261018T080000Z-config-import", oldJson); // the same id in both: listed once

        Assert.Equal([newRun.Id, "20261018T080000Z-config-import"], store.ListRuns());
        Assert.Equal("config-import", SetupRun.FromJson(store.ReadRunRecord("20261018T080000Z-config-import")!).Operation);
        Assert.StartsWith(store.HistoryFolder, store.FindRunFolder(newRun.Id)!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(oldJson, File.ReadAllText(Path.Combine(oldRun, "run.json"))); // old record untouched
        Assert.True(File.Exists(Path.Combine(oldRun, "backups", "brave-main")));   // old backup still there

        var onlyLegacy = new SetupStore(Path.Combine(_dir, "Empty"), legacyRoot);
        Assert.Equal(oldRun, onlyLegacy.FindRunFolder("20261018T080000Z-config-import"));
        Assert.False(Directory.Exists(onlyLegacy.HistoryFolder)); // reading created nothing
    }

    [Fact]
    public void History_lists_newest_first_and_round_trips_a_record()
    {
        var store = new SetupStore(_dir);
        var older = SetupRun.Create("20261017T090000Z-apps-install", "apps-install", null, "a", "b",
            [new InstallOutcome("brave", "Brave.Brave", InstallStatus.Succeeded, "Installed.")], []);
        var newer = SetupRun.Create("20261018T090000Z-profile", "profile", "Gaming", "a", "b", [], []);
        store.WriteRunRecord(older.Id, older.ToJson());
        store.WriteRunRecord(newer.Id, newer.ToJson());

        Assert.Equal([newer.Id, older.Id], store.ListRuns());
        var read = SetupRun.FromJson(store.ReadRunRecord(older.Id)!);
        Assert.Equal(InstallStatus.Succeeded, read.Apps.Single().Status);
        Assert.Contains("\"succeeded\"", store.ReadRunRecord(older.Id)!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("..\\outside")]
    [InlineData("a/b")]
    [InlineData("C:\\x")]
    public void Run_ids_and_backup_names_cannot_escape_the_store(string name)
    {
        var store = new SetupStore(_dir);
        Assert.Throws<ArgumentException>(() => store.WriteRunRecord(name, "{}"));
    }
}

public sealed class ProfileRunnerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mesura-setup-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class FakeWinget(params string[] installed) : IWingetRunner
    {
        public List<string> Ran { get; } = [];

        public bool IsAvailable() => true;

        public WingetRunResult List(AppPackage package) => installed.Contains(package.WingetId)
            ? new WingetRunResult(true, 0, $"Name Id\n----------\n{package.Name} {package.WingetId}")
            : new WingetRunResult(true, WingetOutput.NoApplicationsFound);

        public WingetRunResult Install(AppPackage package, Action<string>? onOutput, CancellationToken cancellationToken)
        {
            Ran.Add(package.WingetId);
            return new WingetRunResult(true, 0);
        }
    }

    [Fact]
    public void A_profile_installs_only_what_is_missing_and_records_the_run()
    {
        var profile = SetupProfile.Parse("""
            { "kind": "Mesura.SetupProfile", "schemaVersion": 1, "name": "Test",
              "bundles": ["media"], "apps": ["spotify"] }
            """, AppCatalog.Default);
        var winget = new FakeWinget("Spotify.Spotify");
        var installer = new AppInstallService(winget);
        var store = new SetupStore(_dir);

        var plan = ProfileRunner.Plan(profile, _dir, AppCatalog.Default, installer);
        var run = ProfileRunner.Run(plan, installer, store, () => new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero), null, CancellationToken.None);

        Assert.Equal(["spotify", "vlc", "obs"], plan.Apps.Select(a => a.Package.Id)); // bundle order, Spotify once
        Assert.Equal(["VideoLAN.VLC", "OBSProject.OBSStudio"], winget.Ran);
        Assert.True(run.Ok);
        Assert.Equal("20261018T090000Z-profile", run.Id);
        Assert.Equal(run.Id, store.ListRuns().Single());
    }

    [Fact]
    public void An_unreadable_config_bundle_is_reported_in_the_plan()
    {
        var profile = SetupProfile.Parse("""
            { "kind": "Mesura.SetupProfile", "schemaVersion": 1, "name": "Test", "configBundle": "missing.json" }
            """, AppCatalog.Default);

        var plan = ProfileRunner.Plan(profile, _dir, AppCatalog.Default, new AppInstallService(new FakeWinget()));

        Assert.NotNull(plan.BundleError);
        Assert.Null(plan.Bundle);
    }
}
