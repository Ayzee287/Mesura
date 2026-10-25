using Mesura.Core.Apps;
using Mesura.Core.Config;
using Mesura.Core.Setup;
using Mesura.Core.Windows;
using Mesura.Setup.Windows;
using Mesura.Setup.Windows.Config;
using Mesura.Setup.Windows.Profiles;

namespace Mesura.Setup.Tests;

/// <summary>
/// A profile run on a synthetic fresh install, then the user's follow-up steps, then a re-run, then a third run: nothing
/// done is repeated, every import is safe to repeat, an app that hasn't created its settings file reads as waiting (not
/// failed), and History holds exactly the runs that did or still need something. Everything runs against temporary
/// files, a stand-in winget and an in-memory registry; no real app, setting or package is touched.
/// </summary>
public sealed class ProfileRetryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mesura-retry-tests-").FullName;
    private readonly string _prefs;
    private DateTimeOffset _now = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);

    public ProfileRetryTests() => _prefs = Path.Combine(_dir, "Brave", "Preferences");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>winget that "installs" by remembering the package, and records every install it was asked to run.</summary>
    private sealed class FakeWinget : IWingetRunner
    {
        public HashSet<string> Installed { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Ran { get; } = [];

        public bool IsAvailable() => true;

        public WingetRunResult List(AppPackage package) => Installed.Contains(package.WingetId)
            ? new WingetRunResult(true, 0, $"Name Id\n----------\n{package.Name} {package.WingetId}")
            : new WingetRunResult(true, WingetOutput.NoApplicationsFound);

        public WingetRunResult Install(AppPackage package, Action<string>? onOutput, CancellationToken cancellationToken)
        {
            Ran.Add(package.WingetId);
            Installed.Add(package.WingetId);
            return new WingetRunResult(true, 0);
        }
    }

    /// <summary>A registry-only Windows, enough for per-user and machine-wide registry operations.</summary>
    private sealed class FakeRegistry : ISystemReader, IWindowsWriter
    {
        public Dictionary<string, (RegistryKind Kind, string Value)> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Writes { get; } = [];

        public int Build => 26100;

        public bool IsElevated { get; set; }

        public UserContext User => new("S-1-5-21-1-1-1-1001", "PC-owner", "");

        public bool? HasBattery => false;

        public bool? ModernStandby => false;
        public bool? PolicyEdition => true;

        private static string Key(string hive, string path, string name) => $@"{hive}\{path}\{name}";

        public RegistryReading ReadRegistry(string hive, string path, string name) =>
            Values.TryGetValue(Key(hive, path, name), out var v) ? new RegistryReading(true, true, v.Kind, v.Value) : new RegistryReading(true, false, null, null);

        public ServiceReading ReadService(string name) => ServiceReading.Absent;

        public FeatureState ReadFeature(string name) => FeatureState.Absent;

        public FeatureState ReadTask(string path) => FeatureState.Absent;

        public void SetTaskEnabled(string path, bool enabled) => throw new NotSupportedException();

        public void SetTcpGlobal(string name, string value) => throw new NotSupportedException();

        public PowerReading ReadPower() => new(null, new HashSet<string>());

        public uint? ReadPowerSetting(string scheme, string subgroup, string setting) => null;

        public void SetRegistry(string hive, string path, string name, RegistryKind kind, string value)
        {
            Writes.Add(Key(hive, path, name));
            Values[Key(hive, path, name)] = (kind, value);
        }

        public void DeleteRegistry(string hive, string path, string name) => Values.Remove(Key(hive, path, name));

        public void DeleteRegistryKeyIfEmpty(string hive, string path) { }

        public void SetServiceStart(string name, int start) => throw new NotSupportedException();

        public bool SetFeature(string name, bool enabled) => throw new NotSupportedException();

        public void CreatePowerScheme(string schemeGuid) => throw new NotSupportedException();

        public void ActivatePowerScheme(string schemeGuid) => throw new NotSupportedException();

        public void DeletePowerScheme(string schemeGuid) => throw new NotSupportedException();

        public void SetPowerSetting(string scheme, string subgroup, string setting, uint acValue) => throw new NotSupportedException();
    }

    private IReadOnlyList<LocatedConfigFile> Locate(ConfigPortDefinition port) =>
        [new LocatedConfigFile("main", Format.Json, _prefs, File.Exists(_prefs))];

    private SetupProfile Profile()
    {
        // The settings file a source PC exported: Brave's new-tab clock on.
        File.WriteAllText(Path.Combine(_dir, "my.settings.json"), ConfigBundleFile.Save(
            [new AppConfigBundle("brave", "Brave", new Dictionary<string, string> { ["brave.ntp.showClock"] = "true" }, [])],
            "2026-10-22T00:00:00Z"));
        return SetupProfile.Parse("""
            { "kind": "Mesura.SetupProfile", "schemaVersion": 1, "name": "Fresh install",
              "apps": ["brave"], "configBundle": "my.settings.json", "configApps": ["brave"],
              "windows": ["explorer.show-extensions", "system.verbose-status"] }
            """, AppCatalog.Default, WindowsCatalog.Default);
    }

    private SetupRun RunOnce(SetupProfile profile, AppInstallService installer, SetupStore store, FakeRegistry pc, SetupStore? history = null)
    {
        _now = _now.AddMinutes(5); // each run gets its own id
        var plan = ProfileRunner.Plan(profile, _dir, AppCatalog.Default, installer, WindowsCatalog.Default, history);
        return ProfileRunner.Run(plan, installer, store, () => _now, null, CancellationToken.None,
            new WindowsConfigurator(pc, pc), Locate, _ => false);
    }

    [Fact]
    public void A_retried_profile_finishes_what_waited_repeats_nothing_and_history_stays_truthful()
    {
        var winget = new FakeWinget();
        var installer = new AppInstallService(winget);
        var store = new SetupStore(Path.Combine(_dir, "store"));
        var pc = new FakeRegistry(); // a standard user: machine-wide settings need elevation
        var profile = Profile();

        // Run 1, fresh install: Brave installs, but it has never started, so its settings file doesn't exist yet.
        var first = RunOnce(profile, installer, store, pc);

        Assert.Equal(["Brave.Brave"], winget.Ran);
        var brave = Assert.Single(first.Config);
        Assert.Equal("waiting", StepWords.Config(brave));                 // not "failed", not "already done"
        Assert.Equal("applied", StepWords.Windows(first.WindowsSteps.Single(w => w.OperationId == "explorer.show-extensions").Status));
        Assert.Equal("needs admin", StepWords.Windows(first.WindowsSteps.Single(w => w.OperationId == "system.verbose-status").Status));
        Assert.Equal(RunStatus.Incomplete, RunFollowUp.Status(first));    // nothing failed; some steps wait on the user
        var waits = RunFollowUp.For(first, id => id).Where(f => f.Kind == FollowUpKind.RunAgain).ToList();
        Assert.Equal(2, waits.Count);                                      // start Brave once; run as administrator
        Assert.Single(store.ListRuns());

        // The user's follow-up steps: Brave started once (it writes its file), Mesura restarted as administrator.
        Directory.CreateDirectory(Path.GetDirectoryName(_prefs)!);
        File.WriteAllText(_prefs, """{"brave":{"new_tab_page":{"show_clock":false}},"profile":{"name":"keep me"}}""");
        pc.IsElevated = true;
        var writesBefore = pc.Writes.Count;

        // Run 2: what waited is done; what was done isn't repeated.
        var second = RunOnce(profile, installer, store, pc);

        Assert.Equal(["Brave.Brave"], winget.Ran);                         // not installed a second time
        Assert.Equal(InstallStatus.AlreadyInstalled, second.Apps.Single().Status);
        Assert.Equal("applied", StepWords.Config(second.Config.Single()));
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(_prefs))!;
        Assert.True(json["brave"]!["new_tab_page"]!["show_clock"]!.GetValue<bool>());
        Assert.Equal("keep me", json["profile"]!["name"]!.GetValue<string>()); // the app's own settings kept
        Assert.Equal(WindowsOpStatus.AlreadyApplied, second.WindowsSteps.Single(w => w.OperationId == "explorer.show-extensions").Status);
        Assert.Equal(WindowsOpStatus.Applied, second.WindowsSteps.Single(w => w.OperationId == "system.verbose-status").Status);
        Assert.Equal([@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System\VerboseStatus"], pc.Writes.Skip(writesBefore));
        Assert.Equal(RunStatus.Succeeded, RunFollowUp.Status(second));
        Assert.DoesNotContain(RunFollowUp.For(second, id => id), f => f.Kind is FollowUpKind.RunAgain or FollowUpKind.Problem);
        Assert.Equal(2, store.ListRuns().Count);

        // Run 3: everything is in place. The import is safe to repeat (nothing written), and the run isn't recorded.
        var fileTime = File.GetLastWriteTimeUtc(_prefs);
        var third = RunOnce(profile, installer, store, pc);

        Assert.True(third.ChangedNothing);
        Assert.Equal("already done", StepWords.Config(third.Config.Single()));
        Assert.Equal(fileTime, File.GetLastWriteTimeUtc(_prefs));
        Assert.Equal(2, store.ListRuns().Count);                           // History: the two runs that did something
        Assert.Equal(["Brave.Brave"], winget.Ran);
    }

    [Fact]
    public void An_app_Mesura_cannot_detect_is_installed_once_not_on_every_re_run()
    {
        var winget = new FakeWinget();
        var installer = new AppInstallService(winget);
        var store = new SetupStore(Path.Combine(_dir, "store"));
        var pc = new FakeRegistry();
        var profile = SetupProfile.Parse("""
            { "kind": "Mesura.SetupProfile", "schemaVersion": 1, "name": "Vencord", "apps": ["vencord"] }
            """, AppCatalog.Default, WindowsCatalog.Default);

        var first = RunOnce(profile, installer, store, pc, history: store);
        Assert.Equal(["Vendicated.Vencord"], winget.Ran);           // winget can't list it, so the first run installs it
        Assert.Equal(InstallStatus.Succeeded, first.Apps.Single().Status);

        var plan = ProfileRunner.Plan(profile, _dir, AppCatalog.Default, installer, WindowsCatalog.Default, store);
        Assert.Contains("vencord", plan.InstalledEarlier);
        var second = RunOnce(profile, installer, store, pc, history: store);

        Assert.Equal(["Vendicated.Vencord"], winget.Ran);           // its installer isn't run again
        var again = second.Apps.Single();
        Assert.Equal(InstallStatus.AlreadyInstalled, again.Status);
        Assert.Equal(ProfilePlan.EarlierDetail, again.Detail);
        Assert.True(second.ChangedNothing);
        Assert.Single(store.ListRuns());                             // and the no-op re-run isn't recorded
    }

    [Fact]
    public void A_settings_file_that_exists_but_cannot_be_read_is_a_failure_not_a_missing_app()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_prefs)!);
        File.WriteAllText(_prefs, "{ this is not json");
        var original = File.ReadAllText(_prefs);
        var bundle = ConfigBundleFile.Load(ConfigBundleFile.Save(
            [new AppConfigBundle("brave", "Brave", new Dictionary<string, string> { ["brave.ntp.showClock"] = "true" }, [])],
            "2026-10-22T00:00:00Z"));

        var outcome = ConfigService.Import(bundle, [ConfigCatalog.ById("brave")!], new SetupStore(Path.Combine(_dir, "store")),
            "20261022T090000Z-test", Locate, _ => false).Single();

        Assert.Equal(ConfigImportStatus.Failed, outcome.Status);
        Assert.Equal("failed", StepWords.Config(outcome));
        Assert.Empty(outcome.DestinationMissing);   // not mistaken for "start the app once"
        Assert.Empty(outcome.Applied);              // nothing claimed as set
        Assert.Contains("couldn't be read", outcome.Detail, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllText(_prefs));
        Assert.Contains(RunFollowUp.For(SetupRun.Create("id", "config-import", null, "a", "b", [], [outcome]), id => id),
            f => f.Kind == FollowUpKind.Problem);
    }
}
