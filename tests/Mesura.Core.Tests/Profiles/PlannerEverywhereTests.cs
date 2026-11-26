using Mesura.Core.Apps;
using Mesura.Core.Hardware;
using Mesura.Core.Profiles;
using Mesura.Core.Setup;
using Mesura.Core.Tests.Windows;
using Mesura.Core.Windows;

namespace Mesura.Core.Tests.Profiles;

/// <summary>
/// Planner Everywhere (0.9, vault D054 and the owner's Phase 1 decisions of 2026-11-25, superseding decision O1 of 2026-11-03): every path that applies a
/// level plans it for the PC through <see cref="LevelPlanning"/>. Every PC, CPU and profile here is a synthetic stand-in: nothing is read from or written to
/// a real PC, and the dual-CCD X3D fixture is not hardware validation (that is a separate gate). The X3D profile is test-only: <c>profiles.json</c> is
/// unchanged in this phase.
/// </summary>
public sealed class PlannerEverywhereTests
{
    private const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private static readonly WindowsCatalog Catalog = WindowsCatalog.Default;
    private static readonly OptimizationProfiles Profiles = OptimizationProfiles.Default;

    private static WindowsPreset Preset(string id) => Catalog.PresetById(id)!;

    /// <summary>A PC whose Balanced scheme registers every power setting any profile touches, at a value none of them wants (as in the planner tests).</summary>
    private static FakeWindows Pc(bool battery = false, GpuFeatureReading? gpu = null)
    {
        var pc = new FakeWindows { HasBattery = battery, ModernStandby = false, PolicyEdition = true, PhysicalMemoryBytes = 32UL << 30, Gpu = gpu };
        var ops = Profiles.Profiles.SelectMany(p => p.Entries).Select(e => Catalog.ById(e.OperationId)!).DistinctBy(o => o.Id).ToList();
        foreach (var setting in ops.SelectMany(o => o.PowerSettings))
        {
            pc.PowerSettings[$"{Balanced}|{setting.Setting}"] = 7;
            pc.PowerSettingsDc[$"{Balanced}|{setting.Setting}"] = 42;
        }

        foreach (var value in ops.Where(o => o.PowerPlan is not null).SelectMany(o => o.PowerPlan!.PlanValues))
        {
            pc.PowerSettings[$"{Balanced}|{value.Setting}"] = 7;
            pc.PowerSettingsDc[$"{Balanced}|{value.Setting}"] = 42;
        }

        pc.System["mmagent:memorycompression"] = "enabled";
        return pc;
    }

    private static CpuProfile IntelCpu => new("Test CPU", CpuMaker.Intel, 8, 16, false);

    /// <summary>A synthetic dual-CCD Ryzen X3D: two L3 caches of clearly different size (96 MB and 32 MB), as <see cref="CpuTopology"/> reads them.</summary>
    private static CpuProfile DualCcdX3d => new("AMD Ryzen 9 7950X3D", CpuMaker.Amd, 16, 32, false, [new CacheGroup(3, 96L << 20, 16), new CacheGroup(3, 32L << 20, 16)]);

    /// <summary>A synthetic two-die Ryzen without stacked cache: two equal L3 caches.</summary>
    private static CpuProfile TwoDieRyzen => new("AMD Ryzen 9 7950X", CpuMaker.Amd, 16, 32, false, [new CacheGroup(3, 32L << 20, 16), new CacheGroup(3, 32L << 20, 16)]);

    /// <summary>A processor nothing could be read about.</summary>
    private static CpuProfile UnreadCpu => new(null, CpuMaker.Unknown, null, null, null);

    private static HardwareProfile Hardware(DeviceForm form = DeviceForm.Desktop, bool nvme = true, GpuMaker gpu = GpuMaker.Nvidia, CpuProfile? cpu = null) => new(
        new WindowsPlatform(26100, "Professional", "24H2", true), form, new PowerPlatform(form == DeviceForm.Portable, false, true),
        cpu ?? IntelCpu, 32UL << 30,
        [new GpuProfile("Test GPU", gpu, gpu == GpuMaker.Amd ? 0x1002 : 0x10DE, 1, "1.0", false)],
        nvme ? [new DiskProfile("NVMe", StorageClass.Nvme, 1, true, Mesura.Core.Raw.BusKind.Nvme)] : [new DiskProfile("SATA", StorageClass.Ssd, 1, true, Mesura.Core.Raw.BusKind.Sata)],
        [new NetworkProfile("Ethernet", AdapterKind.Ethernet, "Realtek", true)]);

    private static SetupProfile Setup(string[] presets, string[]? windows = null) =>
        new("test", "", [], [], null, [], windows ?? [], presets);

    // ---- one planner, every entry point -----------------------------------------------------------------------------------------------------------------------

    public static TheoryData<string, string> PcsAndLevels()
    {
        var data = new TheoryData<string, string>();
        foreach (var pc in new[] { "desktop-nvme-nvidia", "laptop-sata", "radeon-desktop", "hardware-unread" })
        {
            foreach (var level in new[] { LevelPlanning.ReferenceId, LevelPlanning.MaximumId })
            {
                data.Add(pc, level);
            }
        }

        return data;
    }

    private static (FakeWindows Pc, HardwareProfile? Hardware) Machine(string name) => name switch
    {
        "desktop-nvme-nvidia" => (Pc(), Hardware()),
        "laptop-sata" => (Pc(battery: true), Hardware(DeviceForm.Portable, nvme: false)),
        "radeon-desktop" => (Pc(gpu: new GpuFeatureReading(GpuAvailability.Available, "AMD Radeon RX 9070 XT", 0, true, false, 0, null)), Hardware(gpu: GpuMaker.Amd)),
        _ => (Pc(), null),
    };

    [Theory]
    [MemberData(nameof(PcsAndLevels))]
    public void Optimize_the_profile_option_the_preset_option_and_a_saved_setup_apply_the_same_operations(string machine, string level)
    {
        var (pc, hardware) = Machine(machine);
        var context = PlanningContext.Reading(Catalog, hardware, pc);

        var optimize = LevelPlanning.Plan(Preset(level), context).OperationIds;                                          // the window's plan, no choices
        var profileOption = LevelPlanning.Plan(Profiles.ById(level)!, context).OperationIds;                             // windows apply --profile <level>
        var presetOption = LevelPlanning.Resolve([level], [], context, ExplicitOperations.AsNamed).Operations.Select(o => o.Id);   // windows apply --preset <level>
        var savedSetup = Setup([level]).ResolveWindows(context).Operations.Select(o => o.Id);                           // a saved PC setup (app and profile run)
        var direct = ProfilePlanner.Plan(Profiles.ById(level)!, Catalog, hardware, pc, id => Catalog.ById(id) is { } op ? WindowsEvaluator.Evaluate(op, pc) : null).OperationIds;

        Assert.NotEmpty(optimize);
        Assert.Equal(direct, optimize);
        Assert.Equal(optimize, profileOption);
        Assert.Equal(optimize, presetOption);
        Assert.Equal(optimize, savedSetup);
    }

    [Fact]
    public void A_level_named_by_preset_id_is_planned_for_the_pc_and_no_longer_its_whole_list()
    {
        // On a PC without an NVMe drive: the preset's whole list holds the NVMe entry (as it was applied until 0.9); the plan leaves it out and says why.
        var pc = Pc();
        var resolution = LevelPlanning.Resolve([LevelPlanning.MaximumId], [], PlanningContext.Reading(Catalog, Hardware(nvme: false), pc), ExplicitOperations.AsNamed);

        Assert.Contains("power.nvme-stay-active-ac", Preset(LevelPlanning.MaximumId).OperationIds);
        Assert.DoesNotContain(resolution.Operations, o => o.Id == "power.nvme-stay-active-ac");
        var nvme = Assert.Single(resolution.NotHere, e => e.OperationId == "power.nvme-stay-active-ac");
        Assert.Equal((EntryState.Unsupported, "hardware.disk.nvme"), (nvme.State, nvme.Reason));

        // The optional entries are offered with the level in the window, never applied by its id.
        Assert.DoesNotContain(resolution.Operations, o => Preset(LevelPlanning.MaximumId).OptionalIds.Contains(o.Id));
    }

    [Fact]
    public void Hardware_that_was_not_read_leaves_the_entries_for_some_hardware_alone_on_every_path_and_the_rest_proceeds()
    {
        var pc = Pc();
        var resolution = Setup([LevelPlanning.MaximumId]).ResolveWindows(PlanningContext.Reading(Catalog, null, pc));

        Assert.DoesNotContain(resolution.Operations, o => o.Id is "power.nvme-stay-active-ac" or "gpu.amd-anti-lag-on" or "network.preset-realtek-ethernet");
        Assert.All(resolution.NotHere.Where(e => e.Entry.Conditions.Count > 0), e => Assert.Equal(EntryState.CantTell, e.State));
        Assert.Contains(resolution.Operations, o => o.Id == "power.power-throttling-off");
        // 0.9: the X3D-restricted entries are hardware entries too (Phase 1 applied core parking here).
        Assert.DoesNotContain(resolution.Operations, o => o.Id is "cpu.core-parking-off" or "power.mesura-maximum-plan" or "gaming.game-recording-policy-off");
    }

    [Fact]
    public void A_setup_naming_both_levels_resolves_to_maximum_alone_and_never_runs_two_power_plans()
    {
        var pc = Pc();
        var context = PlanningContext.Reading(Catalog, Hardware(), pc);

        var both = Setup([LevelPlanning.ReferenceId, LevelPlanning.MaximumId]).ResolveWindows(context);
        var maximum = Setup([LevelPlanning.MaximumId]).ResolveWindows(context);

        Assert.Equal(maximum.Operations.Select(o => o.Id), both.Operations.Select(o => o.Id));
        Assert.Equal([LevelPlanning.ReferenceId], both.SetAside);
        Assert.Empty(WindowsConfigurator.Conflicts(both.Operations, pc));
        Assert.DoesNotContain(both.Operations, o => o.Id == "power.mesura-balanced-plan");

        // The command line's --preset follows the same rule, in either order.
        var cli = LevelPlanning.Resolve([LevelPlanning.MaximumId, LevelPlanning.ReferenceId], [], context, ExplicitOperations.AsNamed);
        Assert.Equal(maximum.Operations.Select(o => o.Id), cli.Operations.Select(o => o.Id));
    }

    [Fact]
    public void A_preset_that_is_not_a_level_still_gives_its_list_and_keeps_its_place()
    {
        var pc = Pc();
        var context = PlanningContext.Reading(Catalog, Hardware(), pc);
        var collection = Catalog.Presets.First(p => p.Tier == PresetTier.Collection);

        var resolution = LevelPlanning.Resolve([collection.Id], [], context, ExplicitOperations.AsNamed);

        Assert.False(LevelPlanning.IsLevel(collection, Profiles));
        Assert.Empty(resolution.Levels);
        Assert.Equal(collection.OperationIds.Where(id => Catalog.ById(id) is not null), resolution.Operations.Select(o => o.Id));
    }

    // ---- single operations in a saved setup obey the level's hardware conditions -------------------------------------------------------------------------------

    [Fact]
    public void An_operation_a_saved_setup_names_on_its_own_obeys_its_level_condition_while_the_command_lines_id_is_applied_as_named()
    {
        var pc = Pc();
        var nvidia = PlanningContext.Reading(Catalog, Hardware(gpu: GpuMaker.Nvidia), pc);

        // Anti-Lag is a Maximum entry for a Radeon. A setup saved on a Radeon PC names it; on an NVIDIA PC it is left out, with the reason.
        var setup = Setup([], ["gpu.amd-anti-lag-on", "explorer.show-extensions"]).ResolveWindows(nvidia);
        Assert.Equal(["explorer.show-extensions"], setup.Operations.Select(o => o.Id));   // an operation no level conditions is applied as named
        var skipped = Assert.Single(setup.Skipped);
        Assert.Equal(("gpu.amd-anti-lag-on", EntryState.Unsupported, "hardware.gpu.amd"), (skipped.OperationId, skipped.State, skipped.Reason));

        // The same id on the command line is the user's explicit choice: applied as named (the engine still checks it on this PC).
        var cli = LevelPlanning.Resolve([], ["gpu.amd-anti-lag-on"], nvidia, ExplicitOperations.AsNamed);
        Assert.Equal(["gpu.amd-anti-lag-on"], cli.Operations.Select(o => o.Id));
        Assert.Empty(cli.Skipped);

        // Hardware unread: the conditioned operation can't be told, so it is left alone.
        var unread = Setup([], ["gpu.amd-anti-lag-on"]).ResolveWindows(PlanningContext.Reading(Catalog, null, pc));
        Assert.Equal(EntryState.CantTell, Assert.Single(unread.Skipped).State);

        // Named on its own and as an entry of the level the setup names: left out once, and said once.
        var both = Setup([LevelPlanning.MaximumId], ["gpu.amd-anti-lag-on"]).ResolveWindows(nvidia);
        Assert.Single(both.Skipped);
        Assert.Single(both.NotHere, e => e.OperationId == "gpu.amd-anti-lag-on");
    }

    // ---- dual-CCD X3D: a synthetic, test-only profile (the real composition is a later phase) ------------------------------------------------------------------

    /// <summary>
    /// A test-only Maximum with the dual-CCD X3D exclusions of D054 item 4 written as conditions (synthetic: <c>profiles.json</c> is not changed in this
    /// phase, and nothing here validates real X3D hardware).
    /// </summary>
    private static OptimizationProfiles X3dProfiles(bool withConditions = true) => OptimizationProfiles.Parse($$"""
        { "kind": "Mesura.OptimizationProfiles", "schemaVersion": 1,
          "groups": [ { "id": "power", "name": "Power" }, { "id": "cpu", "name": "Processor" }, { "id": "gaming", "name": "Gaming" }, { "id": "privacy", "name": "Privacy" } ],
          "why": { "power.mesura-maximum-plan": "t", "power.mesura-balanced-plan": "t", "cpu.core-parking-off": "t", "gaming.game-recording-policy-off": "t", "privacy.no-advertising-id": "t" },
          "profiles": [ { "id": "maximum-performance", "entries": [
            { "op": "power.mesura-maximum-plan", "group": "power"{{(withConditions ? """, "when": { "cpu": "not-x3d-dual-ccd" }""" : "")}} },
            { "op": "power.mesura-balanced-plan", "group": "power"{{(withConditions ? """, "when": { "cpu": "x3d-dual-ccd" }""" : "")}} },
            { "op": "cpu.core-parking-off", "group": "cpu", "when": { "cpu": "not-x3d-dual-ccd" } },
            { "op": "gaming.game-recording-policy-off", "group": "gaming", "when": { "cpu": "not-x3d-dual-ccd" } },
            { "op": "privacy.no-advertising-id", "group": "privacy" } ] } ] }
        """);

    private static PlanningContext X3dContext(FakeWindows pc, CpuProfile? cpu) =>
        PlanningContext.Reading(Catalog, cpu is null ? null : Hardware(gpu: GpuMaker.Amd, cpu: cpu), pc) with { Profiles = X3dProfiles() };

    [Fact]
    public void On_a_synthetic_dual_ccd_x3d_the_exclusions_hold_on_every_path_and_balanced_mesura_is_the_plan()
    {
        var pc = Pc();
        var context = X3dContext(pc, DualCcdX3d);
        Assert.True(DualCcdX3d.DualCcdX3d);

        var plan = LevelPlanning.Plan(Preset(LevelPlanning.MaximumId), context);
        Assert.Equal(["power.mesura-balanced-plan", "privacy.no-advertising-id"], plan.OperationIds);
        Assert.Equal("hardware.cpu.not-x3d-dual-ccd", plan.Entry("cpu.core-parking-off")!.Reason);
        Assert.Equal(EntryState.Unsupported, plan.Entry("power.mesura-maximum-plan")!.State);
        Assert.Equal(EntryState.Unsupported, plan.Entry("gaming.game-recording-policy-off")!.State);

        // The same through --preset and a saved setup, including a setup saved elsewhere that names core parking on its own.
        Assert.Equal(plan.OperationIds, LevelPlanning.Resolve([LevelPlanning.MaximumId], [], context, ExplicitOperations.AsNamed).Operations.Select(o => o.Id));
        var setup = Setup([LevelPlanning.MaximumId], ["cpu.core-parking-off"]).ResolveWindows(context);
        Assert.Equal(plan.OperationIds, setup.Operations.Select(o => o.Id));
        Assert.Equal("hardware.cpu.not-x3d-dual-ccd", Assert.Single(setup.Skipped).Reason);
        Assert.Empty(WindowsConfigurator.Conflicts(setup.Operations, pc));
    }

    [Fact]
    public void On_a_two_die_ryzen_without_stacked_cache_and_on_intel_the_maximum_plan_applies_and_on_an_unread_processor_neither_plan_does()
    {
        foreach (var cpu in new[] { TwoDieRyzen, IntelCpu })
        {
            var plan = LevelPlanning.Plan(Preset(LevelPlanning.MaximumId), X3dContext(Pc(), cpu));
            Assert.Equal("power.mesura-maximum-plan", plan.OperationIds[0]);
            Assert.DoesNotContain("power.mesura-balanced-plan", plan.OperationIds);
            Assert.Contains("cpu.core-parking-off", plan.OperationIds);
        }

        // A processor that couldn't be read: whether it is a dual-CCD X3D can't be told, so neither plan nor the X3D-sensitive entries are applied.
        foreach (var context in new[] { X3dContext(Pc(), UnreadCpu), X3dContext(Pc(), cpu: null) })
        {
            var plan = LevelPlanning.Plan(Preset(LevelPlanning.MaximumId), context);
            Assert.Equal(["privacy.no-advertising-id"], plan.OperationIds);
            Assert.All(new[] { "power.mesura-maximum-plan", "power.mesura-balanced-plan", "cpu.core-parking-off" }, id => Assert.Equal(EntryState.CantTell, plan.Entry(id)!.State));
        }
    }

    // ---- condition-aware conflict validation ----------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("x3d-dual-ccd", "not-x3d-dual-ccd", true)]
    [InlineData("not-x3d-dual-ccd", "x3d-dual-ccd", true)]
    [InlineData("intel", "amd", true)]
    [InlineData("x3d-dual-ccd", "intel", true)]
    [InlineData("x3d-dual-ccd", "amd", false)]
    [InlineData("not-x3d-dual-ccd", "intel", false)]
    [InlineData("hybrid", "intel", false)]
    [InlineData("hybrid", "x3d-dual-ccd", false)]
    [InlineData("amd", "amd", false)]
    public void Two_processor_conditions_are_disjoint_only_when_no_pc_meets_both(string a, string b, bool disjoint)
    {
        Assert.Equal(disjoint, HardwareCondition.Disjoint(new HardwareCondition(Cpu: a), new HardwareCondition(Cpu: b)));
    }

    [Fact]
    public void Forms_without_a_common_form_are_disjoint_and_a_missing_or_overlapping_condition_never_is()
    {
        Assert.True(HardwareCondition.Disjoint(new HardwareCondition(Form: ["desktop"]), new HardwareCondition(Form: ["portable"])));
        Assert.False(HardwareCondition.Disjoint(new HardwareCondition(Form: ["desktop", "compact"]), new HardwareCondition(Form: ["compact"])));
        Assert.False(HardwareCondition.Disjoint(null, new HardwareCondition(Cpu: "x3d-dual-ccd")));
        Assert.False(HardwareCondition.Disjoint(new HardwareCondition(Gpu: "nvidia"), new HardwareCondition(Gpu: "amd")));   // one PC can have both
        Assert.False(HardwareCondition.Disjoint(new HardwareCondition(Disk: "nvme"), new HardwareCondition(Disk: "sata")));
        Assert.True(HardwareCondition.Disjoint(new HardwareCondition(Gpu: "amd", Cpu: "intel"), new HardwareCondition(Cpu: "amd")));
    }

    [Fact]
    public void Conflicting_entries_kept_apart_by_their_conditions_are_no_conflict_and_the_same_entries_without_conditions_are()
    {
        Assert.Empty(LevelPlanning.ConditionAwareConflicts(X3dProfiles().ById(LevelPlanning.MaximumId)!, Catalog));

        var unconditioned = LevelPlanning.ConditionAwareConflicts(X3dProfiles(withConditions: false).ById(LevelPlanning.MaximumId)!, Catalog);
        var conflict = Assert.Single(unconditioned);
        Assert.Contains("power.mesura-maximum-plan", conflict, StringComparison.Ordinal);
        Assert.Contains("power.mesura-balanced-plan", conflict, StringComparison.Ordinal);
    }

    // ---- bit fields: compared by bits ----------------------------------------------------------------------------------------------------------------------------

    private static WindowsOperation Flags(string id, string body) => WindowsCatalog.Parse($$"""
        { "kind": "Mesura.WindowsCatalog", "schemaVersion": 4, "presets": [], "operations": [
          { "id": "{{id}}", "name": "{{id}}", "category": "System", "description": "d", "risk": "userPreference",
            "registry": [ { "hive": "HKCU", "path": "Control Panel\\Accessibility\\StickyKeys", "name": "Flags", "type": "string", {{body}} } ] } ] }
        """).Operations.Single();

    [Fact]
    public void Bit_field_writes_conflict_only_when_their_bits_disagree()
    {
        var setBit1 = Flags("a.set-1", "\"value\": \"1\", \"setBits\": 1");
        var setBit2 = Flags("b.set-2", "\"value\": \"2\", \"setBits\": 2");
        var clearBit1 = Flags("c.clear-1", "\"value\": \"0\", \"clearBits\": 1");
        var clearBit4 = Flags("d.clear-4", "\"value\": \"0\", \"clearBits\": 4");
        var whole3 = Flags("e.whole-3", "\"value\": \"3\"");
        var whole6 = Flags("f.whole-6", "\"value\": \"6\"");
        var wordy = Flags("g.whole-word", "\"value\": \"on\"");

        Assert.Empty(WindowsConfigurator.Conflicts([setBit1, setBit2]));             // different bits of one value (a text comparison called this a conflict)
        Assert.Empty(WindowsConfigurator.Conflicts([setBit1, clearBit4]));           // set one bit, clear another
        Assert.Single(WindowsConfigurator.Conflicts([setBit1, clearBit1]));          // the same bit set and cleared
        Assert.Empty(WindowsConfigurator.Conflicts([whole3, setBit1, setBit2]));     // a whole value that has the bits both set
        Assert.NotEmpty(WindowsConfigurator.Conflicts([whole6, setBit1]));           // 6 lacks bit 1
        Assert.Empty(WindowsConfigurator.Conflicts([whole3, clearBit4]));            // 3 has no bit 4 set
        Assert.NotEmpty(WindowsConfigurator.Conflicts([whole6, clearBit4]));         // 6 has bit 4 set
        Assert.NotEmpty(WindowsConfigurator.Conflicts([wordy, setBit1]));            // a value that isn't a number can't be compared by bits
        Assert.Single(WindowsConfigurator.Conflicts([whole3, whole6]));              // two whole values, as before

        var groups = WindowsConfigurator.ConflictGroups([setBit1, setBit2, clearBit1]);
        var group = Assert.Single(groups);
        Assert.Equal(["a.set-1", "c.clear-1"], group.Select(o => o.Id).Order(StringComparer.Ordinal));   // the bit-2 writer agrees with both
    }

    // ---- retired operations in saved setups ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_setup_naming_a_retired_operation_opens_never_applies_it_and_says_so_and_an_unknown_id_is_still_an_error()
    {
        var retired = WindowsCatalog.Retired.Operations.First(o => Catalog.ById(o.Id) is null).Id;
        var json = $$"""
            { "kind": "Mesura.SetupProfile", "schemaVersion": 1, "name": "old", "windows": [ "{{retired}}", "explorer.show-extensions" ] }
            """;

        var profile = SetupProfile.Parse(json, AppCatalog.Default, Catalog);

        Assert.Equal(["explorer.show-extensions"], profile.Windows);
        Assert.Equal([retired], profile.RetiredWindows);
        var resolution = profile.ResolveWindows(PlanningContext.Reading(Catalog, Hardware(), Pc()));
        Assert.Equal(["explorer.show-extensions"], resolution.Operations.Select(o => o.Id));
        Assert.DoesNotContain(retired, profile.ToJson(), StringComparison.Ordinal);   // not written back

        // A file that names only retired operations still opens, so the preview can say what became of them.
        var onlyRetired = SetupProfile.Parse($$"""{ "kind": "Mesura.SetupProfile", "schemaVersion": 1, "name": "old", "windows": [ "{{retired}}" ] }""", AppCatalog.Default, Catalog);
        Assert.Empty(onlyRetired.Windows);
        Assert.Equal([retired], onlyRetired.RetiredWindows);

        var unknown = Assert.Throws<FormatException>(() => SetupProfile.Parse(
            """{ "kind": "Mesura.SetupProfile", "schemaVersion": 1, "name": "x", "windows": [ "no.such-operation" ] }""", AppCatalog.Default, Catalog));
        Assert.Contains("no.such-operation", unknown.Message, StringComparison.Ordinal);
    }

    // ---- level in place, tray status ---------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_status_counts_what_the_level_would_apply_here_and_not_the_entries_for_hardware_it_lacks()
    {
        var pc = Pc();
        var withoutNvme = LevelPlanning.Plan(Preset(LevelPlanning.MaximumId), PlanningContext.Reading(Catalog, Hardware(nvme: false), pc));
        var (pending, unreadable) = LevelPlanning.Status(withoutNvme);

        Assert.Equal(withoutNvme.OperationIds.Count, pending);
        Assert.Equal(0, unreadable);
        Assert.True(pending < Preset(LevelPlanning.MaximumId).OperationIds.Count);

        // Hardware unread: the hardware entries are neither pending nor "couldn't read".
        Assert.Equal(0, LevelPlanning.Status(LevelPlanning.Plan(Preset(LevelPlanning.MaximumId), PlanningContext.Reading(Catalog, null, pc))).Unreadable);
    }

    [Fact]
    public void A_preset_that_is_not_a_level_is_in_place_when_each_operation_is_applied_or_not_for_this_pc()
    {
        var collection = Catalog.Presets.First(p => p.Tier == PresetTier.Collection);
        var all = collection.OperationIds.ToDictionary(id => id, id => new OperationEvaluation(Catalog.ById(id)!, OperationState.Applied, "", []));
        var context = new PlanningContext(Catalog, null, Pc(), id => all.GetValueOrDefault(id));

        Assert.True(LevelPlanning.InPlace(collection, context));
        all[collection.OperationIds[0]] = all[collection.OperationIds[0]] with { State = OperationState.NotApplied };
        Assert.False(LevelPlanning.InPlace(collection, context));
    }

    // ---- built-in apps: never removed or put back as another account ---------------------------------------------------------------------------------------------

    private const string News = "Microsoft.BingNews_4.55.62231.0_x64__8wekyb3d8bbwe";

    private static WindowsOperation AppOp => WindowsCatalog.Parse("""
        { "kind": "Mesura.WindowsCatalog", "schemaVersion": 4, "presets": [], "operations": [
          { "id": "test.remove-news", "name": "Remove News", "category": "Bundled apps", "description": "d", "risk": "systemConfiguration", "appx": [ { "name": "Microsoft.BingNews" } ] } ] }
        """).Operations.Single();

    private static UserContext OtherAccount => new("S-1-5-21-1-1-1-1001", "PC-owner", "", ProcessIsUser: false);

    [Fact]
    public void The_user_context_says_whether_the_process_runs_as_the_signed_in_user()
    {
        Assert.True(UserContext.Resolve("S-1-5-21-1", "S-1-5-21-1", "PC\\owner", sessionHiveLoaded: true).ProcessIsUser);
        var elevatedAsAdmin = UserContext.Resolve("S-1-5-21-9", "S-1-5-21-1", "PC\\owner", sessionHiveLoaded: true);
        Assert.True(elevatedAsAdmin.Resolved);                 // the owner's own hive is still the target for per-user values
        Assert.False(elevatedAsAdmin.ProcessIsUser);           // but app removal acts on the process's account
        Assert.False(new UserContext("S-1-5-21-1", "x", "").ProcessIsUser);   // made by hand: never allowed by accident
    }

    [Fact]
    public void An_app_is_neither_evaluated_for_removal_nor_removed_when_mesura_runs_as_another_account_and_registry_settings_still_are()
    {
        var pc = new FakeWindows { User = OtherAccount };
        pc.Apps.Add(News);

        var evaluation = WindowsEvaluator.Evaluate(AppOp, pc);
        Assert.Equal((OperationState.Blocked, WindowsEvaluator.OtherAccountCode), (evaluation.State, evaluation.ReasonCode));
        Assert.Contains("another account than PC-owner", evaluation.Reason, StringComparison.Ordinal);

        var outcome = new WindowsConfigurator(pc, pc).Apply([AppOp], TestContext.Current.CancellationToken).Single();
        Assert.Equal(WindowsOpStatus.Blocked, outcome.Status);
        Assert.Contains(News, pc.Apps);
        Assert.Empty(pc.Writes);

        // A per-user registry value is addressed in the signed-in user's own hive, so it still evaluates as usual.
        Assert.Equal(OperationState.NotApplied, WindowsEvaluator.Evaluate(Catalog.ById("privacy.no-advertising-id")!, pc).State);

        // The plan says why, with its own code (worded "runs as another account" in the window).
        var plan = ProfilePlanner.Plan(Profiles.FromPreset(new WindowsPreset("apps", "Apps", "", ["test.remove-news"], PresetTier.Primary, "", [])),
            WindowsCatalog.Parse("""{ "kind": "Mesura.WindowsCatalog", "schemaVersion": 4, "presets": [], "operations": [ { "id": "test.remove-news", "name": "Remove News", "category": "Bundled apps", "description": "d", "risk": "systemConfiguration", "appx": [ { "name": "Microsoft.BingNews" } ] } ] }"""),
            null, pc, _ => evaluation);
        Assert.Equal((EntryState.CantTell, PlanReason.OtherAccount), (plan.Entries.Single().State, plan.Entries.Single().Reason));
    }

    [Fact]
    public void An_app_removal_is_undone_only_as_the_signed_in_user_it_was_removed_for()
    {
        var pc = new FakeWindows();
        pc.Apps.Add(News);
        var removed = new WindowsConfigurator(pc, pc).Apply([AppOp], TestContext.Current.CancellationToken).Single();
        Assert.Equal(WindowsOpStatus.Applied, removed.Status);
        Assert.Equal(pc.User.Sid, Assert.Single(removed.Changes).UserSid);   // recorded for the user it was removed for

        // Mesura now runs as another account: nothing is put back.
        pc.User = OtherAccount;
        var asOther = new WindowsConfigurator(pc, pc).Revert([removed]).Single();
        Assert.Equal(WindowsOpStatus.Blocked, asOther.Status);
        Assert.DoesNotContain(pc.Writes, w => w.StartsWith("restore ", StringComparison.Ordinal));
        Assert.DoesNotContain(News, pc.Apps);

        // Another Windows user is signed in (and runs Mesura): their apps are not where this one was removed.
        pc.User = new UserContext("S-1-5-21-1-1-1-1002", "PC-other", "", ProcessIsUser: true);
        Assert.Equal(WindowsOpStatus.Blocked, new WindowsConfigurator(pc, pc).Revert([removed]).Single().Status);

        // The same user again: put back.
        pc.User = new UserContext("S-1-5-21-1-1-1-1001", "PC-owner", "", ProcessIsUser: true);
        Assert.Equal(WindowsOpStatus.Reverted, new WindowsConfigurator(pc, pc).Revert([removed]).Single().Status);
    }

    [Fact]
    public void A_record_from_before_the_user_was_recorded_is_still_undone_as_the_signed_in_user_and_refused_as_another_account()
    {
        var pc = new FakeWindows();
        pc.Apps.Add(News);
        var removed = new WindowsConfigurator(pc, pc).Apply([AppOp], TestContext.Current.CancellationToken).Single();
        var old = removed with { Changes = [.. removed.Changes.Select(c => c with { UserSid = null })] };

        pc.User = OtherAccount;
        Assert.Equal(WindowsOpStatus.Blocked, new WindowsConfigurator(pc, pc).Revert([old]).Single().Status);

        pc.User = new UserContext("S-1-5-21-1-1-1-1001", "PC-owner", "", ProcessIsUser: true);
        Assert.Equal(WindowsOpStatus.Reverted, new WindowsConfigurator(pc, pc).Revert([old]).Single().Status);
    }
}
