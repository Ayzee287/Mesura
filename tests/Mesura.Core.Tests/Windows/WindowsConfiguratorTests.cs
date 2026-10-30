using Mesura.Core.Setup;
using Mesura.Core.Windows;

namespace Mesura.Core.Tests.Windows;

/// <summary>An in-memory Windows: registry values, service start types, features and power schemes, read and written.</summary>
internal sealed class FakeWindows : ISystemReader, IWindowsWriter
{
    public Dictionary<string, (RegistryKind Kind, string Value)> Registry { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> Services { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, FeatureState> Features { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Schemes { get; } = new(StringComparer.OrdinalIgnoreCase) { "381b4222-f694-41f0-9685-ff5bb260df2e" };
    public string? Active { get; set; } = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public HashSet<string> Protected { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Writes { get; } = [];

    public int Build { get; set; } = 26100;
    public bool IsElevated { get; set; } = true;
    public UserContext User { get; set; } = new("S-1-5-21-1-1-1-1001", "PC-owner", "");

    /// <summary>A desktop by default: no battery, not Modern Standby.</summary>
    public bool? HasBattery { get; set; } = false;

    public bool? ModernStandby { get; set; } = false;
    public bool? PolicyEdition { get; set; } = true;

    private static string Key(string hive, string path, string name) => $@"{hive}\{path}\{name}";

    public RegistryReading ReadRegistry(string hive, string path, string name) =>
        Unreadable.Contains(Key(hive, path, name)) ? RegistryReading.Failed
        : Registry.TryGetValue(Key(hive, path, name), out var v)
            ? new RegistryReading(true, true, v.Kind, v.Value)
            : new RegistryReading(KeyExists(hive, path), false, null, null);

    /// <summary>Values whose read fails (access denied): nothing is known about them.</summary>
    public HashSet<string> Unreadable { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Values whose write is refused before it takes effect (a locked key).</summary>
    public HashSet<string> RefuseWrite { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ServiceReading ReadService(string name) => Services.TryGetValue(name, out var s) ? new ServiceReading(true, s) : ServiceReading.Absent;

    public FeatureState ReadFeature(string name) => Features.GetValueOrDefault(name, FeatureState.Absent);

    /// <summary>Scheduled tasks by full path; a task not listed is absent.</summary>
    public Dictionary<string, FeatureState> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase);

    public FeatureState ReadTask(string path) => Tasks.GetValueOrDefault(path, FeatureState.Absent);

    public void SetTaskEnabled(string path, bool enabled)
    {
        Writes.Add("task " + path);
        Tasks[path] = enabled ? FeatureState.Enabled : FeatureState.Disabled;
    }

    /// <summary>Global TCP settings by netsh name; one not listed can't be read. <see cref="TcpKeeps"/> false: writes don't stick.</summary>
    public Dictionary<string, string> Tcp { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool TcpKeeps { get; set; } = true;

    public string? ReadTcpGlobal(string name) => Tcp.TryGetValue(name, out var value) ? value : null;

    public void SetTcpGlobal(string name, string value)
    {
        if (!TcpGlobalSetting.IsAllowed(name, value))
        {
            throw new ArgumentException($"not allowed: {name}={value}");
        }

        Writes.Add($"tcp {name}={value}");
        if (TcpKeeps)
        {
            Tcp[name] = value;
        }
    }

    public PowerReading ReadPower() => new(Active, Schemes);

    /// <summary>Power settings by "scheme|setting" (the subgroup isn't needed to tell them apart in the fake).</summary>
    public Dictionary<string, uint> PowerSettings { get; } = new(StringComparer.OrdinalIgnoreCase);

    public uint? ReadPowerSetting(string scheme, string subgroup, string setting) =>
        PowerSettings.TryGetValue($"{scheme}|{setting}", out var v) ? v : null;

    public void SetPowerSetting(string scheme, string subgroup, string setting, uint acValue)
    {
        Writes.Add($"power {setting}={acValue}");
        PowerSettings[$"{scheme}|{setting}"] = acValue;
    }

    public void SetRegistry(string hive, string path, string name, RegistryKind kind, string value)
    {
        var key = Key(hive, path, name);
        if (RefuseWrite.Contains(key))
        {
            throw new UnauthorizedAccessException("access denied");
        }

        Writes.Add(key);
        Keys.Add($@"{hive}\{path}");
        if (!Protected.Contains(key))
        {
            Registry[key] = (kind, value); // a protected value silently refuses, as a policy-owned key does
        }

        if (FailAfterWrite.Contains(key))
        {
            throw new IOException("the write took effect, then the call failed");
        }
    }

    /// <summary>Values whose write takes effect and then reports a failure (a tool that half-worked).</summary>
    public HashSet<string> FailAfterWrite { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void DeleteRegistry(string hive, string path, string name)
    {
        Writes.Add("delete " + Key(hive, path, name));
        Registry.Remove(Key(hive, path, name));
    }

    public void SetServiceStart(string name, int start)
    {
        Writes.Add("service " + name);
        Services[name] = start;
    }

    public bool SetFeature(string name, bool enabled)
    {
        Writes.Add("feature " + name);
        Features[name] = enabled ? FeatureState.Enabled : FeatureState.Disabled;
        return true;
    }

    public void CreatePowerScheme(string schemeGuid)
    {
        Writes.Add("create " + schemeGuid);
        Schemes.Add(schemeGuid);
    }

    public void ActivatePowerScheme(string schemeGuid)
    {
        Writes.Add("activate " + schemeGuid);
        Active = schemeGuid;
    }

    public void DeletePowerScheme(string schemeGuid)
    {
        Writes.Add("delete scheme " + schemeGuid);
        Schemes.Remove(schemeGuid);
    }

    /// <summary>Keys the fake knows exist without a value in them (e.g. a policy key created by a write).</summary>
    public HashSet<string> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> EnumerateSubKeys(string hive, string path)
    {
        var prefix = $@"{hive}\{path}\";
        return [.. Registry.Keys.Concat(Keys).Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => k[prefix.Length..].Split('\\')[0]).Where(n => n.Length > 0 && !Registry.ContainsKey(prefix + n)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Tool-backed settings by "kind:name"; one not listed can't be read.</summary>
    public Dictionary<string, string> System { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? ReadSystemSetting(string kind, string name) => System.TryGetValue($"{kind}:{name}", out var v) ? v : null;

    public void SetSystemSetting(string kind, string name, string value)
    {
        if (!SystemSetting.IsAllowed(kind, name, value))
        {
            throw new ArgumentException($"not allowed: {kind}:{name}={value}");
        }

        Writes.Add($"system {kind}:{name}={value}");
        System[$"{kind}:{name}"] = value;
    }

    /// <summary>Installed packaged apps by full name; staged ones (still on the PC after a removal) by family.</summary>
    public HashSet<string> Apps { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> StagedFamilies { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string>? ReadAppxPackages(string name) =>
        [.. Apps.Where(a => a.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase))];

    public void RemoveAppx(string packageFullName)
    {
        Writes.Add("remove " + packageFullName);
        Apps.Remove(packageFullName);
        StagedFamilies.Add(AppxSetting.FamilyOf(packageFullName)!);
    }

    public void RestoreAppx(string packageFamilyName)
    {
        Writes.Add("restore " + packageFamilyName);
        if (!StagedFamilies.Contains(packageFamilyName))
        {
            throw new InvalidOperationException("no staged copy");
        }

        Apps.Add($"{packageFamilyName.Split('_')[0]}_4.1.0.0_x64__{packageFamilyName.Split('_')[1]}");
    }

    public bool KeyExists(string hive, string path) =>
        Keys.Contains($@"{hive}\{path}") || Registry.Keys.Any(k => k.StartsWith($@"{hive}\{path}\", StringComparison.OrdinalIgnoreCase));

    public void DeleteRegistryKeyIfEmpty(string hive, string path)
    {
        Writes.Add($@"delete key {hive}\{path}");
        if (!Registry.Keys.Any(k => k.StartsWith($@"{hive}\{path}\", StringComparison.OrdinalIgnoreCase)))
        {
            Keys.Remove($@"{hive}\{path}");
        }
    }
}

public sealed class WindowsConfiguratorTests
{
    private const string Adv = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string Theme = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private static readonly WindowsCatalog Catalog = WindowsCatalog.Default;

    private static WindowsOperation Op(string id) => Catalog.ById(id)!;

    [Fact]
    public void The_built_in_catalog_loads_and_every_preset_resolves_to_applicable_documented_operations()
    {
        Assert.NotEmpty(Catalog.Operations);
        foreach (var preset in Catalog.Presets)
        {
            var ops = Catalog.Resolve(preset);
            Assert.Equal(preset.OperationIds.Count, ops.Count);
            Assert.All(ops, o => Assert.True(o.Applicable && o.Risk != RiskClass.Undocumented));
        }

        // Security features are never something the catalog applies: only observed, with a link to Windows Security.
        Assert.All(Catalog.Operations.Where(o => o.Category.StartsWith("Security", StringComparison.Ordinal)),
            o => Assert.Equal(RiskClass.Observe, o.Risk));
    }

    [Fact]
    public void Apply_changes_only_what_differs_and_records_the_exact_before_image()
    {
        var pc = new FakeWindows();
        pc.Registry[$@"{Theme}\AppsUseLightTheme"] = (RegistryKind.DWord, "0"); // already as wanted
        var op = Op("appearance.dark-mode");                                 // AppsUseLightTheme + SystemUsesLightTheme

        var outcome = new WindowsConfigurator(pc, pc).Apply([op], TestContext.Current.CancellationToken).Single();

        Assert.Equal(WindowsOpStatus.Applied, outcome.Status);
        var change = Assert.Single(outcome.Changes);                          // only SystemUsesLightTheme was written
        Assert.EndsWith("SystemUsesLightTheme", change.Target, StringComparison.Ordinal);
        Assert.Null(change.Before);                                           // it didn't exist
        Assert.Equal("0", change.After);
        Assert.Single(pc.Writes);
    }

    [Fact]
    public void An_operation_already_in_place_reports_already_applied_and_writes_nothing()
    {
        var pc = new FakeWindows();
        pc.Registry[$@"{Adv}\ShowTaskViewButton"] = (RegistryKind.DWord, "0");

        var outcome = new WindowsConfigurator(pc, pc).Apply([Op("taskbar.hide-task-view")], TestContext.Current.CancellationToken).Single();

        Assert.Equal(WindowsOpStatus.AlreadyApplied, outcome.Status);
        Assert.Empty(pc.Writes);
    }

    [Fact]
    public void Absent_components_are_left_out_and_an_operation_with_none_present_is_not_applicable()
    {
        var pc = new FakeWindows();
        pc.Services["Spooler"] = 2; // Fax is absent, as on a trimmed image

        var print = new WindowsConfigurator(pc, pc).Apply([Op("services.print-off")], TestContext.Current.CancellationToken).Single();
        Assert.Equal(WindowsOpStatus.Applied, print.Status);
        Assert.Equal(4, pc.Services["Spooler"]);
        Assert.Contains("service Fax", print.Detail, StringComparison.Ordinal);

        var xbox = new WindowsConfigurator(pc, pc).Apply([Op("services.xbox-off")], TestContext.Current.CancellationToken).Single();
        Assert.Equal(WindowsOpStatus.Inapplicable, xbox.Status); // every Xbox service removed
    }

    [Fact]
    public void Machine_wide_operations_wait_for_elevation_and_builds_outside_the_range_are_not_applicable()
    {
        var pc = new FakeWindows { IsElevated = false, Build = 19045 };
        pc.Services["Spooler"] = 2;
        var outcomes = new WindowsConfigurator(pc, pc).Apply([Op("services.print-off"), Op("taskbar.align-left")], TestContext.Current.CancellationToken);

        Assert.Equal(WindowsOpStatus.NeedsElevation, outcomes[0].Status);
        Assert.Equal(WindowsOpStatus.Inapplicable, outcomes[1].Status); // a Windows 11 setting on Windows 10
        Assert.Empty(pc.Writes);
    }

    [Fact]
    public void A_value_Windows_refuses_fails_the_operation_and_puts_back_what_it_changed()
    {
        var pc = new FakeWindows();
        pc.Registry[$@"{Theme}\AppsUseLightTheme"] = (RegistryKind.DWord, "1");
        pc.Protected.Add($@"{Theme}\SystemUsesLightTheme");

        var outcome = new WindowsConfigurator(pc, pc).Apply([Op("appearance.dark-mode")], TestContext.Current.CancellationToken).Single();

        Assert.Equal(WindowsOpStatus.Failed, outcome.Status);
        Assert.Equal("1", pc.Registry[$@"{Theme}\AppsUseLightTheme"].Value); // the first write was undone
        Assert.Contains("put back", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Revert_restores_before_images_deletes_values_that_were_absent_and_leaves_later_changes_alone()
    {
        var pc = new FakeWindows();
        pc.Registry[$@"{Theme}\AppsUseLightTheme"] = (RegistryKind.DWord, "1");
        var configurator = new WindowsConfigurator(pc, pc);
        var applied = configurator.Apply([Op("appearance.dark-mode"), Op("power.ultimate-performance")], TestContext.Current.CancellationToken);
        Assert.Equal("e9a42b02-d5df-448d-aa00-03f14749eb61", pc.Active);

        pc.Registry[$@"{Theme}\AppsUseLightTheme"] = (RegistryKind.DWord, "1"); // the user turned this back on since

        var reverted = configurator.Revert(applied);

        Assert.Equal("381b4222-f694-41f0-9685-ff5bb260df2e", pc.Active);           // previous plan back
        Assert.False(pc.Registry.ContainsKey($@"{Theme}\SystemUsesLightTheme"));           // didn't exist before: deleted
        Assert.Equal("1", pc.Registry[$@"{Theme}\AppsUseLightTheme"].Value);           // the user's later choice kept
        var dark = reverted.Single(r => r.OperationId == "appearance.dark-mode");
        Assert.Equal(WindowsOpStatus.Reverted, dark.Status);
        Assert.Contains("changed since", dark.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_with_windows_steps_round_trips_and_an_older_record_without_them_still_reads()
    {
        var pc = new FakeWindows();
        var steps = new WindowsConfigurator(pc, pc).Apply([Op("taskbar.hide-task-view")], TestContext.Current.CancellationToken);
        var run = SetupRun.Create("20261019T000000Z-windows-apply", "windows-apply", null, "a", "b", [], [], steps);

        var back = SetupRun.FromJson(run.ToJson());
        Assert.Equal(steps.Single().Changes.Single(), back.WindowsSteps.Single().Changes.Single());

        var older = SetupRun.FromJson("""
            { "kind": "Mesura.SetupRun", "schemaVersion": 1, "id": "x", "operation": "apps-install", "startedUtc": "a",
              "finishedUtc": "b", "apps": [], "config": [] }
            """);
        Assert.Empty(older.WindowsSteps);
        Assert.True(older.Ok);
    }

    [Fact]
    public void Reverting_removes_a_key_the_run_created_and_a_power_scheme_it_installed()
    {
        var pc = new FakeWindows();
        var configurator = new WindowsConfigurator(pc, pc);
        var applied = configurator.Apply([Op("search.no-box-suggestions"), Op("power.ultimate-performance")], TestContext.Current.CancellationToken);
        Assert.True(pc.KeyExists("HKCU", @"Software\Policies\Microsoft\Windows\Explorer"));
        Assert.Contains("e9a42b02-d5df-448d-aa00-03f14749eb61", pc.Schemes);

        configurator.Revert(applied);

        Assert.False(pc.KeyExists("HKCU", @"Software\Policies\Microsoft\Windows\Explorer"));
        Assert.DoesNotContain("e9a42b02-d5df-448d-aa00-03f14749eb61", pc.Schemes);
        Assert.Equal("381b4222-f694-41f0-9685-ff5bb260df2e", pc.Active);
    }

    [Fact]
    public void Per_user_policy_values_need_elevation_and_revert_follows_the_same_rule()
    {
        var pc = new FakeWindows { IsElevated = false };
        var configurator = new WindowsConfigurator(pc, pc);

        Assert.Equal(WindowsOpStatus.NeedsElevation, configurator.Apply([Op("search.no-box-suggestions")], TestContext.Current.CancellationToken).Single().Status);

        var power = configurator.Apply([Op("power.high-performance")], TestContext.Current.CancellationToken); // allowed unelevated
        pc.Schemes.Add("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
        power = configurator.Apply([Op("power.high-performance")], TestContext.Current.CancellationToken);
        Assert.Equal(WindowsOpStatus.Applied, power.Single().Status);
        Assert.Equal(WindowsOpStatus.Reverted, configurator.Revert(power).Single().Status); // and revertible unelevated
    }

    [Fact]
    public void A_value_with_the_right_text_but_the_wrong_type_is_not_already_applied()
    {
        var pc = new FakeWindows();
        pc.Registry[$@"{Adv}\ShowTaskViewButton"] = (RegistryKind.String, "0");

        var outcome = new WindowsConfigurator(pc, pc).Apply([Op("taskbar.hide-task-view")], TestContext.Current.CancellationToken).Single();

        Assert.Equal(WindowsOpStatus.Applied, outcome.Status);
        Assert.Equal(RegistryKind.DWord, pc.Registry[$@"{Adv}\ShowTaskViewButton"].Kind);
        Assert.Equal(RegistryKind.String, outcome.Changes.Single().RegistryKind); // before-image keeps the old type
    }

    [Fact]
    public void A_failure_that_cannot_be_undone_keeps_the_stuck_changes_in_the_record()
    {
        var pc = new ThrowingOnUndo();
        pc.Registry[$@"{Theme}\AppsUseLightTheme"] = (RegistryKind.DWord, "1");
        pc.Protected.Add($@"{Theme}\SystemUsesLightTheme");

        var outcome = new WindowsConfigurator(pc, pc).Apply([Op("appearance.dark-mode")], TestContext.Current.CancellationToken).Single();

        Assert.Equal(WindowsOpStatus.Failed, outcome.Status);
        Assert.Contains(outcome.Changes, c => c.Target.EndsWith("AppsUseLightTheme", StringComparison.Ordinal));
    }

    [Fact]
    public void Reverting_a_revert_run_restores_the_original_change_including_a_value_that_was_absent()
    {
        var pc = new FakeWindows();
        var configurator = new WindowsConfigurator(pc, pc);
        var applied = configurator.Apply([Op("appearance.dark-mode")], TestContext.Current.CancellationToken);
        var reverted = configurator.Revert(applied);
        Assert.False(pc.Registry.ContainsKey($@"{Theme}\SystemUsesLightTheme"));

        var again = configurator.Revert(reverted).Single();

        Assert.Equal(WindowsOpStatus.Reverted, again.Status);
        Assert.Equal("0", pc.Registry[$@"{Theme}\SystemUsesLightTheme"].Value);
    }

    /// <summary>A fake whose writes fail once the first registry value has been written, so an undo can't run.</summary>
    private sealed class ThrowingOnUndo : ISystemReader, IWindowsWriter
    {
        private readonly FakeWindows _inner = new();
        private int _writes;

        public Dictionary<string, (RegistryKind Kind, string Value)> Registry => _inner.Registry;
        public HashSet<string> Protected => _inner.Protected;
        public int Build => _inner.Build;
        public bool IsElevated => true;
        public UserContext User => _inner.User;
        public bool? HasBattery => _inner.HasBattery;
        public bool? ModernStandby => _inner.ModernStandby;
        public bool? PolicyEdition => _inner.PolicyEdition;
        public RegistryReading ReadRegistry(string hive, string path, string name) => _inner.ReadRegistry(hive, path, name);
        public ServiceReading ReadService(string name) => _inner.ReadService(name);
        public FeatureState ReadFeature(string name) => _inner.ReadFeature(name);
        public FeatureState ReadTask(string path) => _inner.ReadTask(path);
        public void SetTaskEnabled(string path, bool enabled) => _inner.SetTaskEnabled(path, enabled);
        public void SetTcpGlobal(string name, string value) => _inner.SetTcpGlobal(name, value);
        public PowerReading ReadPower() => _inner.ReadPower();
        public uint? ReadPowerSetting(string scheme, string subgroup, string setting) => _inner.ReadPowerSetting(scheme, subgroup, setting);
        public void SetPowerSetting(string scheme, string subgroup, string setting, uint acValue) => _inner.SetPowerSetting(scheme, subgroup, setting, acValue);

        public void SetRegistry(string hive, string path, string name, RegistryKind kind, string value)
        {
            if (++_writes > 2)
            {
                throw new UnauthorizedAccessException("access denied");
            }

            _inner.SetRegistry(hive, path, name, kind, value);
        }

        public void DeleteRegistry(string hive, string path, string name) => throw new UnauthorizedAccessException("access denied");
        public void DeleteRegistryKeyIfEmpty(string hive, string path) => throw new UnauthorizedAccessException("access denied");
        public void SetServiceStart(string name, int start) => _inner.SetServiceStart(name, start);
        public bool SetFeature(string name, bool enabled) => _inner.SetFeature(name, enabled);
        public void CreatePowerScheme(string schemeGuid) => _inner.CreatePowerScheme(schemeGuid);
        public void ActivatePowerScheme(string schemeGuid) => _inner.ActivatePowerScheme(schemeGuid);
        public void DeletePowerScheme(string schemeGuid) => _inner.DeletePowerScheme(schemeGuid);
    }

    [Fact]
    public void Per_user_operations_are_blocked_when_the_signed_in_user_is_unknown_while_machine_ones_still_run()
    {
        // e.g. an elevated run where the session's user couldn't be established
        var pc = new FakeWindows { User = UserContext.Resolve("S-1-5-21-9-9-9-500", null, "", false) };
        pc.Services["Spooler"] = 2;

        var outcomes = new WindowsConfigurator(pc, pc).Apply([Op("taskbar.hide-task-view"), Op("services.print-off")], TestContext.Current.CancellationToken);

        Assert.Equal(WindowsOpStatus.Blocked, outcomes[0].Status);
        Assert.Equal(WindowsOpStatus.Applied, outcomes[1].Status);
        Assert.DoesNotContain(pc.Writes, w => w.StartsWith("HKCU", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("S-1", "S-1", false, true)]  // same account: target it
    [InlineData("S-500", "S-1", true, true)] // elevated as another admin, signed-in user's hive loaded: target the signed-in user
    [InlineData("S-500", "S-1", false, false)] // their hive not reachable: blocked
    [InlineData("S-500", null, false, false)]  // no signed-in user (service, remote shell): blocked
    public void The_per_user_target_is_always_the_signed_in_user_or_nobody(string process, string? session, bool hiveLoaded, bool resolved)
    {
        var context = UserContext.Resolve(process, session, "PC-owner", hiveLoaded);

        Assert.Equal(resolved, context.Resolved);
        Assert.Equal(resolved ? session : null, context.Sid); // never the process's own account when it differs
    }

    [Fact]
    public void A_per_user_change_recorded_for_one_user_is_not_reverted_into_another()
    {
        var pc = new FakeWindows();
        var applied = new WindowsConfigurator(pc, pc).Apply([Op("appearance.dark-mode")], TestContext.Current.CancellationToken);
        Assert.Equal("S-1-5-21-1-1-1-1001", applied.Single().Changes.First().UserSid);

        pc.User = new UserContext("S-1-5-21-1-1-1-1002", "PC-other", "");
        var reverted = new WindowsConfigurator(pc, pc).Revert(applied).Single();

        Assert.Equal(WindowsOpStatus.Blocked, reverted.Status);
        Assert.True(pc.Registry.ContainsKey($@"{Theme}\SystemUsesLightTheme")); // left as the run wrote it
    }

    [Fact]
    public void Power_settings_change_the_active_plan_on_AC_skip_absent_settings_and_revert_exactly()
    {
        const string balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
        var pc = new FakeWindows();
        pc.PowerSettings[$"{balanced}|48e6b7a6-50f5-4782-a5d4-53bb8f07e226"] = 1; // USB selective suspend on
        // USB 3 link power management isn't on this machine: it is left out, not forced.
        var op = Op("power.usb-keep-awake") with { PowerSettings = [.. Op("power.usb-keep-awake").PowerSettings, .. Op("power.usb3-link-power-off").PowerSettings] };
        var configurator = new WindowsConfigurator(pc, pc);

        var applied = configurator.Apply([op], TestContext.Current.CancellationToken).Single();

        Assert.Equal(WindowsOpStatus.Applied, applied.Status);
        Assert.Equal(0u, pc.PowerSettings[$"{balanced}|48e6b7a6-50f5-4782-a5d4-53bb8f07e226"]);
        var change = Assert.Single(applied.Changes);
        Assert.Equal(ChangeKind.PowerSetting, change.Kind);
        Assert.Equal(balanced, change.Scheme);
        Assert.Contains("not present", applied.Detail, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(WindowsOpStatus.Reverted, configurator.Revert([applied]).Single().Status);
        Assert.Equal(1u, pc.PowerSettings[$"{balanced}|48e6b7a6-50f5-4782-a5d4-53bb8f07e226"]);
    }

    [Fact]
    public void A_plan_switch_in_the_same_batch_runs_before_power_settings_so_they_land_in_the_new_plan()
    {
        const string high = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
        const string usb = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";
        var pc = new FakeWindows();
        pc.Schemes.Add(high);
        pc.PowerSettings[$"{high}|{usb}"] = 1;

        // Settings first, plan second: the order a hand-written profile or a CLI --id list can give.
        var outcomes = new WindowsConfigurator(pc, pc).Apply([Op("power.usb-keep-awake"), Op("power.high-performance")], TestContext.Current.CancellationToken);

        Assert.Equal(["power.high-performance", "power.usb-keep-awake"], outcomes.Select(o => o.OperationId));
        Assert.Equal(high, pc.Active);
        Assert.Equal(0u, pc.PowerSettings[$"{high}|{usb}"]);
    }

    [Fact]
    public void The_sign_in_entry_targets_this_executable_corrects_a_moved_one_and_is_refused_without_a_user()
    {
        const string exe = @"C:\Tools\Mesura\Mesura.exe";
        var pc = new FakeWindows();
        var entry = new StartupEntry(pc, pc);
        Assert.False(entry.IsOn(exe));

        entry.Set(exe, on: true);
        Assert.True(entry.IsOn(exe));
        Assert.Equal("\"C:\\Tools\\Mesura\\Mesura.exe\" --tray", pc.Registry[$@"HKCU\{StartupEntry.RunKey}\{StartupEntry.ValueName}"].Value);
        Assert.False(entry.PointsElsewhere(exe));
        Assert.True(entry.PointsElsewhere(@"D:\Mesura\Mesura.exe")); // Mesura moved: the old entry is shown as such
        entry.Set(@"D:\Mesura\Mesura.exe", on: true);                  // and turning it on corrects it
        Assert.True(entry.IsOn(@"D:\Mesura\Mesura.exe"));

        entry.Set(@"D:\Mesura\Mesura.exe", on: false);
        Assert.False(pc.Registry.ContainsKey($@"HKCU\{StartupEntry.RunKey}\{StartupEntry.ValueName}"));

        pc.User = new UserContext(null, "", "no interactive session");
        Assert.Throws<InvalidOperationException>(() => entry.Set(exe, on: true));
    }

    [Fact]
    public void Telemetry_tasks_are_turned_off_where_present_left_out_where_removed_and_turned_back_on_by_revert()
    {
        const string consolidator = @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator";
        const string appraiser = @"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip"; // removed by the image
        var op = Op("tasks.telemetry-off");
        Assert.True(op.NeedsElevation);
        Assert.True(op.MachineWide);
        var pc = new FakeWindows();
        foreach (var task in op.Tasks.Where(t => t.Path != appraiser))
        {
            pc.Tasks[task.Path] = FeatureState.Enabled; // a trimmed image: one task is gone, the rest still run
        }

        const string dmClient = @"\Microsoft\Windows\Feedback\Siuf\DmClient";
        const string unrelated = @"\Microsoft\Windows\Defrag\ScheduledDefrag";
        pc.Tasks[dmClient] = FeatureState.Disabled;  // already off before Mesura: its state must be kept, not "restored" to on
        pc.Tasks[unrelated] = FeatureState.Enabled;  // a Windows task the operation doesn't list

        var configurator = new WindowsConfigurator(pc, pc);
        Assert.Equal(OperationState.Partial, WindowsEvaluator.Evaluate(op, pc).State);
        var applied = configurator.Apply([op], TestContext.Current.CancellationToken).Single();

        Assert.Equal(WindowsOpStatus.Applied, applied.Status);
        Assert.Equal(FeatureState.Disabled, pc.Tasks[consolidator]);
        Assert.False(pc.Tasks.ContainsKey(appraiser));                  // absent: left out, never created
        Assert.Contains("not present", applied.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(op.Tasks.Count - 2, applied.Changes.Count);         // not the absent one, not the one already off
        Assert.DoesNotContain(applied.Changes, c => c.Path == dmClient);
        Assert.All(applied.Changes, c => Assert.Equal((ChangeKind.Task, "Enabled", "Disabled"), (c.Kind, c.Before, c.After)));
        Assert.All(pc.Writes, w => Assert.Contains(op.Tasks, t => w == "task " + t.Path)); // only listed tasks are written
        Assert.Equal(FeatureState.Enabled, pc.Tasks[unrelated]);
        Assert.Equal(OperationState.Applied, WindowsEvaluator.Evaluate(op, pc).State);

        pc.Tasks[consolidator] = FeatureState.Enabled;                  // turned back on by hand since: left alone
        var reverted = configurator.Revert([applied]).Single();
        Assert.Equal(WindowsOpStatus.Reverted, reverted.Status);
        Assert.All(op.Tasks.Where(t => t.Path != appraiser && t.Path != dmClient), t => Assert.Equal(FeatureState.Enabled, pc.Tasks[t.Path]));
        Assert.Equal(FeatureState.Disabled, pc.Tasks[dmClient]);         // its earlier "off" survives the revert
        Assert.False(pc.Tasks.ContainsKey(appraiser));
        Assert.Contains("Consolidator", reverted.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_task_whose_state_cannot_be_read_stops_the_operation_before_anything_changes()
    {
        var op = Op("tasks.telemetry-off");
        var pc = new FakeWindows();
        foreach (var task in op.Tasks)
        {
            pc.Tasks[task.Path] = FeatureState.Enabled;
        }

        pc.Tasks[op.Tasks[3].Path] = FeatureState.Unknown;

        var outcome = new WindowsConfigurator(pc, pc).Apply([op], TestContext.Current.CancellationToken).Single();

        Assert.Equal(WindowsOpStatus.Unknown, outcome.Status);
        Assert.Empty(pc.Writes);
        Assert.Empty(outcome.Changes);
    }

    [Theory]
    [InlineData(@"\Vendor\Updater")]
    [InlineData(@"\Microsoft\Windows")]
    [InlineData(@"Microsoft\Windows\Defrag\ScheduledDefrag")]
    public void A_catalog_task_outside_Windows_own_tasks_is_refused(string path)
    {
        var json = WindowsCatalog.BuiltInJson();
        var edited = json.Replace(@"\\Microsoft\\Windows\\Customer Experience Improvement Program\\Consolidator",
            path.Replace(@"\", @"\\", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.NotEqual(json, edited);
        var e = Assert.Throws<FormatException>(() => WindowsCatalog.Parse(edited));
        Assert.Contains("Windows' own tasks", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void No_preset_holds_two_operations_that_write_the_same_value_differently()
    {
        foreach (var preset in Catalog.Presets)
        {
            var writes = Catalog.Resolve(preset)
                .SelectMany(o => o.Registry.Select(r => (Key: $@"{r.Hive}\{r.Path}\{r.Name}".ToUpperInvariant(), r.Value, o.Id)))
                .GroupBy(w => w.Key)
                .Where(g => g.Select(w => w.Value).Distinct().Count() > 1)
                .Select(g => $"{preset.Id}: {string.Join(" vs ", g.Select(w => w.Id))}")
                .ToList();
            Assert.Empty(writes);
        }
    }

    [Fact]
    public void A_target_that_is_Windows_default_counts_a_missing_value_as_met_and_only_corrects_a_changed_one()
    {
        const string key = @"Software\Microsoft\GameBar";
        var op = WindowsCatalog.Parse("""
            { "kind": "Mesura.WindowsCatalog", "schemaVersion": 1, "operations": [
              { "id": "a", "name": "A", "description": "d", "risk": "userPreference",
                "registry": [ { "hive": "HKCU", "path": "Software\\Microsoft\\GameBar", "name": "AutoGameModeEnabled", "type": "dword", "value": "1", "absentMatches": true } ] } ] }
            """).Operations.Single();
        var pc = new FakeWindows();
        var configurator = new WindowsConfigurator(pc, pc);

        Assert.Equal(OperationState.Applied, WindowsEvaluator.Evaluate(op, pc).State); // a fresh install: nothing written yet
        Assert.Equal(WindowsOpStatus.AlreadyApplied, configurator.Apply([op], TestContext.Current.CancellationToken).Single().Status);
        Assert.Empty(pc.Writes);

        pc.Registry[$@"HKCU\{key}\AutoGameModeEnabled"] = (RegistryKind.DWord, "0"); // turned off by a tweak tool
        Assert.Equal(OperationState.NotApplied, WindowsEvaluator.Evaluate(op, pc).State);
        var applied = configurator.Apply([op], TestContext.Current.CancellationToken).Single();
        Assert.Equal(WindowsOpStatus.Applied, applied.Status);
        Assert.Equal("1", pc.Registry[$@"HKCU\{key}\AutoGameModeEnabled"].Value);

        Assert.Equal(WindowsOpStatus.Reverted, configurator.Revert([applied]).Single().Status);
        Assert.Equal("0", pc.Registry[$@"HKCU\{key}\AutoGameModeEnabled"].Value); // the exact earlier value comes back
    }

    [Fact]
    public void Power_settings_need_elevation_and_are_machine_wide()
    {
        var op = Op("power.disk-never-off");
        Assert.True(op.NeedsElevation);
        Assert.True(op.MachineWide);
        Assert.False(op.PerUser);
    }

    [Theory]
    [InlineData("""{ "id": "a", "name": "A", "description": "d", "risk": "userPreference" }""", "exactly one")]
    [InlineData("""{ "id": "a", "name": "A", "description": "d", "risk": "userPreference", "registry": [ { "hive": "HKCR", "path": "x", "name": "y", "type": "dword", "value": "1" } ] }""", "HKCU or HKLM")]
    [InlineData("""{ "id": "a", "name": "A", "description": "d", "risk": "userPreference", "registry": [ { "hive": "HKCU", "path": "x\\\\y", "name": "n", "type": "dword", "value": "1" } ] }""", "malformed")]
    [InlineData("""{ "id": "a", "name": "A", "description": "d", "risk": "userPreference", "registry": [ { "hive": "HKCU", "path": "x", "name": "n", "type": "dword", "value": "on" } ] }""", "not a number")]
    [InlineData("""{ "id": "a", "name": "A", "description": "d", "risk": "manual", "registry": [ { "hive": "HKCU", "path": "x", "name": "n", "type": "dword", "value": "1" } ] }""", "risk 'manual'")]
    public void Inconsistent_operations_are_rejected_with_a_pointed_message(string operation, string expected)
    {
        var json = "{ \"kind\": \"Mesura.WindowsCatalog\", \"schemaVersion\": 1, \"operations\": [ " + operation + " ] }";
        var e = Assert.Throws<FormatException>(() => WindowsCatalog.Parse(json));
        Assert.Contains(expected, e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "id": "a", "name": "A", "description": "d", "risk": "systemConfiguration", "powerPlan": { "scheme": "381b4222-f694-41f0-9685-ff5bb260df2e" }, "powerSettings": [ { "subgroup": "2a737441-1930-4402-8d77-b2bebba308a3", "setting": "48e6b7a6-50f5-4782-a5d4-53bb8f07e226", "ac": 0 } ] }""", "not both")]
    [InlineData("""{ "id": "a", "name": "A", "description": "d", "risk": "userPreference", "registry": [ { "hive": "HKCU", "path": "x", "name": "Flags", "type": "string", "value": "510", "clearBits": 4 } ] }""", "clears bits")]
    [InlineData("""{ "id": "a", "name": "A", "description": "d", "risk": "userPreference", "registry": [ { "hive": "HKCU", "path": "x", "name": "Flags", "type": "string", "value": "on", "clearBits": 4 } ] }""", "clears bits")]
    [InlineData("""{ "id": "a", "name": "A", "description": "d", "risk": "systemConfiguration", "requires": [ "laptop" ], "registry": [ { "hive": "HKLM", "path": "x", "name": "n", "type": "dword", "value": "1" } ] }""", "unknown requirement")]
    public void Catalog_extensions_are_validated_too(string operation, string expected) =>
        Inconsistent_operations_are_rejected_with_a_pointed_message(operation, expected);

    private const string Sticky = @"HKCU\Control Panel\Accessibility\StickyKeys";

    [Theory]
    [InlineData("510", "506")] // Windows' default: only the shortcut bit goes
    [InlineData("511", "507")] // Sticky Keys turned on by its user: stays on
    [InlineData(null, "506")]  // missing: the default minus the shortcut bit
    public void An_accessibility_shortcut_bit_is_cleared_and_every_other_bit_is_kept(string? before, string written)
    {
        var op = Op("input.accessibility-shortcuts-off");
        var pc = new FakeWindows();
        if (before is not null)
        {
            pc.Registry[$@"{Sticky}\Flags"] = (RegistryKind.String, before);
        }

        var configurator = new WindowsConfigurator(pc, pc);
        var applied = configurator.Apply([op], TestContext.Current.CancellationToken).Single();

        Assert.Equal(WindowsOpStatus.Applied, applied.Status);
        Assert.Equal((RegistryKind.String, written), pc.Registry[$@"{Sticky}\Flags"]);
        Assert.Equal(OperationState.Applied, WindowsEvaluator.Evaluate(op, pc).State);

        configurator.Revert([applied]);
        Assert.Equal(before is null, !pc.Registry.ContainsKey($@"{Sticky}\Flags"));
        if (before is not null)
        {
            Assert.Equal(before, pc.Registry[$@"{Sticky}\Flags"].Value); // the exact earlier value
        }
    }

    [Fact]
    public void A_bit_field_with_the_shortcut_already_off_is_applied_and_one_that_is_not_a_number_is_left_alone()
    {
        var op = Op("input.accessibility-shortcuts-off");
        var pc = new FakeWindows();
        foreach (var setting in op.Registry)
        {
            pc.Registry[$@"{setting.Hive}\{setting.Path}\{setting.Name}"] = (RegistryKind.String, "26"); // PC-000's Sticky Keys value
        }

        Assert.Equal(OperationState.Applied, WindowsEvaluator.Evaluate(op, pc).State);

        pc.Registry[$@"{Sticky}\Flags"] = (RegistryKind.String, "fast");
        pc.Registry[@"HKCU\Control Panel\Accessibility\ToggleKeys\Flags"] = (RegistryKind.String, "62");
        var outcome = new WindowsConfigurator(pc, pc).Apply([op], TestContext.Current.CancellationToken).Single();

        Assert.Equal(WindowsOpStatus.Failed, outcome.Status);
        Assert.Equal("fast", pc.Registry[$@"{Sticky}\Flags"].Value);
        Assert.Equal("62", pc.Registry[@"HKCU\Control Panel\Accessibility\ToggleKeys\Flags"].Value); // all or nothing
    }

    [Theory]
    [InlineData(true, false, OperationState.Inapplicable)]  // a laptop
    [InlineData(false, true, OperationState.Inapplicable)]  // a Modern Standby desktop: Windows offers only Balanced
    [InlineData(null, false, OperationState.Unknown)]       // can't tell: left alone
    [InlineData(false, false, OperationState.NotApplied)]   // a desktop
    public void Power_plans_apply_only_to_PCs_without_a_battery_or_Modern_Standby(bool? battery, bool? modernStandby, OperationState expected)
    {
        var pc = new FakeWindows { HasBattery = battery, ModernStandby = modernStandby };
        pc.Schemes.Add("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

        Assert.Equal(expected, WindowsEvaluator.Evaluate(Op("power.high-performance"), pc).State);
        var outcome = new WindowsConfigurator(pc, pc).Apply([Op("power.high-performance")], TestContext.Current.CancellationToken).Single();
        Assert.Equal(expected == OperationState.NotApplied ? WindowsOpStatus.Applied : expected == OperationState.Unknown ? WindowsOpStatus.Unknown : WindowsOpStatus.Inapplicable,
            outcome.Status);
    }

    [Fact]
    public void Operations_that_set_one_setting_two_ways_are_refused_together_and_the_rest_still_runs()
    {
        var pc = new FakeWindows();
        pc.Schemes.Add("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
        var ops = new[] { Op("taskbar.search-icon-only"), Op("taskbar.hide-task-view"), Op("taskbar.hide-search"), Op("power.balanced"), Op("power.high-performance") };

        var conflicts = WindowsConfigurator.Conflicts(ops);
        var outcomes = new WindowsConfigurator(pc, pc).Apply(ops, TestContext.Current.CancellationToken);

        Assert.Equal(2, conflicts.Count);
        Assert.Contains(conflicts, c => c.Contains("'taskbar.search-icon-only' and 'taskbar.hide-search'", StringComparison.Ordinal));
        Assert.All(outcomes.Where(o => o.OperationId != "taskbar.hide-task-view"), o => Assert.Equal(WindowsOpStatus.Failed, o.Status));
        Assert.Equal(WindowsOpStatus.Applied, outcomes.Single(o => o.OperationId == "taskbar.hide-task-view").Status);
        Assert.DoesNotContain(pc.Writes, w => w.Contains("SearchboxTaskbarMode", StringComparison.Ordinal) || w.StartsWith("activate", StringComparison.Ordinal));
        Assert.Empty(WindowsConfigurator.Conflicts([Op("taskbar.hide-search"), Op("taskbar.hide-task-view")]));
    }

    [Fact]
    public void A_write_that_takes_effect_and_then_fails_is_still_put_back()
    {
        var pc = new FakeWindows();
        pc.Registry[$@"{Theme}\AppsUseLightTheme"] = (RegistryKind.DWord, "1");
        pc.FailAfterWrite.Add($@"{Theme}\SystemUsesLightTheme");

        var outcome = new WindowsConfigurator(pc, pc).Apply([Op("appearance.dark-mode")], TestContext.Current.CancellationToken).Single();

        Assert.Equal(WindowsOpStatus.Failed, outcome.Status);
        Assert.Equal("1", pc.Registry[$@"{Theme}\AppsUseLightTheme"].Value);
        Assert.False(pc.Registry.ContainsKey($@"{Theme}\SystemUsesLightTheme")); // written, then the failure: removed again
        Assert.Empty(outcome.Changes);
    }

    [Fact]
    public void Reverting_a_revert_installs_the_power_scheme_again_before_making_it_active()
    {
        const string ultimate = "e9a42b02-d5df-448d-aa00-03f14749eb61";
        var pc = new FakeWindows();
        var configurator = new WindowsConfigurator(pc, pc);
        var applied = configurator.Apply([Op("power.ultimate-performance")], TestContext.Current.CancellationToken);
        var reverted = configurator.Revert(applied);
        Assert.DoesNotContain(ultimate, pc.Schemes);

        var again = configurator.Revert(reverted).Single();

        Assert.Equal(WindowsOpStatus.Reverted, again.Status);
        Assert.Contains(ultimate, pc.Schemes);
        Assert.Equal(ultimate, pc.Active);
    }

    [Fact]
    public void The_recommended_preset_holds_only_confirmed_settings_with_no_service_task_feature_power_or_hardware_condition()
    {
        var preset = Catalog.PresetById("recommended-gaming")!;
        var ops = Catalog.Resolve(preset);

        Assert.Equal(preset.OperationIds.Count, ops.Count);
        Assert.Equal("recommended-gaming", Catalog.Presets[0].Id); // shown first
        Assert.All(ops, o =>
        {
            Assert.True(o.Risk is RiskClass.UserPreference or RiskClass.SystemConfiguration, o.Id);
            Assert.True(o.PowerPlan is null && o.PowerSettings.Count == 0 && o.Services.Count == 0 && o.Features.Count == 0 && o.Tasks.Count == 0, o.Id);
            Assert.DoesNotContain(DeviceRequirement.NoBattery, o.Requirements);
            Assert.DoesNotContain(DeviceRequirement.NoModernStandby, o.Requirements);

            // A machine policy is there only where Windows honours it, so it is never written and ignored (Home).
            // (Edge and OneDrive read their own policy keys on every edition, so those need no Windows edition.)
            if (o.Registry.Any(r => r.Machine && r.Path.Contains("Policies", StringComparison.OrdinalIgnoreCase)
                    && !r.Path.Contains(@"Policies\Microsoft\Edge", StringComparison.OrdinalIgnoreCase) && !r.Path.Contains(@"Policies\Microsoft\OneDrive", StringComparison.OrdinalIgnoreCase)))
            {
                Assert.Contains(DeviceRequirement.ProEdition, o.Requirements);
            }
        });
        Assert.Empty(WindowsConfigurator.Conflicts(ops));
    }

    [Fact]
    public void Entries_known_only_from_the_community_are_in_no_profiles_automatic_selection()
    {
        // Documented, or verified read-only on a Windows build (docs/value-verification-2026-10.md). A community-only entry
        // stays selectable one by one and in the opt-in collections.
        foreach (var id in new[] { "recommended-gaming", "maximum-performance" })
        {
            Assert.All(Catalog.Resolve(Catalog.PresetById(id)!), o =>
                Assert.True(o.Basis is ValueBasis.Documented or ValueBasis.Verified, $"{o.Id} in {id} is {o.Basis?.ToString() ?? "not assessed"}"));
        }

        Assert.DoesNotContain(Catalog.Presets.Where(p => p.Tier == PresetTier.Primary), p => p.OperationIds.Contains("gaming.background-recording-off"));
        Assert.All(Catalog.Operations.Where(o => o.Basis == ValueBasis.Verified), o =>
            Assert.Contains("docs/value-verification-2026-10.md", o.Source, StringComparison.Ordinal)); // every 'verified' says where the check is
    }

    [Fact]
    public void On_an_untouched_Windows_the_recommended_defaults_and_repairs_already_read_as_done()
    {
        var pc = new FakeWindows();
        pc.Registry[@"HKLM\SYSTEM\CurrentControlSet\Services\NlaSvc\Parameters\Internet\EnableActiveProbing"] = (RegistryKind.DWord, "1");

        foreach (var id in new[] { "gaming.background-recording-off", "gaming.game-mode-on", "repair.ncsi-active-probing", "repair.threaded-dpc" })
        {
            Assert.Equal(OperationState.Applied, WindowsEvaluator.Evaluate(Op(id), pc).State);
        }

        pc.Unreadable.Add(@"HKCU\Software\Microsoft\GameBar\AutoGameModeEnabled"); // access denied is not "missing"
        Assert.Equal(OperationState.Unknown, WindowsEvaluator.Evaluate(Op("gaming.game-mode-on"), pc).State);

        pc.Registry[@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel\ThreadDpcEnable"] = (RegistryKind.DWord, "0"); // tweak residue
        var repaired = new WindowsConfigurator(pc, pc).Apply([Op("repair.threaded-dpc")], TestContext.Current.CancellationToken).Single();
        Assert.Equal(WindowsOpStatus.Applied, repaired.Status);
        Assert.Equal("1", pc.Registry[@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel\ThreadDpcEnable"].Value);
    }

    [Fact]
    public void A_write_refused_before_it_took_effect_leaves_no_record_of_a_change()
    {
        var pc = new FakeWindows();
        pc.Registry[$@"{Theme}\AppsUseLightTheme"] = (RegistryKind.DWord, "1");
        pc.RefuseWrite.Add($@"{Theme}\AppsUseLightTheme");

        var outcome = new WindowsConfigurator(pc, pc).Apply([Op("appearance.dark-mode")], TestContext.Current.CancellationToken).Single();

        Assert.Equal(WindowsOpStatus.Failed, outcome.Status);
        Assert.Empty(outcome.Changes);                                         // nothing changed, so nothing to revert later
        Assert.DoesNotContain("couldn't be put back", outcome.Detail, StringComparison.Ordinal);
        Assert.Equal("1", pc.Registry[$@"{Theme}\AppsUseLightTheme"].Value);
    }

    [Fact]
    public void An_operation_that_cant_act_on_this_PC_conflicts_with_nothing()
    {
        var laptop = new FakeWindows { HasBattery = true };
        laptop.Schemes.Add("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
        var ops = new[] { Op("power.high-performance"), Op("power.balanced") };

        Assert.Single(WindowsConfigurator.Conflicts(ops));                  // as a selection, anywhere
        Assert.Empty(WindowsConfigurator.Conflicts(ops, laptop));            // here High performance doesn't apply
        var outcomes = new WindowsConfigurator(laptop, laptop).Apply(ops, TestContext.Current.CancellationToken);
        Assert.Equal(WindowsOpStatus.Inapplicable, outcomes.Single(o => o.OperationId == "power.high-performance").Status);
        Assert.Equal(WindowsOpStatus.AlreadyApplied, outcomes.Single(o => o.OperationId == "power.balanced").Status);
    }
}
