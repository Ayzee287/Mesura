using Mesura.Core.Apps;
using Mesura.Core.Hardware;
using Mesura.Core.Profiles;
using Mesura.Core.Setup;
using Mesura.Core.Tests.Windows;
using Mesura.Core.Windows;

namespace Mesura.Core.Tests.Profiles;

/// <summary>
/// The dual-CCD Ryzen X3D policy (0.9 Phase 2, vault D054 item 4) on the built-in profiles: the owner's "dual-ccd-x3d" restriction in <c>profiles.json</c>
/// and the Balanced Mesura entry of Maximum. Every processor here is a synthetic stand-in built from the cache layout Windows would report; nothing is read
/// from or written to a real PC, and none of this is hardware validation (that is a separate gate, and synthetic results never relax the policy).
/// </summary>
public sealed class X3dPolicyTests
{
    private const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private const string BalancedMesura = "b71ddbcf-9d21-46ea-9def-6f2a2378e65a";
    private const string MaximumMesura = "76d36892-93d0-4c33-9e09-c2e1fb87cf49";
    private const string CoreParkingMinCores = "0cc5b647-c1df-4637-891a-dec35c318583";
    private const string CoreParkingMinCoresClass1 = "0cc5b647-c1df-4637-891a-dec35c318584";
    private const string PowerPlanType = "245d8541-3943-4422-b025-13a784f679b7";
    private const string NoSubgroup = "fea3413e-7e05-4911-9a71-700331f1c294";

    private static readonly WindowsCatalog Catalog = WindowsCatalog.Default;
    private static readonly OptimizationProfiles Profiles = OptimizationProfiles.Default;

    /// <summary>The operations the policy leaves out on a dual-CCD X3D (D054 item 4), Windows' own High performance and Ultimate plans with the Maximum one.</summary>
    private static readonly string[] Restricted =
        ["cpu.core-parking-off", "power.mesura-maximum-plan", "power.high-performance", "power.ultimate-performance", "gaming.game-recording-policy-off", "apps.remove-game-bar"];

    private static WindowsPreset Maximum => Catalog.PresetById(LevelPlanning.MaximumId)!;

    // ---- the five synthetic machines -------------------------------------------------------------------------------------------------------------------------

    private sealed record Machine(string Name, CpuProfile Cpu, DeviceForm Form, bool Battery, bool Hybrid);

    private static long Mb(int n) => (long)n << 20;

    /// <summary>Ryzen 9 7950X3D: two core dies, one with 96 MB of L3, one with 32 MB.</summary>
    private static readonly Machine Ryzen7950X3d = new("7950X3D", new CpuProfile("AMD Ryzen 9 7950X3D 16-Core Processor", CpuMaker.Amd, 16, 32, false,
        [new CacheGroup(3, Mb(96), 16), new CacheGroup(3, Mb(32), 16)]), DeviceForm.Desktop, false, false);

    /// <summary>Ryzen 7 9800X3D: one core die with 96 MB of L3, nothing to park.</summary>
    private static readonly Machine Ryzen9800X3d = new("9800X3D", new CpuProfile("AMD Ryzen 7 9800X3D 8-Core Processor", CpuMaker.Amd, 8, 16, false,
        [new CacheGroup(3, Mb(96), 16)]), DeviceForm.Desktop, false, false);

    /// <summary>Core i7-13700K: a hybrid Intel desktop processor, one shared L3.</summary>
    private static readonly Machine Intel13700K = new("13700K", new CpuProfile("13th Gen Intel(R) Core(TM) i7-13700K", CpuMaker.Intel, 16, 24, true,
        [new CacheGroup(3, Mb(30), 24)]), DeviceForm.Desktop, false, true);

    /// <summary>A hybrid Intel laptop (Core Ultra 7 155H class), on a battery.</summary>
    private static readonly Machine IntelHybridLaptop = new("hybrid-laptop", new CpuProfile("Intel(R) Core(TM) Ultra 7 155H", CpuMaker.Intel, 16, 22, true,
        [new CacheGroup(3, Mb(24), 22)]), DeviceForm.Portable, true, true);

    /// <summary>A processor nothing could be read about: no maker, name, counts or cache layout.</summary>
    private static readonly Machine UnreadCpu = new("unread-cpu", new CpuProfile(null, CpuMaker.Unknown, null, null, null), DeviceForm.Desktop, false, false);

    private static Machine ByName(string name) => new[] { Ryzen7950X3d, Ryzen9800X3d, Intel13700K, IntelHybridLaptop, UnreadCpu }.Single(m => m.Name == name);

    private static HardwareProfile Hardware(Machine m) => new(
        new WindowsPlatform(26100, "Professional", "24H2", true), m.Form, new PowerPlatform(m.Battery, false, true), m.Cpu, 32UL << 30,
        [new GpuProfile("Test GPU", GpuMaker.Nvidia, 0x10DE, 1, "1.0", false)],
        [new DiskProfile("NVMe", StorageClass.Nvme, 1, true, Mesura.Core.Raw.BusKind.Nvme)],
        [new NetworkProfile("Ethernet", AdapterKind.Ethernet, "Realtek", true)]);

    /// <summary>An in-memory Windows whose Balanced scheme registers every power setting the profiles touch, at a value none of them wants.</summary>
    private static FakeWindows Pc(Machine m)
    {
        var pc = new FakeWindows { HasBattery = m.Battery, ModernStandby = false, HybridCpu = m.Hybrid, PolicyEdition = true, PhysicalMemoryBytes = 32UL << 30 };
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

    private static PlanningContext Context(Machine m, FakeWindows pc) => PlanningContext.Reading(Catalog, Hardware(m), pc);

    private static SetupProfile Setup(string[] presets, string[]? windows = null) => new("test", "", [], [], null, [], windows ?? [], presets);

    /// <summary>A saved setup as a file on disk would be read (so ids this version retired are set aside, as for a real file).</summary>
    private static SetupProfile SavedSetup(string[] presets, string[] windows) => SetupProfile.Parse(System.Text.Json.JsonSerializer.Serialize(new
    {
        kind = SetupProfile.FileKind, schemaVersion = 1, name = "saved elsewhere", windowsPresets = presets, windows,
    }), AppCatalog.Default, Catalog);

    /// <summary>The restricted operations still in the catalog: Windows' High performance and Ultimate plans are retired since 0.9 Phase 3 (D057).</summary>
    private static string[] LiveRestricted => [.. Restricted.Where(id => Catalog.ById(id) is not null)];

    private static readonly string[] RetiredRestricted = ["power.high-performance", "power.ultimate-performance"];

    // ---- the fixtures read as intended -----------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("7950X3D", true)]
    [InlineData("9800X3D", false)]
    [InlineData("13700K", false)]
    [InlineData("hybrid-laptop", false)]
    [InlineData("unread-cpu", null)]
    public void The_synthetic_processors_are_told_apart_by_their_cache_layout(string machine, bool? dualCcdX3d)
    {
        Assert.Equal(dualCcdX3d, ByName(machine).Cpu.DualCcdX3d);
    }

    // ---- the plan per machine, identical on every entry point -------------------------------------------------------------------------------------------------

    public static TheoryData<string> Machines() => ["7950X3D", "9800X3D", "13700K", "hybrid-laptop", "unread-cpu"];

    [Theory]
    [MemberData(nameof(Machines))]
    public void Every_entry_point_plans_maximum_the_same_way_on_each_machine(string machine)
    {
        var m = ByName(machine);
        var pc = Pc(m);
        var context = Context(m, pc);

        var optimize = LevelPlanning.Plan(Maximum, context).OperationIds;
        Assert.Equal(optimize, LevelPlanning.Plan(Profiles.ById(LevelPlanning.MaximumId)!, context).OperationIds);
        Assert.Equal(optimize, LevelPlanning.Resolve([LevelPlanning.MaximumId], [], context, ExplicitOperations.AsNamed).Operations.Select(o => o.Id));
        Assert.Equal(optimize, Setup([LevelPlanning.MaximumId]).ResolveWindows(context).Operations.Select(o => o.Id));
        Assert.Equal(optimize, Setup([LevelPlanning.ReferenceId, LevelPlanning.MaximumId]).ResolveWindows(context).Operations.Select(o => o.Id));
        Assert.Equal(optimize.Count, LevelPlanning.Status(LevelPlanning.Plan(Maximum, context)).Pending);
        Assert.Empty(WindowsConfigurator.Conflicts(optimize.Select(id => Catalog.ById(id)!), pc));
    }

    [Fact]
    public void On_a_7950X3D_maximum_uses_balanced_mesura_and_leaves_out_every_restricted_operation_with_the_reason()
    {
        var plan = LevelPlanning.Plan(Maximum, Context(Ryzen7950X3d, Pc(Ryzen7950X3d)));

        Assert.Equal("power.mesura-balanced-plan", plan.OperationIds[0]);
        Assert.DoesNotContain("power.mesura-maximum-plan", plan.OperationIds);
        foreach (var id in new[] { "cpu.core-parking-off", "power.mesura-maximum-plan", "gaming.game-recording-policy-off" })
        {
            var entry = plan.Entry(id)!;
            Assert.Equal((EntryState.Unsupported, "hardware.cpu.not-x3d-dual-ccd"), (entry.State, entry.Reason));
            Assert.Contains("Ryzen X3D with two core dies", entry.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain(id, plan.OperationIds);
        }

        // What the policy doesn't name still applies (no broader exclusion): power throttling, the energy hint, memory, storage.
        foreach (var id in new[] { "power.power-throttling-off", "power.cpu-epp-performance-ac", "memory.compression-off", "system.ntfs-8dot3-off" })
        {
            Assert.Contains(id, plan.OperationIds);
        }
    }

    [Theory]
    [InlineData("9800X3D")]
    [InlineData("13700K")]
    [InlineData("hybrid-laptop")]
    public void Machines_that_are_not_a_dual_ccd_x3d_keep_maximum_as_it_was(string machine)
    {
        var m = ByName(machine);
        var plan = LevelPlanning.Plan(Maximum, Context(m, Pc(m)));

        Assert.Equal("power.mesura-maximum-plan", plan.OperationIds[0]);
        Assert.DoesNotContain("power.mesura-balanced-plan", plan.OperationIds);
        foreach (var id in new[] { "cpu.core-parking-off", "gaming.game-recording-policy-off" })
        {
            var e = plan.Entry(id)!;
            Assert.True(plan.OperationIds.Contains(id), $"{id}: {e.State} {e.Reason} {e.Detail}");
        }
        var balanced = plan.Entry("power.mesura-balanced-plan")!;
        Assert.Equal((EntryState.Unsupported, "hardware.cpu.x3d-dual-ccd"), (balanced.State, balanced.Reason));   // shown as "only on a dual-CCD X3D"

        // Exactly what the plan was before the policy: the X3D entries are the only difference.
        var withoutPolicy = OptimizationProfiles.Parse(System.Text.RegularExpressions.Regex.Replace(ProfilesJson(), "\"restrictions\": \\[.*?\\n  \\],", "", System.Text.RegularExpressions.RegexOptions.Singleline));
        var before = LevelPlanning.Plan(Maximum, Context(m, Pc(m)) with { Profiles = withoutPolicy }).OperationIds;
        Assert.Equal(before, plan.OperationIds);
        if (m.Hybrid)
        {
            // A hybrid processor is not an X3D: the core-type preference is there, covered by the Maximum plan, which writes the same values on a hybrid CPU.
            Assert.Equal((EntryState.Covered, "power.mesura-maximum-plan"), (plan.Entry("cpu.hybrid-prefer-performance-cores")!.State, plan.Entry("cpu.hybrid-prefer-performance-cores")!.CoveredBy));
        }
    }

    [Fact]
    public void An_unreadable_processor_gets_neither_plan_and_no_x3d_sensitive_operation_by_guess()
    {
        var plan = LevelPlanning.Plan(Maximum, Context(UnreadCpu, Pc(UnreadCpu)));

        Assert.DoesNotContain(plan.OperationIds, id => id is "power.mesura-maximum-plan" or "power.mesura-balanced-plan" or "cpu.core-parking-off" or "gaming.game-recording-policy-off");
        foreach (var id in new[] { "power.mesura-maximum-plan", "power.mesura-balanced-plan", "cpu.core-parking-off", "gaming.game-recording-policy-off" })
        {
            Assert.Equal(EntryState.CantTell, plan.Entry(id)!.State);
            Assert.StartsWith("hardware.cpu.", plan.Entry(id)!.Reason, StringComparison.Ordinal);
        }

        // The rest proceeds; the tray doesn't count what can't be told as unreadable settings.
        Assert.Contains("power.power-throttling-off", plan.OperationIds);
        Assert.Equal(0, LevelPlanning.Status(plan).Unreadable);

        // Hardware not read at all: the same, for every hardware-conditioned entry.
        var none = LevelPlanning.Plan(Maximum, PlanningContext.Reading(Catalog, null, Pc(UnreadCpu)));
        Assert.DoesNotContain(none.OperationIds, id => Restricted.Contains(id) || id == "power.mesura-balanced-plan");
    }

    // ---- saved setups can't bring the restricted operations back ------------------------------------------------------------------------------------------------

    [Fact]
    public void On_a_7950X3D_a_saved_setup_naming_the_restricted_operations_one_by_one_or_through_a_collection_applies_none_of_them()
    {
        var context = Context(Ryzen7950X3d, Pc(Ryzen7950X3d));

        // Saved on another PC: the level, every restricted operation by id, and the "gaming" collection (which holds the Game Bar recording policy).
        var setup = SavedSetup([LevelPlanning.MaximumId, "gaming"], [.. Restricted, "explorer.show-extensions"]);
        var resolution = setup.ResolveWindows(context);
        var ids = resolution.Operations.Select(o => o.Id).ToList();

        Assert.DoesNotContain(ids, Restricted.Contains);
        Assert.Contains("power.mesura-balanced-plan", ids);
        Assert.Contains("explorer.show-extensions", ids);                     // not restricted: applied as named
        Assert.Contains("input.pointer-precision-off", ids);                  // the rest of the collection still applies
        Assert.All(LiveRestricted, id => Assert.Contains(resolution.NotHere, e => e.OperationId == id && e.Reason == "hardware.cpu.not-x3d-dual-ccd"));
        Assert.Equal(RetiredRestricted, setup.RetiredWindows);              // Windows' own plans: retired since 0.9 Phase 3, never applied anywhere
        Assert.Empty(WindowsConfigurator.Conflicts(resolution.Operations, Pc(Ryzen7950X3d)));

        // A restricted operation no level lists (Game Bar removal) is held to the policy too.
        var appsOnly = SavedSetup([], ["apps.remove-game-bar", "power.high-performance"]);
        var appsResolution = appsOnly.ResolveWindows(context);
        Assert.Empty(appsResolution.Operations);
        Assert.Equal("apps.remove-game-bar", Assert.Single(appsResolution.Skipped).OperationId);
        Assert.Equal(["power.high-performance"], appsOnly.RetiredWindows);

        // The command line's --id stays the user's explicit choice (Phase 1): applied as named, each still checked by the engine.
        Assert.Equal(["cpu.core-parking-off"], LevelPlanning.Resolve([], ["cpu.core-parking-off"], context, ExplicitOperations.AsNamed).Operations.Select(o => o.Id));
    }

    [Theory]
    [InlineData("9800X3D")]
    [InlineData("13700K")]
    public void On_other_machines_the_same_saved_setup_applies_the_restricted_operations_it_names(string machine)
    {
        var m = ByName(machine);
        var resolution = Setup([], ["cpu.core-parking-off", "apps.remove-game-bar", "gaming.game-recording-policy-off"]).ResolveWindows(Context(m, Pc(m)));

        Assert.Equal(["cpu.core-parking-off", "apps.remove-game-bar", "gaming.game-recording-policy-off"], resolution.Operations.Select(o => o.Id));
        Assert.Empty(resolution.Skipped);
    }

    [Fact]
    public void With_the_processor_unread_a_saved_setups_restricted_operations_are_left_alone()
    {
        var setup = SavedSetup([], [.. Restricted]);
        var resolution = setup.ResolveWindows(Context(UnreadCpu, Pc(UnreadCpu)));

        Assert.Empty(resolution.Operations);
        Assert.Equal(LiveRestricted, resolution.Skipped.Select(e => e.OperationId));
        Assert.All(resolution.Skipped, e => Assert.Equal(EntryState.CantTell, e.State));
        Assert.Equal(RetiredRestricted, setup.RetiredWindows);
    }

    // ---- Balanced Mesura, the X3D target, is consistent ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Balanced_mesura_writes_no_core_parking_or_plan_personality_value()
    {
        var plan = Catalog.ById("power.mesura-balanced-plan")!.PowerPlan!;

        Assert.Equal(Balanced, plan.BasedOn);
        Assert.Equal(BalancedMesura, plan.SchemeGuid);
        Assert.Equal(["Processor performance boost policy", "USB selective suspend setting", "USB 3 Link Power Mangement", "Turn off hard disk after", "Link State Power Management"],
            plan.PlanValues.Select(v => v.Name));
        Assert.DoesNotContain(plan.PlanValues, v => v.Setting is CoreParkingMinCores or CoreParkingMinCoresClass1 or PowerPlanType);
        Assert.DoesNotContain(plan.PlanValues, v => v.Subgroup.Equals(NoSubgroup, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.PlanValues, v => v.Name.Contains("parking", StringComparison.OrdinalIgnoreCase));

        // The Maximum plan, which the policy leaves out there, is the one that does both.
        var maximumPlan = Catalog.ById("power.mesura-maximum-plan")!.PowerPlan!;
        Assert.Contains(maximumPlan.PlanValues, v => v.Setting == PowerPlanType && v.Ac == 1);
    }

    [Fact]
    public void On_a_7950X3D_nothing_the_plan_applies_writes_core_parking_or_the_plan_personality_and_it_applies_and_undoes_through_the_engine()
    {
        var pc = Pc(Ryzen7950X3d);
        var context = Context(Ryzen7950X3d, pc);
        var ops = LevelPlanning.Plan(Maximum, context).OperationIds.Select(id => Catalog.ById(id)!).ToList();

        var written = ops.SelectMany(o => o.PowerSettings.Select(s => s.Setting).Concat(o.PowerPlan?.PlanValues.Select(v => v.Setting) ?? [])).ToList();
        Assert.DoesNotContain(written, s => s is CoreParkingMinCores or CoreParkingMinCoresClass1 or PowerPlanType);

        var outcomes = new WindowsConfigurator(pc, pc).Apply(ops, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(outcomes, o => !o.Ok);
        Assert.Equal(BalancedMesura, pc.Active);                                     // Balanced Mesura is the active plan, not Maximum Mesura
        Assert.DoesNotContain(MaximumMesura, pc.Schemes);
        Assert.Equal(7u, pc.ReadPowerSetting(BalancedMesura, "", CoreParkingMinCores)); // core parking as the copied Balanced has it

        // After the run the level is in place on this machine, and the tray has nothing left to count. Balanced Mesura holds Maximum's own drive and PCI Express
        // values on top of its own (the plan is not "partly applied" for that), and a re-run changes nothing. The three settings Windows confirms in effect
        // (pointer acceleration, the accessibility hotkeys, NTFS last access) all answer "disabled" once applied; the stand-in says so.
        pc.Effect = "disabled";
        var after = PlanningContext.Reading(Catalog, Hardware(Ryzen7950X3d), pc);
        Assert.Equal(0u, pc.ReadPowerSetting(BalancedMesura, "", "6738e2c4-e8a5-4a42-b16a-e040e769756e"));   // drives never off (Maximum), not Balanced Mesura's 20 minutes
        Assert.Equal(EntryState.Applied, LevelPlanning.Plan(Maximum, after).Entry("power.mesura-balanced-plan")!.State);
        Assert.Empty(LevelPlanning.Plan(Maximum, after).OperationIds);
        Assert.True(LevelPlanning.InPlace(Maximum, after), string.Join("; ", LevelPlanning.Plan(Maximum, after).Entries
            .Where(e => e.Entry.Role == EntryRole.Default && e.State is not (EntryState.Applied or EntryState.Waiting or EntryState.Unsupported))
            .Select(e => $"{e.OperationId} {e.State} {e.Reason} {e.Detail}")));
        Assert.Equal(0, LevelPlanning.Status(LevelPlanning.Plan(Maximum, after)).Pending);

        var reverted = new WindowsConfigurator(pc, pc).Revert(outcomes);
        Assert.DoesNotContain(reverted, r => r.Status is WindowsOpStatus.Failed or WindowsOpStatus.Blocked);
        Assert.Equal(Balanced, pc.Active);
    }

    [Fact]
    public void Balanced_mesura_counts_as_in_place_only_where_the_profiles_own_wanted_entries_explain_the_difference()
    {
        var pc = Pc(Ryzen7950X3d);
        var context = Context(Ryzen7950X3d, pc);
        new WindowsConfigurator(pc, pc).Apply(LevelPlanning.Plan(Maximum, context).OperationIds.Select(id => Catalog.ById(id)!), TestContext.Current.CancellationToken);
        PlanningContext Now() => PlanningContext.Reading(Catalog, Hardware(Ryzen7950X3d), pc);
        Assert.Equal(EntryState.Applied, LevelPlanning.Plan(Maximum, Now()).Entry("power.mesura-balanced-plan")!.State);

        // The user unticks "drives never off": the plan's own 20 minutes should come back, so the plan is to apply again.
        var unticked = new ProfileChoices(new HashSet<string>(), new Dictionary<string, bool> { ["power.disk-never-off"] = false });
        Assert.Equal(EntryState.ToApply, LevelPlanning.Plan(Maximum, Now(), unticked).Entry("power.mesura-balanced-plan")!.State);

        // Something else changed a plan value (no entry of the profile writes 5): not explained, so the plan is to apply again.
        pc.PowerSettings[$"{BalancedMesura}|6738e2c4-e8a5-4a42-b16a-e040e769756e"] = 5;
        Assert.Equal(EntryState.ToApply, LevelPlanning.Plan(Maximum, Now()).Entry("power.mesura-balanced-plan")!.State);

        // Another plan is active: Balanced Mesura isn't in place, whatever its values.
        pc.PowerSettings[$"{BalancedMesura}|6738e2c4-e8a5-4a42-b16a-e040e769756e"] = 0;
        pc.Active = Balanced;
        Assert.Equal(EntryState.ToApply, LevelPlanning.Plan(Maximum, Now()).Entry("power.mesura-balanced-plan")!.State);
    }

    [Fact]
    public void A_maximum_run_saved_on_a_non_x3d_pc_is_not_in_place_on_a_7950X3D_and_vice_versa()
    {
        // In place is judged on the level as planned for the machine: what Maximum is on a 13700K isn't what it is on a 7950X3D.
        var intel = Pc(Intel13700K);
        new WindowsConfigurator(intel, intel).Apply(LevelPlanning.Plan(Maximum, Context(Intel13700K, intel)).OperationIds.Select(id => Catalog.ById(id)!), TestContext.Current.CancellationToken);
        intel.Effect = "disabled";   // Windows confirms the three effect-checked settings (see above)
        Assert.True(LevelPlanning.InPlace(Maximum, Context(Intel13700K, intel)));
        Assert.False(LevelPlanning.InPlace(Maximum, Context(Ryzen7950X3d, intel)));     // the X3D plan (Balanced Mesura) isn't active there
    }

    // ---- condition-aware conflicts on the real profiles ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_two_mesura_plans_in_maximum_conflict_only_on_paper_their_conditions_keep_them_apart()
    {
        var profile = Profiles.ById(LevelPlanning.MaximumId)!;
        var maximumPlan = profile.Entries.Single(e => e.OperationId == "power.mesura-maximum-plan");
        var balancedPlan = profile.Entries.Single(e => e.OperationId == "power.mesura-balanced-plan");

        Assert.NotEmpty(WindowsConfigurator.Conflicts([Catalog.ById(maximumPlan.OperationId)!, Catalog.ById(balancedPlan.OperationId)!]));
        Assert.True(HardwareCondition.Disjoint(maximumPlan.Conditions.Select(c => c.When), balancedPlan.Conditions.Select(c => c.When)));
        Assert.Empty(LevelPlanning.ConditionAwareConflicts(profile, Catalog));

        // Without the owner's restriction, the pair would be a real conflict on a PC that isn't an X3D.
        var unrestricted = profile with { Entries = [.. profile.Entries.Select(e => e with { Restrictions = [] })] };
        Assert.NotEmpty(LevelPlanning.ConditionAwareConflicts(unrestricted, Catalog));
    }

    // ---- the policy as data ----------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_policy_is_one_restriction_over_exactly_the_approved_operations_and_every_level_entry_carries_it()
    {
        var restriction = Assert.Single(Profiles.Restrictions);
        Assert.Equal(("dual-ccd-x3d", "not-x3d-dual-ccd"), (restriction.Id, restriction.When.Cpu));
        Assert.Equal(Restricted, restriction.Operations);   // unchanged by the 0.9 Phase 3 retirements (accepted policy)
        Assert.All(restriction.Operations, id => Assert.True(Catalog.ById(id) is not null || WindowsCatalog.Retired.ById(id) is not null, id));
        Assert.All(RetiredRestricted, id => Assert.Null(Catalog.ById(id)));

        foreach (var entry in Profiles.Profiles.SelectMany(p => p.Entries))
        {
            Assert.Equal(Restricted.Contains(entry.OperationId), entry.Restrictions.Count == 1);
        }

        // The Balanced Mesura entry of Reference is unconditional: Reference's plan is the same on every PC.
        Assert.Null(Profiles.ById(LevelPlanning.ReferenceId)!.Entries.Single(e => e.OperationId == "power.mesura-balanced-plan").When);
    }

    [Theory]
    [InlineData("""{ "id": "x", "ops": [ "cpu.core-parking-off" ], "why": "w" }""", "no 'when'")]
    [InlineData("""{ "id": "x", "when": { "cpu": "not-x3d-dual-ccd" }, "ops": [], "why": "w" }""", "names no operations")]
    [InlineData("""{ "id": "x", "when": { "cpu": "not-x3d-dual-ccd" }, "ops": [ "cpu.core-parking-off" ] }""", "has no 'why'")]
    [InlineData("""{ "id": "x", "when": { "cpu": "zen" }, "ops": [ "cpu.core-parking-off" ], "why": "w" }""", "unknown cpu")]
    public void A_malformed_restriction_is_refused(string restriction, string message)
    {
        var json = $$"""
            { "kind": "Mesura.OptimizationProfiles", "schemaVersion": 1, "groups": [ { "id": "cpu", "name": "Processor" } ], "why": { "cpu.core-parking-off": "t" },
              "restrictions": [ {{restriction}} ], "profiles": [ { "id": "p", "entries": [ { "op": "cpu.core-parking-off", "group": "cpu" } ] } ] }
            """;

        var e = Assert.Throws<FormatException>(() => OptimizationProfiles.Parse(json));
        Assert.Contains(message, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_edited_catalogs_own_level_is_held_to_the_policy_too()
    {
        var level = new WindowsPreset("my-level", "My level", "", ["cpu.core-parking-off", "repair.threaded-dpc"], PresetTier.Primary, "", []);
        var plan = LevelPlanning.Plan(level, Context(Ryzen7950X3d, Pc(Ryzen7950X3d)));

        Assert.Equal(EntryState.Unsupported, plan.Entry("cpu.core-parking-off")!.State);
        Assert.DoesNotContain("cpu.core-parking-off", plan.OperationIds);
    }

    private static string ProfilesJson()
    {
        using var stream = typeof(OptimizationProfiles).Assembly.GetManifestResourceStream("profiles.json")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
