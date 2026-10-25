using System.Text;
using Mesura.Core.Apps;
using Mesura.Core.Setup;
using Mesura.Core.Windows;
using Mesura.Core.Nvidia;
using Mesura.Setup.Windows;
using Mesura.Setup.Windows.Nvidia;
using Mesura.Setup.Windows.Workflow;

namespace Mesura.Setup.Tests;

/// <summary>
/// A whole setup (D031) against stand-ins only — a winget that remembers, an in-memory registry, NVIDIA tools that keep
/// their profiles in memory and temporary folders: the steps run in order and record into one history entry, the driver
/// step waits for the user and the setup continues after it without repeating anything, a profile backup is put back and
/// verified, an install from Apps leaves an unfinished setup alone, and a cancelled step runs again.
/// </summary>
public sealed class SetupWorkflowRunnerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mesura-setup-tests-").FullName;
    private DateTimeOffset _now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class FakeWinget : IWingetRunner
    {
        public HashSet<string> Installed { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Ran { get; } = [];

        public Action<string>? OnInstall { get; set; }

        public bool IsAvailable() => true;

        public WingetRunResult List(AppPackage package) => Installed.Contains(package.WingetId)
            ? new WingetRunResult(true, 0, $"Name Id\n----------\n{package.Name} {package.WingetId}")
            : new WingetRunResult(true, WingetOutput.NoApplicationsFound);

        public WingetRunResult Install(AppPackage package, Action<string>? onOutput, CancellationToken cancellationToken)
        {
            Ran.Add(package.WingetId);
            Installed.Add(package.WingetId);
            OnInstall?.Invoke(package.WingetId);
            return new WingetRunResult(true, 0);
        }
    }

    /// <summary>NVIDIA tools whose driver profiles live in memory, as NVIDIA Profile Inspector would export them.</summary>
    private sealed class FakeNvidia(FakeWinget winget) : INvidiaTools
    {
        public string Profiles { get; set; } = NipWith("10240");

        public List<string> Imported { get; } = [];

        public int Launches { get; private set; }

        /// <summary>When set, an import "succeeds" (exit 0) without changing anything, as a failed silent import does.</summary>
        public bool ImportIgnored { get; set; }

        public Version InspectorVersion { get; set; } = new(3, 0, 2, 3);

        public string? ExportError { get; set; }

        public string? FindNvCleanstall() => winget.Installed.Contains("TechPowerUp.NVCleanstall") ? @"C:\Program Files\NVCleanstall\NVCleanstall.exe" : null;

        public string? LaunchNvCleanstall(string path)
        {
            Launches++;
            return null;
        }

        public ProfileInspector? FindProfileInspector() =>
            winget.Installed.Contains("Orbmu2k.nvidiaProfileInspector") ? new ProfileInspector(@"C:\npi\nvidiaProfileInspector.exe", InspectorVersion) : null;

        public ProfileExport Export(ProfileInspector inspector, CancellationToken cancellationToken) =>
            ExportError is { } error ? new(null, error) : new([0xFF, 0xFE, .. Encoding.Unicode.GetBytes(Profiles)], null);

        public string? MergeImport(ProfileInspector inspector, string nipPath, CancellationToken cancellationToken)
        {
            Imported.Add(nipPath);
            if (!ImportIgnored)
            {
                Profiles = Merge(Profiles, NipFile.Decode(File.ReadAllBytes(nipPath)));
            }

            return null;
        }

        /// <summary>A merge import as NVIDIA Profile Inspector does it: each profile by name, each setting by id stored over.</summary>
        private static string Merge(string current, string imported)
        {
            var profiles = NipFile.Parse(current).Select(p => (p.Name, p.Executables, Settings: p.Settings.ToDictionary(s => s.Id))).ToList();
            foreach (var profile in NipFile.Parse(imported))
            {
                var target = profiles.FirstOrDefault(t => t.Name == profile.Name);
                if (target.Name is null)
                {
                    target = (profile.Name, profile.Executables, new Dictionary<uint, NipSetting>());
                    profiles.Add(target);
                }

                foreach (var setting in profile.Settings)
                {
                    target.Settings[setting.Id] = setting;
                }
            }

            static string X(string text) => System.Security.SecurityElement.Escape(text);
            return """<?xml version="1.0" encoding="utf-16"?><ArrayOfProfile>"""
                + string.Concat(profiles.Select(p => $"<Profile><ProfileName>{X(p.Name)}</ProfileName><Executeables>"
                    + string.Concat(p.Executables.Select(e => $"<string>{X(e)}</string>")) + "</Executeables><Settings>"
                    + string.Concat(p.Settings.Values.Select(s => $"<ProfileSetting><SettingNameInfo /><SettingID>{s.Id}</SettingID>"
                        + $"<SettingValue>{X(s.Value)}</SettingValue><ValueType>{s.ValueType}</ValueType></ProfileSetting>"))
                    + "</Settings></Profile>"))
                + "</ArrayOfProfile>";
        }
    }

    private static string NipWith(string cacheSize) => $"""
        <?xml version="1.0" encoding="utf-16"?>
        <ArrayOfProfile>
          <Profile>
            <ProfileName>Some Game</ProfileName>
            <Executeables><string>game.exe</string></Executeables>
            <Settings>
              <ProfileSetting><SettingNameInfo /><SettingID>11306135</SettingID><SettingValue>{cacheSize}</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
            </Settings>
          </Profile>
        </ArrayOfProfile>
        """;

    private const string EmptyNip = """<?xml version="1.0" encoding="utf-16"?><ArrayOfProfile />""";

    /// <summary>A registry-only Windows for per-user registry operations.</summary>
    private sealed class FakeRegistry : ISystemReader, IWindowsWriter
    {
        public Dictionary<string, (RegistryKind Kind, string Value)> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int Build => 26200;

        public bool IsElevated => true;

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

        public void SetRegistry(string hive, string path, string name, RegistryKind kind, string value) => Values[Key(hive, path, name)] = (kind, value);

        public void DeleteRegistry(string hive, string path, string name) => Values.Remove(Key(hive, path, name));

        public void DeleteRegistryKeyIfEmpty(string hive, string path) { }

        public void SetServiceStart(string name, int start) => throw new NotSupportedException();

        public bool SetFeature(string name, bool enabled) => throw new NotSupportedException();

        public void CreatePowerScheme(string schemeGuid) => throw new NotSupportedException();

        public void ActivatePowerScheme(string schemeGuid) => throw new NotSupportedException();

        public void DeletePowerScheme(string schemeGuid) => throw new NotSupportedException();

        public void SetPowerSetting(string scheme, string subgroup, string setting, uint acValue) => throw new NotSupportedException();
    }

    /// <summary>A PC for a setup; <paramref name="battery"/> is what Windows says about a system battery (<c>null</c>: unread).</summary>
    private (SetupContext Context, FakeWinget Winget, FakeNvidia Nvidia, SetupStore Store) Pc(bool? battery = false)
    {
        var winget = new FakeWinget();
        var nvidia = new FakeNvidia(winget);
        var store = new SetupStore(Path.Combine(_dir, "store"));
        var pc = new FakeRegistry();
        var context = new SetupContext(new AppInstallService(winget), AppCatalog.Default, WindowsCatalog.Default, new WindowsConfigurator(pc, pc), store,
            nvidia, () => _now = _now.AddMinutes(1), ConfigBundle: null, Locate: _ => [], IsRunning: _ => false, ReadBattery: () => battery);
        return (context, winget, nvidia, store);
    }

    private static PendingSetup Plan(params SetupStep[] steps) =>
        PendingSetup.Create("20261023T120000Z-setup", "2026-10-23T12:00:00Z", "recommended-gaming", "none", steps,
            new NvidiaTarget("NVIDIA GeForce RTX 4070", "560.94", "621.01", ["display-driver"], CleanInstall: false));

    private static PendingSetup Everything() => Plan(
        new SetupStep(StepKind.Runtimes, ["vcredist-2013-x64"]),
        new SetupStep(StepKind.Apps, ["brave"]),
        new SetupStep(StepKind.Windows, ["explorer.show-extensions"]),
        new SetupStep(StepKind.NvidiaBackup, []),
        new SetupStep(StepKind.NvidiaDriver, []),
        new SetupStep(StepKind.NvidiaRestore, []));

    [Fact]
    public void A_setup_runs_in_order_waits_for_the_driver_continues_after_it_and_is_one_record()
    {
        var (context, winget, nvidia, store) = Pc();

        // First sitting: everything up to the driver, which waits for the user in NVCleanstall.
        var first = SetupWorkflowRunner.Run(Everything(), context, null, CancellationToken.None);
        Assert.Equal(["Microsoft.VCRedist.2013.x64", "Brave.Brave", "Orbmu2k.nvidiaProfileInspector", "TechPowerUp.NVCleanstall"], winget.Ran);
        Assert.Equal([StepState.Done, StepState.Done, StepState.Done, StepState.Done, StepState.Waiting, StepState.Pending],
            first.Setup.Steps.Select(s => s.State));
        Assert.Equal(1, nvidia.Launches);
        Assert.Null(first.RecordError);
        Assert.NotNull(store.ReadPendingSetup());
        Assert.NotNull(store.RunFilePath(first.Setup.RunId, SetupWorkflowRunner.BackupFile));
        Assert.NotNull(SetupWorkflowRunner.Pending(store));

        // The user installed the driver; the profiles came back reset, as a clean install leaves them.
        nvidia.Profiles = EmptyNip;
        var finished = SetupWorkflowRunner.FinishDriver(SetupWorkflowRunner.Pending(store)!, context, "621.01", skipped: false);
        Assert.Equal(StepState.Done, finished.Setup.Step(StepKind.NvidiaDriver)!.State);
        Assert.Equal("621.01", finished.Setup.Step(StepKind.NvidiaDriver)!.Value);

        // Second sitting: only the restore runs; nothing is installed again; the backup is merged back and verified.
        var second = SetupWorkflowRunner.Run(finished.Setup, context, null, CancellationToken.None);
        Assert.Equal(4, winget.Ran.Count);
        Assert.Equal(StepState.Done, second.Setup.Step(StepKind.NvidiaRestore)!.State);
        Assert.Single(nvidia.Imported);
        Assert.Contains("10240", nvidia.Profiles, StringComparison.Ordinal);
        Assert.True(second.Setup.Finished);
        Assert.Null(store.ReadPendingSetup());

        // One record for the whole setup, with every step.
        var run = SetupRun.FromJson(store.ReadRunRecord(first.Setup.RunId)!);
        Assert.Equal("setup", run.Operation);
        Assert.Equal(4, run.Apps.Count);
        Assert.Single(run.WindowsSteps);
        Assert.Equal([StepKind.NvidiaBackup, StepKind.NvidiaDriver, StepKind.NvidiaRestore], run.SetupSteps.Select(s => s.Kind));
        Assert.All(run.SetupSteps, s => Assert.Equal(StepState.Done, s.State));
        Assert.Equal(RunStatus.Succeeded, RunFollowUp.Status(run));
    }

    [Fact]
    public void An_import_that_changed_nothing_fails_its_check()
    {
        var (context, _, nvidia, _) = Pc();
        var waiting = SetupWorkflowRunner.Run(Plan(new SetupStep(StepKind.NvidiaBackup, []), new SetupStep(StepKind.NvidiaDriver, []),
            new SetupStep(StepKind.NvidiaRestore, [])), context, null, CancellationToken.None);
        nvidia.Profiles = EmptyNip;
        nvidia.ImportIgnored = true; // NVIDIA Profile Inspector exits with 0 even when nothing was imported
        var done = SetupWorkflowRunner.FinishDriver(waiting.Setup, context, "621.01", skipped: false);
        var restore = SetupWorkflowRunner.Run(done.Setup, context, null, CancellationToken.None).Setup.Step(StepKind.NvidiaRestore)!;
        Assert.Equal(StepState.Failed, restore.State);
        Assert.Contains("aren't in place", restore.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void No_customised_profiles_means_nothing_to_back_up_or_put_back()
    {
        var (context, _, nvidia, _) = Pc();
        nvidia.Profiles = EmptyNip;
        var waiting = SetupWorkflowRunner.Run(Plan(new SetupStep(StepKind.NvidiaBackup, []), new SetupStep(StepKind.NvidiaDriver, []),
            new SetupStep(StepKind.NvidiaRestore, [])), context, null, CancellationToken.None);
        Assert.Equal(StepState.Skipped, waiting.Setup.Step(StepKind.NvidiaBackup)!.State);
        var skipped = SetupWorkflowRunner.FinishDriver(waiting.Setup, context, null, skipped: true);
        Assert.Equal(StepState.Skipped, skipped.Setup.Step(StepKind.NvidiaDriver)!.State);
        var end = SetupWorkflowRunner.Run(skipped.Setup, context, null, CancellationToken.None);
        Assert.Equal(StepState.Skipped, end.Setup.Step(StepKind.NvidiaRestore)!.State);
        Assert.Empty(nvidia.Imported);
        Assert.True(end.Setup.Finished);
    }

    [Fact]
    public void An_install_from_apps_leaves_an_unfinished_setup_as_it_was()
    {
        var (context, winget, _, store) = Pc();
        var waiting = SetupWorkflowRunner.Run(Plan(new SetupStep(StepKind.NvidiaDriver, [])), context, null, CancellationToken.None);
        var before = store.ReadPendingSetup();
        Assert.NotNull(before);

        var apps = PendingSetup.Create("20261023T130000Z-apps-install", "2026-10-23T13:00:00Z", null, "none", [new SetupStep(StepKind.Apps, ["sevenzip"])], null);
        var sitting = SetupWorkflowRunner.Run(apps, context, null, CancellationToken.None, operation: "apps-install", persist: false);
        Assert.Contains("7zip.7zip", winget.Ran);
        Assert.Equal(before, store.ReadPendingSetup());
        Assert.Equal("apps-install", SetupRun.FromJson(store.ReadRunRecord(sitting.Run.Id)!).Operation);
        Assert.Equal(StepState.Waiting, SetupWorkflowRunner.Pending(store)!.Step(StepKind.NvidiaDriver)!.State);
        _ = waiting;
    }

    [Fact]
    public void A_cancelled_step_stays_to_do_and_runs_again_without_repeating_what_finished()
    {
        var (context, winget, _, store) = Pc();
        using var cancel = new CancellationTokenSource();
        winget.OnInstall = id =>
        {
            if (id == "Microsoft.VCRedist.2013.x64")
            {
                cancel.Cancel(); // the user cancels while the first runtime installs
            }
        };
        var setup = Plan(new SetupStep(StepKind.Runtimes, ["vcredist-2013-x64", "vcredist-2013-x86"]), new SetupStep(StepKind.Apps, ["brave"]));
        var stopped = SetupWorkflowRunner.Run(setup, context, null, cancel.Token);
        Assert.Equal(StepState.Pending, stopped.Setup.Step(StepKind.Runtimes)!.State);
        Assert.Equal(StepState.Pending, stopped.Setup.Step(StepKind.Apps)!.State);
        Assert.NotNull(store.ReadPendingSetup());

        winget.OnInstall = null;
        var resumed = SetupWorkflowRunner.Run(SetupWorkflowRunner.Pending(store)!, context, null, CancellationToken.None);
        Assert.True(resumed.Setup.Finished);
        Assert.Equal(["Microsoft.VCRedist.2013.x64", "Microsoft.VCRedist.2013.x86", "Brave.Brave"], winget.Ran);
        var run = SetupRun.FromJson(store.ReadRunRecord(setup.RunId)!);
        Assert.All(run.Apps, a => Assert.True(a.Status is InstallStatus.Succeeded or InstallStatus.AlreadyInstalled, a.AppId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_failed_backup_keeps_nvcleanstall_closed_and_the_restore_says_why(bool tooOld)
    {
        var (context, winget, nvidia, _) = Pc();
        if (tooOld)
        {
            nvidia.InspectorVersion = new Version(3, 0, 1, 16); // can't import without replacing profiles
        }
        else
        {
            nvidia.ExportError = "NVIDIA Profile Inspector didn't start: The requested operation requires elevation.";
        }

        var end = SetupWorkflowRunner.Run(Plan(new SetupStep(StepKind.NvidiaBackup, []), new SetupStep(StepKind.NvidiaDriver, []),
            new SetupStep(StepKind.NvidiaRestore, [])), context, null, CancellationToken.None).Setup;

        Assert.Equal(StepState.Failed, end.Step(StepKind.NvidiaBackup)!.State);
        Assert.Equal(StepState.Failed, end.Step(StepKind.NvidiaDriver)!.State);
        Assert.Contains("NVCleanstall wasn't opened", end.Step(StepKind.NvidiaDriver)!.Detail, StringComparison.Ordinal);
        Assert.Equal(StepState.Skipped, end.Step(StepKind.NvidiaRestore)!.State);
        Assert.Equal(0, nvidia.Launches);
        Assert.DoesNotContain("TechPowerUp.NVCleanstall", winget.Ran);
        Assert.True(end.Finished);
    }

    [Fact]
    public void A_runtime_whose_state_cant_be_read_is_not_installed_blind()
    {
        var winget = new FakeWinget();
        var store = new SetupStore(Path.Combine(_dir, "store"));
        var installer = new AppInstallService(winget, new Dictionary<string, Func<InstallState>>
        {
            ["vcredist-2013-x64"] = () => InstallState.Unknown,
            ["vcredist-2013-x86"] = () => InstallState.NotInstalled,
        });
        var context = new SetupContext(installer, AppCatalog.Default, WindowsCatalog.Default, null, store, new FakeNvidia(winget), () => _now, null);

        var end = SetupWorkflowRunner.Run(Plan(new SetupStep(StepKind.Runtimes, ["vcredist-2013-x64", "vcredist-2013-x86"])), context, null, CancellationToken.None);

        Assert.Equal(["Microsoft.VCRedist.2013.x86"], winget.Ran);
        Assert.Equal(StepState.Failed, end.Setup.Step(StepKind.Runtimes)!.State);
        Assert.Contains(end.Run.Apps, a => a.AppId == "vcredist-2013-x64" && a.Status == InstallStatus.Failed && a.Detail.Contains("couldn't be read", StringComparison.Ordinal));
    }

    [Fact]
    public void An_app_installed_before_a_cancel_is_still_set_up()
    {
        var winget = new FakeWinget();
        var nvidia = new FakeNvidia(winget);
        var store = new SetupStore(Path.Combine(_dir, "store"));
        var prefs = Path.Combine(_dir, "Brave", "Preferences");
        using var cancel = new CancellationTokenSource();
        winget.OnInstall = id =>
        {
            if (id == "Spotify.Spotify")
            {
                cancel.Cancel(); // Brave is installed; the user cancels while Spotify installs
            }
        };
        var context = new SetupContext(new AppInstallService(winget), AppCatalog.Default, WindowsCatalog.Default, null, store, nvidia, () => _now,
            Mesura.Core.Config.ConfigDefaults.Bundle("2026-10-23T12:00:00Z"),
            Locate: port => port.Id == "brave" ? [new Mesura.Setup.Windows.Config.LocatedConfigFile("main", Mesura.Core.Config.Format.Json, prefs, File.Exists(prefs))] : [],
            IsRunning: _ => false);

        // Brave's first start would create its file; it exists here so the set-up can write it.
        Directory.CreateDirectory(Path.GetDirectoryName(prefs)!);
        winget.OnInstall += id =>
        {
            if (id == "Brave.Brave")
            {
                File.WriteAllText(prefs, "{}");
            }
        };

        var stopped = SetupWorkflowRunner.Run(Plan(new SetupStep(StepKind.Apps, ["brave", "spotify"])), context, null, cancel.Token);

        Assert.Equal(StepState.Pending, stopped.Setup.Step(StepKind.Apps)!.State);
        var brave = Assert.Single(stopped.Run.Config);
        Assert.Equal("brave", brave.PortId);
        Assert.Equal(ConfigImportStatus.Applied, brave.Status);
    }

    [Fact]
    public void Discarding_a_setup_that_waited_marks_the_waiting_step_in_its_record()
    {
        var (context, _, _, store) = Pc();
        var waiting = SetupWorkflowRunner.Run(Plan(new SetupStep(StepKind.NvidiaDriver, [])), context, null, CancellationToken.None);
        Assert.Equal(RunStatus.Incomplete, RunFollowUp.Status(SetupRun.FromJson(store.ReadRunRecord(waiting.Setup.RunId)!)));

        SetupWorkflowRunner.Discard(store);

        Assert.Null(store.ReadPendingSetup());
        var run = SetupRun.FromJson(store.ReadRunRecord(waiting.Setup.RunId)!);
        Assert.Equal(StepState.Skipped, Assert.Single(run.SetupSteps).State);
        Assert.NotEqual(RunStatus.Incomplete, RunFollowUp.Status(run));
    }

    private static PendingSetup NvidiaPlan(string runId) => PendingSetup.Create(runId, "2026-10-24T12:00:00Z", null, "none",
        [new SetupStep(StepKind.NvidiaBackup, []), new SetupStep(StepKind.NvidiaDriver, []), new SetupStep(StepKind.NvidiaRestore, []),
         new SetupStep(StepKind.NvidiaProfile, [])],
        NvidiaTarget.From("NVIDIA GeForce RTX 3080", "560.94", "621.01", NvidiaDriverConfig.Default));

    /// <summary>The value the driver holds for a setting of the Base Profile, as the stand-in tool exports it.</summary>
    private static string? BaseValue(FakeNvidia nvidia, uint id) =>
        NipFile.Parse(nvidia.Profiles).FirstOrDefault(p => p.IsGlobal)?.Settings.FirstOrDefault(s => s.Id == id)?.Value;

    private const uint PowerMode = MesuraNvidiaProfile.PowerManagementMode;
    private const uint VrrState = 0x1094F1F7;

    [Fact]
    public void After_the_driver_mesuras_profile_is_merged_in_over_the_restored_profiles_unchanged_on_a_desktop_and_checked()
    {
        var (context, _, nvidia, store) = Pc(battery: false);
        var plan = NvidiaPlan("20261024T120000Z-setup");
        var waiting = SetupWorkflowRunner.Run(plan, context, null, CancellationToken.None);
        nvidia.Profiles = EmptyNip; // the clean install reset the profiles
        var done = SetupWorkflowRunner.FinishDriver(waiting.Setup, context, "621.01", skipped: false);

        // The stand-in tool merges what it is given: the backup first, then Mesura's profile, as it is on a desktop.
        var end = SetupWorkflowRunner.Run(done.Setup, context, null, CancellationToken.None).Setup;

        Assert.Equal(StepState.Done, end.Step(StepKind.NvidiaRestore)!.State);
        var import = end.Step(StepKind.NvidiaProfile)!;
        Assert.Equal(StepState.Done, import.State);
        Assert.Equal(NvidiaProfiles.Designated.Id, import.Value);
        Assert.Equal(2, nvidia.Imported.Count);
        Assert.EndsWith(NvidiaProfiles.Designated.FileName, nvidia.Imported[1], StringComparison.Ordinal);

        Assert.Equal(NvidiaProfiles.Designated.Bytes(), File.ReadAllBytes(store.RunFilePath(plan.RunId, NvidiaProfiles.Designated.FileName)!));
        Assert.Equal("1", BaseValue(nvidia, PowerMode));
        Assert.Equal("2", BaseValue(nvidia, VrrState));
        Assert.Equal("2", BaseValue(nvidia, 0x1194F158)); // Enable G-SYNC globally: full screen and windowed
        Assert.Contains("10240", nvidia.Profiles, StringComparison.Ordinal); // the user's own game profile is still there
        Assert.Contains("unchanged", import.Detail, StringComparison.Ordinal);

        var record = Assert.Single(SetupRun.FromJson(store.ReadRunRecord(plan.RunId)!).SetupSteps, s => s.Kind == StepKind.NvidiaProfile);
        Assert.Equal(StepState.Done, record.State);
        Assert.Empty(record.Adjustments ?? []);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void On_a_laptop_or_an_unread_battery_the_power_setting_is_left_out_and_the_rest_imported(bool? battery)
    {
        var (context, _, nvidia, store) = Pc(battery);
        var plan = NvidiaPlan("20261024T110000Z-setup");
        var waiting = SetupWorkflowRunner.Run(plan, context, null, CancellationToken.None);
        nvidia.Profiles = EmptyNip;
        var end = SetupWorkflowRunner.Run(SetupWorkflowRunner.FinishDriver(waiting.Setup, context, "621.01", false).Setup, context, null, CancellationToken.None).Setup;

        var import = end.Step(StepKind.NvidiaProfile)!;
        Assert.Equal(StepState.Done, import.State);
        Assert.EndsWith(NvidiaProfiles.Designated.AdjustedFileName, nvidia.Imported[1], StringComparison.Ordinal);
        Assert.Null(BaseValue(nvidia, PowerMode));
        Assert.Equal("2", BaseValue(nvidia, VrrState));
        Assert.Contains("left out", import.Detail, StringComparison.Ordinal);

        var record = Assert.Single(SetupRun.FromJson(store.ReadRunRecord(plan.RunId)!).SetupSteps, s => s.Kind == StepKind.NvidiaProfile);
        var adjustment = Assert.Single(record.Adjustments!);
        Assert.Equal(("Power management mode", null), (adjustment.Setting, adjustment.Imported));
    }

    [Fact]
    public void With_no_new_driver_needed_the_profile_is_still_put_back_imported_and_checked()
    {
        var (context, _, nvidia, store) = Pc(battery: false);
        var plan = NvidiaPlan("20261024T140000Z-setup");
        var waiting = SetupWorkflowRunner.Run(plan, context, null, CancellationToken.None);
        Assert.Equal(StepState.Waiting, waiting.Setup.Step(StepKind.NvidiaDriver)!.State);

        var skipped = SetupWorkflowRunner.FinishDriver(waiting.Setup, context, null, skipped: true);
        var end = SetupWorkflowRunner.Run(skipped.Setup, context, null, CancellationToken.None).Setup;

        Assert.Equal(StepState.Skipped, end.Step(StepKind.NvidiaDriver)!.State);
        Assert.Equal(StepState.Done, end.Step(StepKind.NvidiaRestore)!.State);
        Assert.Equal(StepState.Done, end.Step(StepKind.NvidiaProfile)!.State);
        Assert.Equal("1", BaseValue(nvidia, PowerMode));
        Assert.Null(store.ReadPendingSetup());
    }

    [Fact]
    public void A_power_mode_the_user_already_chose_stays_when_the_profile_leaves_it_out_on_a_laptop()
    {
        // The user's own Base Profile holds Power management mode 4 (prefer minimum); the clean install resets it, the restore puts it back.
        var (context, _, nvidia, _) = Pc(battery: true);
        nvidia.Profiles = """
            <?xml version="1.0" encoding="utf-16"?>
            <ArrayOfProfile><Profile><ProfileName>Base Profile</ProfileName><Executeables /><Settings>
              <ProfileSetting><SettingNameInfo /><SettingID>274197361</SettingID><SettingValue>4</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
            </Settings></Profile></ArrayOfProfile>
            """;
        var waiting = SetupWorkflowRunner.Run(NvidiaPlan("20261024T150000Z-setup"), context, null, CancellationToken.None);
        nvidia.Profiles = EmptyNip;
        var end = SetupWorkflowRunner.Run(SetupWorkflowRunner.FinishDriver(waiting.Setup, context, "621.01", false).Setup, context, null, CancellationToken.None).Setup;

        Assert.Equal(StepState.Done, end.Step(StepKind.NvidiaProfile)!.State);
        Assert.Equal("4", BaseValue(nvidia, PowerMode));
        Assert.Equal("2", BaseValue(nvidia, VrrState));
    }

    [Fact]
    public void A_setting_the_driver_doesnt_take_fails_the_import_by_name()
    {
        var (context, _, nvidia, _) = Pc(battery: false);
        var waiting = SetupWorkflowRunner.Run(NvidiaPlan("20261024T160000Z-setup"), context, null, CancellationToken.None);

        // Nothing of the import reaches the driver, as when NVIDIA Profile Inspector skips settings the driver doesn't know.
        nvidia.ImportIgnored = true;
        var end = SetupWorkflowRunner.Run(SetupWorkflowRunner.FinishDriver(waiting.Setup, context, "621.01", false).Setup, context, null, CancellationToken.None).Setup;

        var import = end.Step(StepKind.NvidiaProfile)!;
        Assert.Equal(StepState.Failed, import.State);
        Assert.Contains("3 setting(s) aren't in place after the import", import.Detail, StringComparison.Ordinal);
        Assert.Contains("0x1057EB71", import.Detail, StringComparison.Ordinal);
        Assert.Contains("may not know a setting", import.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void No_profile_in_the_configuration_is_a_failure_to_fix_not_a_silent_skip()
    {
        var (context, _, nvidia, _) = Pc();
        var plan = PendingSetup.Create("20261024T130000Z-setup", "2026-10-24T13:00:00Z", null, "none",
            [new SetupStep(StepKind.NvidiaBackup, []), new SetupStep(StepKind.NvidiaDriver, []), new SetupStep(StepKind.NvidiaProfile, [])],
            NvidiaTarget.From("NVIDIA GeForce RTX 3080", "560.94", null, NvidiaDriverConfig.Default with { Profile = null }));
        var waiting = SetupWorkflowRunner.Run(plan, context, null, CancellationToken.None);
        var end = SetupWorkflowRunner.Run(SetupWorkflowRunner.FinishDriver(waiting.Setup, context, "621.01", false).Setup, context, null, CancellationToken.None).Setup;

        var import = end.Step(StepKind.NvidiaProfile)!;
        Assert.Equal(StepState.Failed, import.State);
        Assert.Contains("names no NVIDIA profile", import.Detail, StringComparison.Ordinal);
        Assert.Empty(nvidia.Imported); // nothing was imported, and nothing pretends it was
    }

    [Fact]
    public void The_store_keeps_the_unfinished_setup_and_a_runs_files_by_plain_names_only()
    {
        var store = new SetupStore(Path.Combine(_dir, "store"));
        Assert.Null(store.ReadPendingSetup());
        store.WritePendingSetup("{}");
        Assert.Equal("{}", store.ReadPendingSetup());
        store.ClearPendingSetup();
        Assert.Null(store.ReadPendingSetup());
        store.ClearPendingSetup(); // clearing twice is fine

        var path = store.KeepRunFile("20261023T120000Z-setup", "backup.nip", [1, 2, 3]);
        Assert.Equal(path, store.RunFilePath("20261023T120000Z-setup", "backup.nip"));
        Assert.Null(store.RunFilePath("20261023T120000Z-setup", "other.nip"));
        Assert.Throws<ArgumentException>(() => store.KeepRunFile("20261023T120000Z-setup", @"..\escape.nip", [1]));
    }
}
