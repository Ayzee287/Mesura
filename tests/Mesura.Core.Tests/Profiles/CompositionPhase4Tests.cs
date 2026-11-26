using Mesura.Core.Apps;
using Mesura.Core.Hardware;
using Mesura.Core.Profiles;
using Mesura.Core.Setup;
using Mesura.Core.Tests.Windows;
using Mesura.Core.Windows;

namespace Mesura.Core.Tests.Profiles;

/// <summary>
/// The 0.9 Phase 4 composition (vault D058, docs/plan-0.9-phase4.md) pinned and planned on five synthetic machines (7950X3D, 9800X3D, 13700K, a hybrid laptop,
/// an unreadable CPU) through every entry point. Stand-ins only: nothing touches a real PC, and none of it is hardware validation.
/// </summary>
public sealed class CompositionPhase4Tests
{
    private const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private static readonly WindowsCatalog Catalog = WindowsCatalog.Default;
    private static readonly OptimizationProfiles Profiles = OptimizationProfiles.Default;

    private static OptimizationProfile Reference => Profiles.ById(LevelPlanning.ReferenceId)!;

    private static OptimizationProfile Maximum => Profiles.ById(LevelPlanning.MaximumId)!;

    private static readonly string[] PromotedApps = ["apps.remove-promoted-games", "apps.remove-promoted-streaming", "apps.remove-promoted-social"];

    // Owner, 2026-11-26 (D061, superseding D058 for these three): the promotional app groups are optional, unticked, in both levels, because Mesura can't
    // tell a copy Windows installed from one the user installed from the Store and uses.
    private static readonly string[] MaximumDefaultApps =
    [
        "apps.remove-bing-news", "apps.remove-bing-weather", "apps.remove-bing-search", "apps.remove-tips", "apps.remove-office-hub", "apps.remove-solitaire",
        "apps.remove-people", "apps.remove-power-automate", "apps.remove-feedback-hub", "apps.remove-maps", "apps.remove-clipchamp", "apps.remove-cortana", "apps.remove-skype",
        "apps.remove-mixed-reality", "apps.remove-3d-viewer", "apps.remove-mobile-plans", "apps.remove-dev-home", "apps.remove-copilot-app",
    ];

    private static readonly string[] MaximumOptionalApps =
        [.. PromotedApps, "apps.remove-get-help", "apps.remove-xbox-app", "apps.remove-game-bar", "apps.remove-edge-game-assist", "apps.remove-phone-link", "apps.remove-teams"];

    private static readonly string[] KeptOut =
    [
        "apps.remove-photos", "apps.remove-media-player", "apps.remove-movies-tv", "apps.remove-sticky-notes", "apps.remove-todo", "apps.remove-onenote", "apps.remove-family",
        "apps.remove-quick-assist", "apps.remove-outlook-new",
        // Owner, 2026-11-26 (D059): Print Spooler is not added, and OneDrive's sync policy is not used (the client removal the owner wants is withheld); the
        // withheld and retired operations; the Advanced-only ones.
        "services.print-spooler-off", "services.print-on", "system.onedrive-sync-off", "security.mark-of-the-web-off",
        "power.high-performance", "power.ultimate-performance", "network.adapter-ipv6-binding-off", "network.ipv6-6to4-isatap-off",
    ];

    private static EntryRole? Role(OptimizationProfile profile, string id) => profile.Entries.SingleOrDefault(e => e.OperationId == id)?.Role;

    // ---- the composition, pinned -------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Reference_is_the_old_reference_plus_the_ceip_and_feedback_tasks_and_offers_the_three_promotional_app_groups_unticked()
    {
        foreach (var id in new[] { "tasks.ceip-off", "tasks.feedback-off" })
        {
            Assert.Equal(EntryRole.Default, Role(Reference, id));
        }

        Assert.All(PromotedApps, id => Assert.Equal(EntryRole.Optional, Role(Reference, id)));
        Assert.All(PromotedApps, id => Assert.Equal(EntryRole.Optional, Role(Maximum, id)));
        // The reason says why, and the consequence names the user's own Store copy and the way back.
        Assert.All(PromotedApps, id => Assert.Contains("can't tell them from a copy you installed from the Store", Reference.Entries.Single(e => e.OperationId == id).Why));
        Assert.All(PromotedApps, id => Assert.Contains("Undo brings it back only through the Store", Catalog.ById(id)!.Limitations));

        Assert.Equal(["apps"], Reference.Entries.Where(e => e.OperationId.StartsWith("apps.", StringComparison.Ordinal)).Select(e => e.GroupId).Distinct());
        // Still no service, no component, no security operation in Reference.
        Assert.DoesNotContain(Reference.Entries, e => Catalog.ById(e.OperationId) is { } op && (op.Services.Count > 0 || op.Features.Count > 0 || op.Category == "Security"));
        Assert.Equal(["telemetry.onesettings-downloads-off", .. PromotedApps], Reference.OptionalIds);
        // The catalog preset lists mirror the roles.
        Assert.All(PromotedApps, id => Assert.DoesNotContain(id, Catalog.PresetById("recommended-gaming")!.OperationIds));
        Assert.All(PromotedApps, id => Assert.Contains(id, Catalog.PresetById("recommended-gaming")!.OptionalIds));
        Assert.All(PromotedApps, id => Assert.DoesNotContain(id, Catalog.PresetById("maximum-performance")!.OperationIds));
        Assert.All(PromotedApps, id => Assert.Contains(id, Catalog.PresetById("maximum-performance")!.OptionalIds));
    }

    [Fact]
    public void Maximum_adds_the_reviewed_layers_with_the_reviewed_roles()
    {
        Assert.All(MaximumDefaultApps, id => Assert.Equal(EntryRole.Default, Role(Maximum, id)));
        Assert.All(MaximumOptionalApps, id => Assert.Equal(EntryRole.Optional, Role(Maximum, id)));
        Assert.Equal(MaximumDefaultApps.Length + MaximumOptionalApps.Length, Maximum.Entries.Count(e => e.OperationId.StartsWith("apps.", StringComparison.Ordinal)));

        Assert.All(new[] { "features.legacy-media-off", "features.xps-off", "features.powershell-v2-off", "tasks.ceip-off", "tasks.feedback-off" },
            id => Assert.Equal(EntryRole.Default, Role(Maximum, id)));
        Assert.All(new[] { "network.smb1-off", "features.recall-off", "features.virtual-machine-platform-off", "services.windows-search-off", "network.prefer-ipv4", "security.smartscreen-off",
                "services.diagnostic-tracking-off" },
            id => Assert.Equal(EntryRole.Optional, Role(Maximum, id)));
        Assert.Null(Role(Reference, "services.diagnostic-tracking-off"));
        Assert.Equal("background", Maximum.Entries.Single(e => e.OperationId == "services.diagnostic-tracking-off").GroupId);
        Assert.Equal("security", Maximum.Entries.Single(e => e.OperationId == "security.smartscreen-off").GroupId);
        Assert.All(KeptOut, id => Assert.DoesNotContain(Profiles.Profiles.SelectMany(p => p.Entries), e => e.OperationId == id));

        // Every entry that removes or gives up something says it has a cost.
        Assert.All(Maximum.Entries.Where(e => e.OperationId.StartsWith("apps.", StringComparison.Ordinal) || e.GroupId is "components" or "background" or "security"),
            e => Assert.True(e.SideEffects, e.OperationId));
    }

    // ---- the owner's review corrections of 2026-11-26 (D059) --------------------------------------------------------------------------------------------------

    [Fact]
    public void Diagtrack_says_it_is_one_telemetry_path_and_restores_its_exact_start_type_is_idempotent_and_recovers_after_a_crash()
    {
        var op = Catalog.ById("services.diagnostic-tracking-off")!;
        Assert.Equal(["DiagTrack"], op.Services.Select(s => s.Name));                 // its whole scope: one service, nothing else
        Assert.Contains("not all of Windows' telemetry", op.Limitations, StringComparison.Ordinal);
        Assert.Contains("Windows Error Reporting", op.Limitations, StringComparison.Ordinal);
        Assert.Equal(Activation.Reboot, op.Activation);
        Assert.DoesNotContain("faster", op.Description + op.Summary, StringComparison.OrdinalIgnoreCase);

        var pc = new FakeWindows();
        pc.Services["DiagTrack"] = 2;
        var applied = new WindowsConfigurator(pc, pc).Apply([op], TestContext.Current.CancellationToken).Single();
        Assert.Equal((WindowsOpStatus.Applied, 4), (applied.Status, pc.Services["DiagTrack"]));
        Assert.Equal(("2", "4"), (Assert.Single(applied.Changes).Before, applied.Changes[0].After));
        Assert.Equal(WindowsOpStatus.AlreadyApplied, new WindowsConfigurator(pc, pc).Apply([op], TestContext.Current.CancellationToken).Single().Status);
        Assert.Equal(WindowsOpStatus.Reverted, new WindowsConfigurator(pc, pc, Catalog).Revert([applied]).Single().Status);
        Assert.Equal(2, pc.Services["DiagTrack"]);

        // A PC whose image removed the service: not applicable, nothing written.
        var stripped = new FakeWindows();
        Assert.Equal(OperationState.Inapplicable, WindowsEvaluator.Evaluate(op, stripped).State);

        // Journal first: a crash at the write recovers to nothing left to undo; a crash after it leaves an undoable record.
        var early = new FakeWindows { CrashAtWrite = 1 };
        early.Services["DiagTrack"] = 2;
        var journal = new MemoryJournal();
        Assert.Throws<SimulatedCrash>(() => new WindowsConfigurator(early, early).Apply([op], TestContext.Current.CancellationToken, journal));
        early.CrashAtWrite = null;
        Assert.Null(RunJournal.Recover(RunJournal.Parse(journal.Text)!, new WindowsConfigurator(early, early).Check, null, "2026-11-26T10:00:00.0000000Z", out _));

        var late = new FakeWindows();
        late.Services["DiagTrack"] = 2;
        var lateJournal = new MemoryJournal();
        new WindowsConfigurator(late, late).Apply([op], TestContext.Current.CancellationToken, lateJournal);
        var record = RunJournal.Recover(RunJournal.Parse(lateJournal.Text)!, new WindowsConfigurator(late, late).Check, null, "2026-11-26T10:00:00.0000000Z", out _);
        Assert.NotNull(record);
        Assert.All(new WindowsConfigurator(late, late).Revert(record.WindowsSteps), o => Assert.Equal(WindowsOpStatus.Reverted, o.Status));
        Assert.Equal(2, late.Services["DiagTrack"]);
    }

    [Theory]
    [MemberData(nameof(MachineNames))]
    public void Diagtrack_is_never_applied_by_maximum_unless_ticked_and_print_spooler_is_never_touched_by_either_level(string machine)
    {
        var m = ByName(machine);
        var pc = Pc(m);
        pc.Services["DiagTrack"] = 2;
        pc.Services["Spooler"] = 2;
        pc.Services["PrintNotify"] = 3;

        var untouched = LevelPlanning.Plan(Maximum, Context(m, pc)).OperationIds;
        Assert.DoesNotContain("services.diagnostic-tracking-off", untouched);
        Assert.Contains("services.diagnostic-tracking-off", LevelPlanning.Plan(Maximum, Context(m, pc), AllOptionalTicked(Maximum)).OperationIds);
        Assert.DoesNotContain("services.diagnostic-tracking-off", LevelPlanning.Plan(Reference, Context(m, pc), AllOptionalTicked(Reference)).OperationIds);

        // With every option ticked in both levels, nothing either applies names a print service.
        foreach (var profile in new[] { Reference, Maximum })
        {
            var ops = LevelPlanning.Plan(profile, Context(m, pc), AllOptionalTicked(profile)).OperationIds.Select(id => Catalog.ById(id)!);
            Assert.DoesNotContain(ops, o => o.Services.Any(s => s.Name is "Spooler" or "PrintNotify"));
        }
    }

    [Fact]
    public void Onedrive_removal_is_withheld_and_neither_the_sync_policy_nor_any_onedrive_operation_is_in_a_profile()
    {
        // The owner wants the OneDrive client removed, not sync turned off. Withheld (docs/plan-0.9-phase4.md, D059): Microsoft documents no uninstall command and no
        // built-in reinstall (Undo would be a download of the current version, not the one removed), and the per-user uninstaller lives in a folder the user can
        // change, which an elevated Mesura never runs. No profile holds anything OneDrive instead.
        Assert.DoesNotContain(Profiles.Profiles.SelectMany(p => p.Entries), e => e.OperationId.Contains("onedrive", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(Catalog.Operations, o => o.Id.Contains("onedrive", StringComparison.OrdinalIgnoreCase) && o.Id.Contains("remove", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Get_help_is_optional_because_windows_11_troubleshooters_run_through_it_and_its_text_now_says_so()
    {
        Assert.Equal(EntryRole.Optional, Role(Maximum, "apps.remove-get-help"));
        var op = Catalog.ById("apps.remove-get-help")!;
        Assert.Contains("troubleshooters", op.Limitations, StringComparison.Ordinal);
        Assert.DoesNotContain("troubleshooters stay", op.Limitations, StringComparison.Ordinal);   // the 0.8 text, which Microsoft's MSDT deprecation contradicts
    }

    [Fact]
    public void Every_operation_a_profile_applies_or_offers_is_in_the_catalog_with_an_assessed_basis_and_both_languages_have_its_reason()
    {
        foreach (var entry in Profiles.Profiles.SelectMany(p => p.Entries))
        {
            var op = Catalog.ById(entry.OperationId);
            Assert.NotNull(op);
            Assert.True(op.Basis is not null || op.Effect is not null, $"{entry.OperationId} has no assessed basis");
            Assert.False(string.IsNullOrWhiteSpace(entry.Why), entry.OperationId);
        }
    }

    // ---- the five synthetic machines ---------------------------------------------------------------------------------------------------------------------------

    private sealed record Machine(string Name, CpuProfile Cpu, DeviceForm Form, bool Battery, bool Hybrid);

    private static long Mb(int n) => (long)n << 20;

    private static readonly Machine[] Machines =
    [
        new("7950X3D", new CpuProfile("AMD Ryzen 9 7950X3D 16-Core Processor", CpuMaker.Amd, 16, 32, false, [new CacheGroup(3, Mb(96), 16), new CacheGroup(3, Mb(32), 16)]), DeviceForm.Desktop, false, false),
        new("9800X3D", new CpuProfile("AMD Ryzen 7 9800X3D 8-Core Processor", CpuMaker.Amd, 8, 16, false, [new CacheGroup(3, Mb(96), 16)]), DeviceForm.Desktop, false, false),
        new("13700K", new CpuProfile("13th Gen Intel(R) Core(TM) i7-13700K", CpuMaker.Intel, 16, 24, true, [new CacheGroup(3, Mb(30), 24)]), DeviceForm.Desktop, false, true),
        new("hybrid-laptop", new CpuProfile("Intel(R) Core(TM) Ultra 7 155H", CpuMaker.Intel, 16, 22, true, [new CacheGroup(3, Mb(24), 22)]), DeviceForm.Portable, true, true),
        new("unread-cpu", new CpuProfile(null, CpuMaker.Unknown, null, null, null), DeviceForm.Desktop, false, false),
    ];

    public static TheoryData<string> MachineNames() => [.. Machines.Select(m => m.Name)];

    private static Machine ByName(string name) => Machines.Single(m => m.Name == name);

    private static HardwareProfile Hardware(Machine m) => new(
        new WindowsPlatform(26100, "Professional", "24H2", true), m.Form, new PowerPlatform(m.Battery, false, true), m.Cpu, 32UL << 30,
        [new GpuProfile("Test GPU", GpuMaker.Nvidia, 0x10DE, 1, "1.0", false)],
        [new DiskProfile("NVMe", StorageClass.Nvme, 1, true, Mesura.Core.Raw.BusKind.Nvme)],
        [new NetworkProfile("Ethernet", AdapterKind.Ethernet, "Realtek", true)]);

    /// <summary>A PC with every app, task, service and feature the profiles touch present and on, and every power setting registered at a value nobody wants.</summary>
    private static FakeWindows Pc(Machine m)
    {
        var pc = new FakeWindows { HasBattery = m.Battery, ModernStandby = false, HybridCpu = m.Hybrid, PolicyEdition = true, PhysicalMemoryBytes = 32UL << 30 };
        var ops = Profiles.Profiles.SelectMany(p => p.Entries).Select(e => Catalog.ById(e.OperationId)!).DistinctBy(o => o.Id).ToList();
        foreach (var setting in ops.SelectMany(o => o.PowerSettings).Select(s => s.Setting).Concat(ops.Where(o => o.PowerPlan is not null).SelectMany(o => o.PowerPlan!.PlanValues).Select(v => v.Setting)))
        {
            pc.PowerSettings[$"{Balanced}|{setting}"] = 7;
            pc.PowerSettingsDc[$"{Balanced}|{setting}"] = 42;
        }

        foreach (var app in ops.SelectMany(o => o.AppxSettings))
        {
            pc.Apps.Add($"{app.Name}_1.0.0.0_x64__8wekyb3d8bbwe");
        }

        foreach (var task in ops.SelectMany(o => o.Tasks))
        {
            pc.Tasks[task.Path] = FeatureState.Enabled;
        }

        foreach (var service in ops.SelectMany(o => o.Services))
        {
            pc.Services[service.Name] = 2;
        }

        foreach (var feature in ops.SelectMany(o => o.Features))
        {
            pc.Features[feature.Name] = FeatureState.Enabled;
        }

        pc.System["mmagent:memorycompression"] = "enabled";
        return pc;
    }

    private static PlanningContext Context(Machine m, FakeWindows pc) => PlanningContext.Reading(Catalog, Hardware(m), pc);

    private static ProfileChoices AllOptionalTicked(OptimizationProfile profile) =>
        new(new HashSet<string>(), profile.OptionalIds.ToDictionary(id => id, _ => true));

    [Theory]
    [MemberData(nameof(MachineNames))]
    public void Every_entry_point_gives_the_same_plan_for_both_levels_on_every_machine(string machine)
    {
        var m = ByName(machine);
        var pc = Pc(m);
        var context = Context(m, pc);
        foreach (var level in new[] { LevelPlanning.ReferenceId, LevelPlanning.MaximumId })
        {
            var optimize = LevelPlanning.Plan(Catalog.PresetById(level)!, context).OperationIds;
            Assert.Equal(optimize, LevelPlanning.Plan(Profiles.ById(level)!, context).OperationIds);
            Assert.Equal(optimize, LevelPlanning.Resolve([level], [], context, ExplicitOperations.AsNamed).Operations.Select(o => o.Id));
            Assert.Equal(optimize, new SetupProfile("s", "", [], [], null, [], [], [level]).ResolveWindows(context).Operations.Select(o => o.Id));
            Assert.Empty(WindowsConfigurator.Conflicts(optimize.Select(id => Catalog.ById(id)!), pc));
        }
    }

    [Theory]
    [MemberData(nameof(MachineNames))]
    public void With_every_optional_entry_ticked_maximum_still_has_no_conflict_and_the_x3d_policy_still_holds(string machine)
    {
        var m = ByName(machine);
        var pc = Pc(m);
        var plan = LevelPlanning.Plan(Maximum, Context(m, pc), AllOptionalTicked(Maximum));
        var ids = plan.OperationIds;

        Assert.Empty(WindowsConfigurator.Conflicts(ids.Select(id => Catalog.ById(id)!), pc));
        Assert.Contains("security.smartscreen-off", ids);                    // ticked on purpose: applies (Pro here)
        Assert.Contains("apps.remove-teams", ids);

        switch (m.Cpu.DualCcdX3d)
        {
            case true:   // 7950X3D: Game Bar removal and the other X3D-restricted entries stay out even when ticked
                Assert.Equal(EntryState.Unsupported, plan.Entry("apps.remove-game-bar")!.State);
                Assert.DoesNotContain(ids, id => id is "apps.remove-game-bar" or "cpu.core-parking-off" or "power.mesura-maximum-plan" or "gaming.game-recording-policy-off");
                Assert.Equal("power.mesura-balanced-plan", ids[0]);
                break;
            case false:
                Assert.Contains("apps.remove-game-bar", ids);
                Assert.Equal("power.mesura-maximum-plan", ids[0]);
                break;
            default:     // unreadable: left alone, never guessed
                Assert.Equal(EntryState.CantTell, plan.Entry("apps.remove-game-bar")!.State);
                Assert.DoesNotContain(ids, id => id is "apps.remove-game-bar" or "power.mesura-maximum-plan" or "power.mesura-balanced-plan" or "cpu.core-parking-off");
                break;
        }
    }

    [Theory]
    [MemberData(nameof(MachineNames))]
    public void Untouched_maximum_applies_no_optional_entry_and_the_app_and_component_layers_apply_on_every_machine(string machine)
    {
        var m = ByName(machine);
        var ids = LevelPlanning.Plan(Maximum, Context(m, Pc(m))).OperationIds;

        Assert.DoesNotContain(ids, Maximum.OptionalIds.Contains);
        Assert.All(MaximumDefaultApps, id => Assert.Contains(id, ids));     // not hardware-conditioned: the same on every machine, unread CPU included
        Assert.All(new[] { "features.legacy-media-off", "features.xps-off", "features.powershell-v2-off", "tasks.ceip-off", "tasks.feedback-off" }, id => Assert.Contains(id, ids));
    }

    [Fact]
    public void Reference_is_the_same_on_every_machine_and_never_touches_x3d_restricted_settings()
    {
        var plans = Machines.Select(m => LevelPlanning.Plan(Reference, Context(m, Pc(m))).OperationIds).ToList();
        Assert.All(plans, p => Assert.Equal(plans[0], p));
        Assert.All(plans, p => Assert.DoesNotContain(p, id => Profiles.RestrictionsFor(id).Count > 0));
        Assert.Contains("power.mesura-balanced-plan", plans[0]);
    }

    [Fact]
    public void On_windows_home_smartscreen_off_does_not_apply_even_when_ticked_and_on_another_account_the_app_removals_are_left_alone()
    {
        var m = ByName("13700K");
        var home = Pc(m);
        home.PolicyEdition = false;
        var ticked = new ProfileChoices(new HashSet<string>(), new Dictionary<string, bool> { ["security.smartscreen-off"] = true });
        var plan = LevelPlanning.Plan(Maximum, Context(m, home), ticked);
        Assert.Equal((EntryState.Unsupported, PlanReason.Edition), (plan.Entry("security.smartscreen-off")!.State, plan.Entry("security.smartscreen-off")!.Reason));

        var other = Pc(m);
        other.User = new UserContext("S-1-5-21-1-1-1-1001", "PC-owner", "", ProcessIsUser: false);
        var asOther = LevelPlanning.Plan(Maximum, Context(m, other));
        Assert.All(MaximumDefaultApps, id => Assert.Equal((EntryState.CantTell, PlanReason.OtherAccount), (asOther.Entry(id)!.State, asOther.Entry(id)!.Reason)));
        Assert.DoesNotContain(asOther.OperationIds, id => id.StartsWith("apps.", StringComparison.Ordinal));
    }

    [Fact]
    public void A_saved_setup_naming_maximum_and_game_bar_removal_on_a_7950X3D_removes_the_apps_and_leaves_game_bar()
    {
        var m = ByName("7950X3D");
        var setup = SetupProfile.Parse("""
            { "kind": "Mesura.SetupProfile", "schemaVersion": 1, "name": "saved on another PC",
              "windowsPresets": [ "maximum-performance" ], "windows": [ "apps.remove-game-bar", "apps.remove-xbox-app" ] }
            """, AppCatalog.Default, Catalog);

        var resolution = setup.ResolveWindows(Context(m, Pc(m)));
        var ids = resolution.Operations.Select(o => o.Id).ToList();

        Assert.All(MaximumDefaultApps, id => Assert.Contains(id, ids));
        Assert.All(PromotedApps, id => Assert.DoesNotContain(id, ids));       // optional: a level named in a setup applies its defaults only
        Assert.Contains("apps.remove-xbox-app", ids);                      // named on its own, no hardware condition: applied as named
        Assert.DoesNotContain("apps.remove-game-bar", ids);                 // the X3D restriction holds for an id a setup names
        Assert.Contains(resolution.NotHere, e => e.OperationId == "apps.remove-game-bar" && e.Reason == "hardware.cpu.not-x3d-dual-ccd");
    }

    [Fact]
    public void The_whole_maximum_plan_with_the_new_layers_applies_and_undoes_to_the_exact_start_through_the_engine()
    {
        var m = ByName("13700K");
        var pc = Pc(m);
        var apps = pc.Apps.Order(StringComparer.Ordinal).ToList();
        var tasks = pc.Tasks.ToDictionary(p => p.Key, p => p.Value);
        var features = pc.Features.ToDictionary(p => p.Key, p => p.Value);
        var ops = LevelPlanning.Plan(Maximum, Context(m, pc)).OperationIds.Select(id => Catalog.ById(id)!).ToList();

        var outcomes = new WindowsConfigurator(pc, pc).Apply(ops, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(outcomes, o => !o.Ok);
        Assert.DoesNotContain(pc.Apps, a => a.StartsWith("Microsoft.BingNews_", StringComparison.Ordinal));
        Assert.Contains(pc.Apps, a => a.StartsWith("Microsoft.GamingApp_", StringComparison.Ordinal));   // the Xbox app is optional: kept
        Assert.All(Catalog.ById("features.xps-off")!.Features, f => Assert.Equal(FeatureState.Disabled, pc.Features[f.Name]));

        var reverted = new WindowsConfigurator(pc, pc, Catalog).Revert(outcomes);
        Assert.DoesNotContain(reverted, r => r.Status is WindowsOpStatus.Failed or WindowsOpStatus.Blocked);
        Assert.Equal(tasks.OrderBy(p => p.Key), pc.Tasks.OrderBy(p => p.Key));
        Assert.Equal(features.OrderBy(p => p.Key), pc.Features.OrderBy(p => p.Key));
        // Each removed app is registered again from Windows' staged copy (the stand-in gives it a new version number, as Windows may).
        Assert.Equal(apps.Select(a => a.Split('_')[0]).Order(StringComparer.Ordinal), pc.Apps.Select(a => a.Split('_')[0]).Order(StringComparer.Ordinal));
    }
}
