using Mesura.Core.Hardware;
using Mesura.Core.Profiles;
using Mesura.Core.Tests.Windows;
using Mesura.Core.Windows;

namespace Mesura.Core.Tests.Profiles;

/// <summary>
/// The planner against an in-memory Windows (nothing real is read or written): what each entry becomes on a desktop, a laptop, a Home edition, a PC
/// with little memory or unread hardware; what the user's choices make of it; and that applying a plan through the real engine works and undoes.
/// </summary>
public sealed class ProfilePlannerTests
{
    private const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private static readonly WindowsCatalog Catalog = WindowsCatalog.Default;
    private static readonly OptimizationProfiles Profiles = OptimizationProfiles.Default;

    private static OptimizationProfile Maximum => Profiles.ById("maximum-performance")!;

    private static OptimizationProfile Reference => Profiles.ById("recommended-gaming")!;

    /// <summary>A PC whose Balanced scheme registers every power setting any profile entry or plan touches, at a value none of them wants.</summary>
    private static FakeWindows Pc(bool battery = false, bool hybrid = false, bool pro = true, ulong memoryGb = 32)
    {
        var pc = new FakeWindows { HasBattery = battery, ModernStandby = false, HybridCpu = hybrid, PolicyEdition = pro, PhysicalMemoryBytes = memoryGb << 30 };
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

    private static HardwareProfile Hardware(DeviceForm form = DeviceForm.Desktop, bool nvme = true, bool sata = false) => new(
        new WindowsPlatform(26100, "Professional", "24H2", true), form, new PowerPlatform(form == DeviceForm.Portable, false, true),
        new CpuProfile("Test CPU", CpuMaker.Intel, 8, 16, false), 32UL << 30,
        [new GpuProfile("Test GPU", GpuMaker.Nvidia, 0x10DE, 1, "1.0", false)],
        [.. new[] { nvme ? new DiskProfile("NVMe", StorageClass.Nvme, 1, true, Mesura.Core.Raw.BusKind.Nvme) : null, sata ? new DiskProfile("SATA", StorageClass.Ssd, 1, !nvme, Mesura.Core.Raw.BusKind.Sata) : null }.OfType<DiskProfile>()],
        []);

    private static ProfilePlan Plan(OptimizationProfile profile, FakeWindows pc, HardwareProfile? hardware, ProfileChoices? choices = null, IReadOnlyDictionary<string, Activation>? waiting = null,
        WindowsCatalog? catalog = null) =>
        ProfilePlanner.Plan(profile, catalog ?? Catalog, hardware, pc, id => (catalog ?? Catalog).ById(id) is { } op ? WindowsEvaluator.Evaluate(op, pc) : null, waiting, choices);

    [Fact]
    public void On_a_desktop_maximum_applies_everything_that_applies_and_the_plan_comes_first()
    {
        var pc = Pc();
        var plan = Plan(Maximum, pc, Hardware());

        var ids = plan.OperationIds;
        Assert.Equal("power.mesura-maximum-plan", ids[0]);
        foreach (var id in new[] { "cpu.core-parking-off", "power.power-throttling-off", "memory.compression-off", "memory.keep-kernel-in-ram", "system.ntfs-8dot3-off",
                                   "input.pointer-precision-off", "taskbar.widgets-policy-off", "privacy.diagnostic-data-required" })
        {
            Assert.Contains(id, ids);
        }

        // 2026-11-04 (owner): hardware-accelerated GPU scheduling is offered unticked (no confirmed gain); Anti-Lag is for a Radeon and this PC has an NVIDIA card.
        Assert.DoesNotContain("gpu.hardware-scheduling-on", ids);
        Assert.Equal(EntryRole.Optional, plan.Entry("gpu.hardware-scheduling-on")!.Entry.Role);
        Assert.Equal(EntryState.Unsupported, plan.Entry("gpu.amd-anti-lag-on")!.State);
        Assert.Equal("hardware.gpu.amd", plan.Entry("gpu.amd-anti-lag-on")!.Reason);

        // The optional entries are offered and not applied; the SATA entry is for a SATA drive and this PC has none.
        Assert.DoesNotContain("visual.transparency-off", ids);
        Assert.DoesNotContain("gaming.background-recording-off", ids);
        Assert.Equal(EntryState.Unsupported, plan.Entry("power.sata-link-power-off-ac")!.State);
        Assert.Equal("hardware.disk.sata", plan.Entry("power.sata-link-power-off-ac")!.Reason);
        Assert.True(plan.Entry("visual.transparency-off")!.Selectable);
    }

    [Fact]
    public void The_plans_own_values_cover_the_power_entries_they_already_write_so_nothing_is_applied_twice()
    {
        var plan = Plan(Maximum, Pc(hybrid: true), Hardware());

        foreach (var id in new[] { "power.usb-keep-awake", "power.disk-never-off", "power.pcie-aspm-off", "power.cpu-epp-performance-ac", "power.nvme-stay-active-ac" })
        {
            var entry = plan.Entry(id)!;
            Assert.True(entry.State == EntryState.Covered, $"{id} is {entry.State} ({entry.Detail})");
            Assert.Equal("power.mesura-maximum-plan", entry.CoveredBy);
            Assert.False(entry.Included);
        }

        // What the plan does not write stays an entry of its own: core parking of the main cores, the registry and system settings.
        Assert.Equal(EntryState.ToApply, plan.Entry("cpu.core-parking-off")!.State);
        Assert.True(plan.Entry("cpu.core-parking-off")!.Included);
        Assert.True(plan.Entry("power.power-throttling-off")!.Included);
    }

    [Fact]
    public void When_the_user_unticks_the_plan_the_entries_it_covered_are_theirs_again()
    {
        var choices = new ProfileChoices(new HashSet<string>(), new Dictionary<string, bool> { ["power.mesura-maximum-plan"] = false });

        var plan = Plan(Maximum, Pc(), Hardware(), choices);

        Assert.False(plan.Entry("power.mesura-maximum-plan")!.Included);
        Assert.Equal(EntryState.ToApply, plan.Entry("power.usb-keep-awake")!.State);
        Assert.True(plan.Entry("power.usb-keep-awake")!.Included);
        Assert.DoesNotContain("power.mesura-maximum-plan", plan.OperationIds);
    }

    [Fact]
    public void On_a_laptop_maximum_applies_the_ac_only_entries_and_power_throttling_off_too()
    {
        var plan = Plan(Maximum, Pc(battery: true, hybrid: true), Hardware(DeviceForm.Portable));
        Assert.Equal(EntryState.Covered, plan.Entry("power.cpu-epp-performance-ac")!.State);

        Assert.True(plan.Entry("cpu.core-parking-off")!.Included);
        Assert.True(plan.Entry("power.mesura-maximum-plan")!.Included);
        // The owner's decision: power throttling is off in Maximum on a laptop as well, whatever it costs the battery, and the entry carries the cost.
        var throttling = plan.Entry("power.power-throttling-off")!;
        Assert.Equal(EntryState.ToApply, throttling.State);
        Assert.True(throttling.Included);
        Assert.True(throttling.Entry.SideEffects);
    }

    [Fact]
    public void Windows_home_skips_every_machine_policy_with_the_edition_as_the_reason_including_telemetry()
    {
        var plan = Plan(Maximum, Pc(pro: false), Hardware());

        var telemetry = plan.Groups.Single(g => g.Id == "telemetry");
        Assert.All(telemetry.Entries.Where(e => e.Entry.Role == EntryRole.Default), e => Assert.Equal((EntryState.Unsupported, PlanReason.Edition), (e.State, e.Reason)));
        Assert.False(telemetry.Visible);
        Assert.Equal(telemetry.Entries.Count, telemetry.NotHere);
        // The per-user entries do not depend on the edition.
        Assert.True(plan.Entry("privacy.no-advertising-id")!.Included);
    }

    [Fact]
    public void Little_memory_skips_the_memory_entries_and_an_unread_amount_leaves_them_alone()
    {
        var small = Plan(Maximum, Pc(memoryGb: 8), Hardware());
        Assert.Equal(PlanReason.Memory, small.Entry("memory.compression-off")!.Reason);
        Assert.Equal(PlanReason.Memory, small.Entry("memory.keep-kernel-in-ram")!.Reason);

        var pc = Pc();
        pc.PhysicalMemoryBytes = null;
        var unread = Plan(Maximum, pc, Hardware());
        Assert.Equal(EntryState.CantTell, unread.Entry("memory.compression-off")!.State);
        Assert.False(unread.Entry("memory.compression-off")!.Included);
    }

    [Fact]
    public void Hardware_that_has_not_been_read_leaves_the_hardware_entries_alone_and_the_rest_proceeds()
    {
        var plan = Plan(Maximum, Pc(), hardware: null);

        var nvme = plan.Entry("power.nvme-stay-active-ac")!;
        Assert.Equal((EntryState.CantTell, PlanReason.Unread), (nvme.State, nvme.Reason));
        Assert.False(nvme.Included);
        Assert.True(plan.Entry("cpu.core-parking-off")!.Included);
    }

    [Fact]
    public void An_nvme_drive_makes_its_entry_applicable_and_a_pc_without_one_skips_it_with_the_hardware_named()
    {
        Assert.Equal(EntryState.Covered, Plan(Maximum, Pc(), Hardware(nvme: true)).Entry("power.nvme-stay-active-ac")!.State);

        var sataOnly = Plan(Maximum, Pc(), Hardware(nvme: false, sata: true));
        Assert.Equal("hardware.disk.nvme", sataOnly.Entry("power.nvme-stay-active-ac")!.Reason);
        Assert.Equal(EntryState.Covered, sataOnly.Entry("power.sata-link-power-off-ac")!.State);
    }

    [Fact]
    public void A_group_switched_off_applies_none_of_its_entries_and_the_others_are_untouched()
    {
        var all = Plan(Maximum, Pc(), Hardware());
        var choices = new ProfileChoices(new HashSet<string> { "privacy", "cpu" }, new Dictionary<string, bool>());

        var plan = Plan(Maximum, Pc(), Hardware(), choices);

        Assert.False(plan.Groups.Single(g => g.Id == "privacy").Enabled);
        Assert.Equal(0, plan.Groups.Single(g => g.Id == "cpu").Included);
        Assert.Equal(0, plan.Groups.Single(g => g.Id == "privacy").Included);
        Assert.Equal(all.Groups.Single(g => g.Id == "telemetry").Included, plan.Groups.Single(g => g.Id == "telemetry").Included);
        Assert.Equal(all.OperationIds.Count - all.Groups.Single(g => g.Id == "privacy").Included - all.Groups.Single(g => g.Id == "cpu").Included, plan.OperationIds.Count);
    }

    [Fact]
    public void A_single_entry_can_be_unticked_and_an_optional_one_ticked()
    {
        var choices = new ProfileChoices(new HashSet<string>(), new Dictionary<string, bool> { ["memory.compression-off"] = false, ["visual.transparency-off"] = true });

        var plan = Plan(Maximum, Pc(), Hardware(), choices);

        Assert.DoesNotContain("memory.compression-off", plan.OperationIds);
        Assert.Contains("visual.transparency-off", plan.OperationIds);
        Assert.Contains("memory.keep-kernel-in-ram", plan.OperationIds);
    }

    [Fact]
    public void An_entry_that_cannot_apply_is_never_included_whatever_the_user_chose()
    {
        var choices = new ProfileChoices(new HashSet<string>(), new Dictionary<string, bool> { ["power.power-throttling-off"] = true, ["power.sata-link-power-off-ac"] = true });

        var plan = Plan(Maximum, Pc(pro: false), Hardware());

        Assert.DoesNotContain("power.sata-link-power-off-ac", Plan(Maximum, Pc(), Hardware(), choices).OperationIds); // no SATA drive here
        Assert.DoesNotContain("power.power-throttling-off", Plan(Maximum, Pc(pro: false), Hardware(), choices).OperationIds); // a machine policy Home ignores
        Assert.NotNull(plan.Entry("power.power-throttling-off"));
    }

    [Fact]
    public void What_is_already_set_is_applied_and_what_waits_for_a_restart_says_so()
    {
        var pc = Pc();
        var op = Catalog.ById("repair.threaded-dpc")!;
        new WindowsConfigurator(pc, pc).Apply([op], TestContext.Current.CancellationToken);
        var sign = Catalog.ById("input.accessibility-shortcuts-off")!;
        new WindowsConfigurator(pc, pc).Apply([sign], TestContext.Current.CancellationToken);
        pc.Effect = "disabled"; // what Windows reports for the accessibility hotkeys once the values are stored
        var waiting = new Dictionary<string, Activation> { [sign.Id] = Activation.SignOut };

        var plan = Plan(Reference, pc, Hardware(), waiting: waiting);

        Assert.Equal(EntryState.Applied, plan.Entry("repair.threaded-dpc")!.State);
        Assert.False(plan.Entry("repair.threaded-dpc")!.Included);
        var entry = plan.Entry("input.accessibility-shortcuts-off")!;
        Assert.Equal((EntryState.Waiting, (Activation?)Activation.SignOut), (entry.State, entry.Waiting));
        Assert.Equal(1, plan.Groups.Single(g => g.Id == "windows").Applied);
        Assert.Equal(1, plan.Groups.Single(g => g.Id == "gaming").Waiting);
    }

    [Fact]
    public void An_entry_whose_operation_an_edited_catalog_lacks_is_reported_not_thrown()
    {
        var smaller = WindowsCatalog.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "engine-fixtures.catalog.json")));

        var plan = Plan(Reference, Pc(), Hardware(), catalog: smaller);

        Assert.All(plan.Entries.Where(e => e.Operation is null), e => Assert.Equal(PlanReason.NotInCatalog, e.Reason));
        Assert.Contains(plan.Entries, e => e.Operation is null);
        Assert.Empty(plan.OperationIds);
    }

    [Fact]
    public void A_primary_preset_of_an_edited_catalog_with_no_built_in_profile_is_planned_as_one_group()
    {
        var preset = new WindowsPreset("my-level", "My level", "", ["repair.threaded-dpc", "gaming.game-mode-on"], PresetTier.Primary, "", ["visual.transparency-off"]);

        var profile = Profiles.FromPreset(preset);
        var plan = Plan(profile, Pc(), Hardware());

        Assert.Equal("my-level", profile.Id);
        Assert.Equal(["windows"], plan.Groups.Select(g => g.Id));
        Assert.Equal(["repair.threaded-dpc", "gaming.game-mode-on", "visual.transparency-off"], profile.Entries.Select(e => e.OperationId));
        Assert.Equal([EntryRole.Default, EntryRole.Default, EntryRole.Optional], profile.Entries.Select(e => e.Role));
        Assert.DoesNotContain(plan.Entries, e => e.Operation is null);
        Assert.DoesNotContain("visual.transparency-off", plan.OperationIds); // optional: offered, not applied
        Assert.True(plan.Entry("visual.transparency-off")!.Selectable);
    }

    [Fact]
    public void An_operation_the_app_has_not_read_yet_is_left_alone()
    {
        var plan = ProfilePlanner.Plan(Reference, Catalog, Hardware(), Pc(), _ => null);

        Assert.All(plan.Entries, e => Assert.Equal(EntryState.CantTell, e.State));
        Assert.Empty(plan.OperationIds);
    }

    [Fact]
    public void Reference_on_a_desktop_is_telemetry_privacy_windows_gaming_and_the_balanced_plan_only()
    {
        var plan = Plan(Reference, Pc(), Hardware());

        Assert.Equal(["telemetry", "privacy", "windows", "power", "gaming"], plan.Groups.Where(g => g.Visible).Select(g => g.Id));
        Assert.Equal("power.mesura-balanced-plan", plan.OperationIds[0]);
        Assert.DoesNotContain("power.mesura-maximum-plan", plan.OperationIds);
        // Two entries are already in Windows' default state on this PC (their values are Windows' own defaults), so they are applied already, not planned.
        Assert.Equal(["repair.threaded-dpc", "gaming.game-mode-on"], plan.Entries.Where(e => e.State == EntryState.Applied).Select(e => e.OperationId));
        // The five network adapter presets wait for the adapters to be read (this hardware lists none): left alone, with the reason.
        Assert.Equal(Reference.DefaultIds.Count - 2 - 5, plan.OperationIds.Count);
        Assert.All(plan.Entries.Where(e => e.OperationId.StartsWith("network.preset-", StringComparison.Ordinal)), e => Assert.Equal(EntryState.CantTell, e.State));
    }

    [Fact]
    public void The_whole_maximum_plan_goes_through_the_real_engine_without_a_failure_a_conflict_or_a_battery_write_and_undoes_to_the_exact_start()
    {
        var pc = Pc(hybrid: true);
        var before = pc.Registry.ToDictionary(p => p.Key, p => p.Value);
        var plan = Plan(Maximum, pc, Hardware());
        var ops = plan.OperationIds.Select(id => Catalog.ById(id)!).ToList();
        Assert.Empty(WindowsConfigurator.Conflicts(ops, pc));

        var outcomes = new WindowsConfigurator(pc, pc).Apply(ops, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(outcomes, o => o.Status is WindowsOpStatus.Failed or WindowsOpStatus.NeedsElevation or WindowsOpStatus.Blocked);
        Assert.DoesNotContain(pc.Writes, w => w.Contains(" dc ", StringComparison.Ordinal));
        Assert.Equal("76d36892-93d0-4c33-9e09-c2e1fb87cf49", pc.Active);

        // Read back: what the plan said would change is now in the target state (the effect-checked ones wait for what Windows itself reports).
        var after = Plan(Maximum, pc, Hardware());
        foreach (var id in new[] { "cpu.core-parking-off", "power.power-throttling-off", "privacy.diagnostic-data-required", "taskbar.widgets-policy-off", "memory.compression-off" })
        {
            Assert.True(after.Entry(id)!.State is EntryState.Applied or EntryState.Waiting, $"{id} is {after.Entry(id)!.State}");
        }

        var reverted = new WindowsConfigurator(pc, pc).Revert(outcomes);
        Assert.DoesNotContain(reverted, r => r.Status is WindowsOpStatus.Failed or WindowsOpStatus.Blocked);
        Assert.Equal(Balanced, pc.Active);
        Assert.Equal(before.OrderBy(p => p.Key), pc.Registry.OrderBy(p => p.Key));
    }
}
