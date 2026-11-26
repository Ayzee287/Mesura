using Mesura.Core.Apps;
using Mesura.Core.Profiles;
using Mesura.Core.Setup;
using Mesura.Core.Windows;

namespace Mesura.Core.Tests.Windows;

/// <summary>
/// 0.9 Phase 3 (vault D057): four new catalog operations, each reviewed on its own, and three retirements. Every operation goes through the real engine
/// against the in-memory Windows: applicability, the exact before-image, exact Undo, idempotence, the restart it needs, the journal and recovery, conflicts and
/// refusal when the state can't be read. Nothing touches a real PC.
/// </summary>
public sealed class CatalogPhase3Tests
{
    private const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string Ultimate = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    private const string DisabledComponents = @"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\DisabledComponents";
    private const string Consolidator = @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator";
    private const string UsbCeip = @"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip";
    private const string DmClient = @"\Microsoft\Windows\Feedback\Siuf\DmClient";
    private const string DmClientOnScenarioDownload = @"\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload";

    private static readonly WindowsCatalog Catalog = WindowsCatalog.Default;

    private static readonly string[] Added = ["tasks.ceip-off", "tasks.feedback-off", "services.windows-search-off", "features.virtual-machine-platform-off", "network.prefer-ipv4"];

    private static readonly string[] RetiredIds = ["power.high-performance", "power.ultimate-performance", "network.adapter-ipv6-binding-off"];

    private static WindowsOperation Op(string id) => Catalog.ById(id) ?? throw new InvalidOperationException(id);

    private static WindowsOpOutcome ApplyOne(FakeWindows pc, WindowsOperation op, IChangeJournal? journal = null) =>
        journal is null
            ? new WindowsConfigurator(pc, pc).Apply([op], TestContext.Current.CancellationToken).Single()
            : new WindowsConfigurator(pc, pc).Apply([op], TestContext.Current.CancellationToken, journal).Single();

    private static SetupRun? Recover(FakeWindows pc, MemoryJournal journal, out RunJournalFindings findings) =>
        RunJournal.Recover(RunJournal.Parse(journal.Text)!, new WindowsConfigurator(pc, pc).Check, null, "2026-11-25T12:05:00.0000000Z", out findings);

    // ---- what was added, and what wasn't ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_four_operations_are_in_the_catalog_in_no_profile_and_no_preset_each_with_a_cost_a_restart_rule_and_no_gain_claimed()
    {
        Assert.Equal(5, Added.Length);
        foreach (var id in Added)
        {
            var op = Op(id);
            Assert.True(op.Applicable, id);
            Assert.Equal(ValueBasis.Documented, op.Basis);
            Assert.Equal(OperationBenefit.Unverified, op.Evidence);
            Assert.False(string.IsNullOrWhiteSpace(op.Limitations), id);
            Assert.DoesNotContain("faster", op.Summary, StringComparison.OrdinalIgnoreCase);
            Assert.True(op.NeedsElevation, id);                                         // all machine-wide
        }

        // Their profile assignment, decided in Phase 4 (D058, docs/plan-0.9-phase4.md): the two task entries in both levels, the rest offered unticked in
        // Maximum only.
        (EntryRole? Reference, EntryRole? Maximum) Roles(string id) =>
            (OptimizationProfiles.Default.ById(LevelPlanning.ReferenceId)!.Entries.SingleOrDefault(e => e.OperationId == id)?.Role,
             OptimizationProfiles.Default.ById(LevelPlanning.MaximumId)!.Entries.SingleOrDefault(e => e.OperationId == id)?.Role);
        Assert.Equal((EntryRole.Default, EntryRole.Default), Roles("tasks.ceip-off"));
        Assert.Equal((EntryRole.Default, EntryRole.Default), Roles("tasks.feedback-off"));
        Assert.All(new[] { "services.windows-search-off", "features.virtual-machine-platform-off", "network.prefer-ipv4" },
            id => Assert.Equal(((EntryRole?)null, (EntryRole?)EntryRole.Optional), Roles(id)));

        Assert.Equal(Activation.Immediate, Op("tasks.ceip-off").Activation);
        Assert.Equal(Activation.Immediate, Op("tasks.feedback-off").Activation);
        Assert.Equal(Activation.Reboot, Op("services.windows-search-off").Activation);
        Assert.Equal(Activation.Reboot, Op("features.virtual-machine-platform-off").Activation);
        Assert.Equal(Activation.Reboot, Op("network.prefer-ipv4").Activation);
    }

    [Fact]
    public void Withheld_on_review_the_compatibility_appraiser_tasks_and_no_new_hibernation_entry()
    {
        // The Appraiser tasks feed Windows' upgrade-compatibility checks (removed on purpose in the 0.4 audit as feature-update safeguards) and Microsoft
        // documents neither them nor what turning them off does to feature updates: withheld, not forced in (D057).
        Assert.DoesNotContain(Catalog.Operations, o => o.Tasks.Any(t => t.Path.Contains("Application Experience", StringComparison.OrdinalIgnoreCase)));
        // The existing hibernation entry is unchanged; no second, desktop-only one was added (it is in no reviewed plan).
        Assert.Single(Catalog.Operations, o => o.SystemSettings.Any(s => s.Kind == "hibernate"));
        Assert.Empty(Op("system.hibernation-off").Requirements);
    }

    // ---- the scheduled tasks ---------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("tasks.ceip-off", Consolidator, UsbCeip)]
    [InlineData("tasks.feedback-off", DmClient, DmClientOnScenarioDownload)]
    public void A_task_operation_disables_its_tasks_reads_them_back_is_idempotent_and_undo_restores_exactly_the_prior_state(string id, string first, string second)
    {
        var op = Op(id);
        var pc = new FakeWindows();
        pc.Tasks[first] = FeatureState.Enabled;
        pc.Tasks[second] = FeatureState.Disabled;   // already off before Mesura: not recorded, so Undo leaves it off

        Assert.Equal(OperationState.Partial, WindowsEvaluator.Evaluate(op, pc).State);
        var applied = ApplyOne(pc, op);

        Assert.Equal(WindowsOpStatus.Applied, applied.Status);
        Assert.Equal(Activation.Immediate, applied.Activation);
        var change = Assert.Single(applied.Changes);
        Assert.Equal((ChangeKind.Task, first, "Enabled", "Disabled"), (change.Kind, change.Path, change.Before, change.After));
        Assert.Equal((FeatureState.Disabled, FeatureState.Disabled), (pc.Tasks[first], pc.Tasks[second]));
        Assert.Equal(OperationState.Applied, WindowsEvaluator.Evaluate(op, pc).State);

        var writes = pc.Writes.Count;
        Assert.Equal(WindowsOpStatus.AlreadyApplied, ApplyOne(pc, op).Status);   // idempotent: nothing written again
        Assert.Equal(writes, pc.Writes.Count);

        var reverted = new WindowsConfigurator(pc, pc, Catalog).Revert([applied]).Single();
        Assert.Equal(WindowsOpStatus.Reverted, reverted.Status);
        Assert.Equal((FeatureState.Enabled, FeatureState.Disabled), (pc.Tasks[first], pc.Tasks[second]));   // exactly as before
    }

    [Fact]
    public void Task_operations_skip_a_task_this_windows_lacks_do_not_apply_where_it_has_none_and_refuse_when_a_task_cannot_be_read()
    {
        var op = Op("tasks.ceip-off");

        var none = new FakeWindows();   // neither task exists (an image that removed them)
        Assert.Equal(OperationState.Inapplicable, WindowsEvaluator.Evaluate(op, none).State);
        Assert.Equal(WindowsOpStatus.Inapplicable, ApplyOne(none, op).Status);

        var one = new FakeWindows();
        one.Tasks[Consolidator] = FeatureState.Enabled;  // UsbCeip absent
        Assert.Equal(WindowsOpStatus.Applied, ApplyOne(one, op).Status);
        Assert.False(one.Tasks.ContainsKey(UsbCeip));

        var unread = new FakeWindows();
        unread.Tasks[Consolidator] = FeatureState.Unknown;
        unread.Tasks[UsbCeip] = FeatureState.Enabled;
        Assert.Equal(OperationState.Unknown, WindowsEvaluator.Evaluate(op, unread).State);
        Assert.NotEqual(WindowsOpStatus.Applied, ApplyOne(unread, op).Status);
        Assert.Empty(unread.Writes);
    }

    [Fact]
    public void A_task_change_is_journaled_before_it_is_made_and_a_crash_at_that_write_recovers_to_nothing_left_to_undo()
    {
        var op = Op("tasks.ceip-off");
        var pc = new FakeWindows { CrashAtWrite = 1 };
        pc.Tasks[Consolidator] = FeatureState.Enabled;
        pc.Tasks[UsbCeip] = FeatureState.Enabled;
        var journal = new MemoryJournal();

        Assert.Throws<SimulatedCrash>(() => ApplyOne(pc, op, journal));
        pc.CrashAtWrite = null;

        Assert.Equal(1, journal.Count);   // in the journal before the write
        Assert.Null(Recover(pc, journal, out var findings));   // never made: nothing to undo
        Assert.Equal(0, findings.InPlace + findings.Other);
        Assert.Equal(FeatureState.Enabled, pc.Tasks[Consolidator]);
    }

    // ---- Windows Search ---------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Windows_search_is_set_to_disabled_at_the_next_start_and_undo_puts_back_the_exact_start_type()
    {
        var op = Op("services.windows-search-off");
        var pc = new FakeWindows();
        pc.Services["WSearch"] = 2;   // Automatic (delayed start is a separate value, which Mesura doesn't touch)

        var applied = ApplyOne(pc, op);

        Assert.Equal(WindowsOpStatus.Applied, applied.Status);
        Assert.Equal(Activation.Reboot, applied.Activation);
        var change = Assert.Single(applied.Changes);
        Assert.Equal((ChangeKind.Service, "WSearch", "2", "4"), (change.Kind, change.Name, change.Before, change.After));
        Assert.Equal(4, pc.Services["WSearch"]);
        Assert.Equal(WindowsOpStatus.AlreadyApplied, ApplyOne(pc, op).Status);

        Assert.Equal(WindowsOpStatus.Reverted, new WindowsConfigurator(pc, pc, Catalog).Revert([applied]).Single().Status);
        Assert.Equal(2, pc.Services["WSearch"]);

        // A PC whose image removed the service: not applicable, nothing written.
        var stripped = new FakeWindows();
        Assert.Equal(OperationState.Inapplicable, WindowsEvaluator.Evaluate(op, stripped).State);
        Assert.Empty(stripped.Writes);
    }

    [Fact]
    public void Windows_search_names_what_is_lost()
    {
        var op = Op("services.windows-search-off");
        Assert.Contains("Outlook", op.Limitations, StringComparison.Ordinal);
        Assert.Contains("File Explorer", op.Limitations, StringComparison.Ordinal);
        Assert.Contains("next start of Windows", op.Limitations, StringComparison.Ordinal);
        // The retired 0.4 entry for the same service keeps its own id, so an old setup naming it doesn't start applying this one.
        Assert.NotNull(WindowsCatalog.Retired.ById("services.search-indexing-off"));
        Assert.Null(Catalog.ById("services.search-indexing-off"));
        Assert.Empty(WindowsConfigurator.Conflicts([op, WindowsCatalog.Retired.ById("services.search-indexing-off")!]));   // same target, same value
    }

    // ---- Virtual Machine Platform -----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Virtual_machine_platform_is_turned_off_with_a_restart_and_undo_turns_it_on_again()
    {
        var op = Op("features.virtual-machine-platform-off");
        var pc = new FakeWindows();
        pc.Features["VirtualMachinePlatform"] = FeatureState.Enabled;

        var applied = ApplyOne(pc, op);

        Assert.Equal(WindowsOpStatus.Applied, applied.Status);
        Assert.Equal(Activation.Reboot, applied.Activation);
        var change = Assert.Single(applied.Changes);
        Assert.Equal((ChangeKind.Feature, "VirtualMachinePlatform", "Enabled", "Disabled"), (change.Kind, change.Name, change.Before, change.After));
        Assert.Equal(WindowsOpStatus.AlreadyApplied, ApplyOne(pc, op).Status);

        Assert.Equal(WindowsOpStatus.Reverted, new WindowsConfigurator(pc, pc, Catalog).Revert([applied]).Single().Status);
        Assert.Equal(FeatureState.Enabled, pc.Features["VirtualMachinePlatform"]);
    }

    [Fact]
    public void Virtual_machine_platform_does_not_apply_where_windows_lacks_it_and_is_refused_when_its_state_cannot_be_read()
    {
        var op = Op("features.virtual-machine-platform-off");

        Assert.Equal(OperationState.Inapplicable, WindowsEvaluator.Evaluate(op, new FakeWindows()).State);   // the image doesn't carry it

        var unread = new FakeWindows();
        unread.Features["VirtualMachinePlatform"] = FeatureState.Unknown;
        Assert.Equal(OperationState.Unknown, WindowsEvaluator.Evaluate(op, unread).State);
        Assert.Equal(WindowsOpStatus.Unknown, ApplyOne(unread, op).Status);
        Assert.Empty(unread.Writes);
    }

    [Fact]
    public void Virtual_machine_platform_states_its_cost_to_wsl2_sandbox_and_other_virtual_machines_without_claiming_more_than_microsoft_says()
    {
        var op = Op("features.virtual-machine-platform-off");
        Assert.Contains("WSL 2", op.Description, StringComparison.Ordinal);
        Assert.Contains("WSL 2", op.Limitations, StringComparison.Ordinal);
        Assert.Contains("Windows Sandbox", op.Limitations, StringComparison.Ordinal);       // what Microsoft's Sandbox requirements say, and that it wasn't checked
        Assert.Contains("virtual machines", op.Limitations, StringComparison.Ordinal);
        Assert.Contains("doesn't check whether any are installed", op.Limitations, StringComparison.Ordinal);
        Assert.Contains("doesn't claim the hypervisor stops", op.Description, StringComparison.Ordinal);
        Assert.Contains("restart", op.Limitations, StringComparison.Ordinal);
    }

    [Fact]
    public void A_crash_at_the_feature_write_is_recovered_as_never_made_and_after_the_write_as_in_place_with_undo()
    {
        var op = Op("features.virtual-machine-platform-off");

        // Dies before dism changed anything: the journal names the change, the PC still shows the before-state, nothing to undo (0.9: "not made", not "other").
        var early = new FakeWindows { CrashAtWrite = 1 };
        early.Features["VirtualMachinePlatform"] = FeatureState.Enabled;
        var journal = new MemoryJournal();
        Assert.Throws<SimulatedCrash>(() => ApplyOne(early, op, journal));
        early.CrashAtWrite = null;
        Assert.Null(Recover(early, journal, out var findings));
        Assert.Equal((0, 0), (findings.InPlace, findings.Other));

        // The write happened but the run's record never did (the process died after it): the interrupted record holds it, and its Undo turns the feature back on.
        var late = new FakeWindows();
        late.Features["VirtualMachinePlatform"] = FeatureState.Enabled;
        var lateJournal = new MemoryJournal();
        ApplyOne(late, op, lateJournal);
        var record = Recover(late, lateJournal, out var lateFindings);
        Assert.NotNull(record);
        Assert.Equal(1, lateFindings.InPlace);
        Assert.All(new WindowsConfigurator(late, late).Revert(record.WindowsSteps), o => Assert.Equal(WindowsOpStatus.Reverted, o.Status));
        Assert.Equal(FeatureState.Enabled, late.Features["VirtualMachinePlatform"]);
    }

    // ---- Prefer IPv4 ------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Prefer_ipv4_sets_only_bit_0x20_keeps_every_other_bit_needs_a_restart_and_undo_restores_the_exact_previous_value()
    {
        var op = Op("network.prefer-ipv4");
        var setting = Assert.Single(op.Registry);
        Assert.Equal((32u, 0u, RegistryKind.DWord), (setting.SetBits, setting.ClearBits, setting.Kind));

        // Absent: written as 32; Undo removes the value again.
        var fresh = new FakeWindows();
        var applied = ApplyOne(fresh, op);
        Assert.Equal((WindowsOpStatus.Applied, Activation.Reboot), (applied.Status, applied.Activation));
        Assert.Equal("32", fresh.Registry[DisabledComponents].Value);
        Assert.Null(Assert.Single(applied.Changes).Before);
        Assert.Equal(WindowsOpStatus.AlreadyApplied, ApplyOne(fresh, op).Status);
        Assert.Equal(WindowsOpStatus.Reverted, new WindowsConfigurator(fresh, fresh, Catalog).Revert([applied]).Single().Status);
        Assert.False(fresh.Registry.ContainsKey(DisabledComponents));

        // A value a tweak tool left (0x10, tunnel interfaces off): bit 0x20 is added, 0x10 kept; Undo puts back exactly 16.
        var tweaked = new FakeWindows();
        tweaked.Registry[DisabledComponents] = (RegistryKind.DWord, "16");
        var onTop = ApplyOne(tweaked, op);
        Assert.Equal("48", tweaked.Registry[DisabledComponents].Value);
        Assert.Equal(("16", "48"), (Assert.Single(onTop.Changes).Before, onTop.Changes[0].After));
        new WindowsConfigurator(tweaked, tweaked, Catalog).Revert([onTop]);
        Assert.Equal("16", tweaked.Registry[DisabledComponents].Value);

        // Already set among other bits (0xFF): nothing to do, nothing written.
        var all = new FakeWindows();
        all.Registry[DisabledComponents] = (RegistryKind.DWord, "255");
        Assert.Equal(WindowsOpStatus.AlreadyApplied, ApplyOne(all, op).Status);
        Assert.Equal("255", all.Registry[DisabledComponents].Value);
    }

    [Fact]
    public void Prefer_ipv4_and_turn_ipv6_back_on_run_together_and_each_keeps_the_others_bit()
    {
        var prefer = Op("network.prefer-ipv4");
        var repair = Op("repair.ipv6-components");
        Assert.Empty(WindowsConfigurator.Conflicts([prefer, repair]));   // set 0x20 and clear every bit but 0x20: no bit in common (0.9 compares by bits)

        var pc = new FakeWindows();
        pc.Registry[DisabledComponents] = (RegistryKind.DWord, "17");    // 0x11: IPv6 parts off
        var outcomes = new WindowsConfigurator(pc, pc).Apply([prefer, repair], TestContext.Current.CancellationToken);

        Assert.DoesNotContain(outcomes, o => !o.Ok);
        Assert.Equal("32", pc.Registry[DisabledComponents].Value);       // IPv6 back on, IPv4 preferred
        var reverted = new WindowsConfigurator(pc, pc, Catalog).Revert(outcomes);
        Assert.DoesNotContain(reverted, r => r.Status is WindowsOpStatus.Failed or WindowsOpStatus.Blocked);
        Assert.Equal("17", pc.Registry[DisabledComponents].Value);       // exactly as it was
    }

    [Fact]
    public void Prefer_ipv4_conflicts_with_a_whole_value_that_clears_its_bit_and_is_refused_when_the_value_cannot_be_read()
    {
        var prefer = Op("network.prefer-ipv4");
        var zero = WindowsCatalog.Parse("""
            { "kind": "Mesura.WindowsCatalog", "schemaVersion": 4, "presets": [], "operations": [
              { "id": "test.ipv6-zero", "name": "Zero", "category": "Network", "description": "d", "risk": "systemConfiguration",
                "registry": [ { "hive": "HKLM", "path": "SYSTEM\\CurrentControlSet\\Services\\Tcpip6\\Parameters", "name": "DisabledComponents", "type": "dword", "value": "0" } ] } ] }
            """).Operations.Single();
        Assert.NotEmpty(WindowsConfigurator.Conflicts([prefer, zero]));

        var unread = new FakeWindows();
        unread.Unreadable.Add(DisabledComponents);
        Assert.NotEqual(WindowsOpStatus.Applied, ApplyOne(unread, prefer).Status);
        Assert.Empty(unread.Writes);
    }

    [Fact]
    public void Prefer_ipv4_says_ipv6_stays_on_the_restart_and_what_other_bits_mean()
    {
        var op = Op("network.prefer-ipv4");
        Assert.Contains("IPv6 stays on", op.Description, StringComparison.Ordinal);
        Assert.Contains("Every other bit", op.Description, StringComparison.Ordinal);
        Assert.Contains("Needs a restart", op.Limitations, StringComparison.Ordinal);
        Assert.Contains("Routing and Remote Access", op.Limitations, StringComparison.Ordinal);
    }

    // ---- retirements ------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_retired_operations_are_out_of_the_catalog_and_every_preset_and_their_declarations_are_kept()
    {
        foreach (var id in RetiredIds)
        {
            Assert.Null(Catalog.ById(id));
            Assert.Null(WindowsCatalog.Full.ById(id));
            Assert.DoesNotContain(Catalog.Presets, p => p.OperationIds.Contains(id) || p.OptionalIds.Contains(id));
            Assert.NotNull(WindowsCatalog.Retired.ById(id));
        }

        Assert.Equal(HighPerformance, WindowsCatalog.Retired.ById("power.high-performance")!.PowerPlan!.SchemeGuid);
        Assert.Equal(Ultimate, WindowsCatalog.Retired.ById("power.ultimate-performance")!.PowerPlan!.SchemeGuid);
        Assert.Equal("ms_tcpip6", Assert.Single(WindowsCatalog.Retired.ById("network.adapter-ipv6-binding-off")!.AdapterSettings).Key);
    }

    [Fact]
    public void An_older_setup_naming_the_retired_operations_opens_never_applies_them_and_names_them()
    {
        var json = """
            { "kind": "Mesura.SetupProfile", "schemaVersion": 1, "name": "saved with 0.8",
              "windows": [ "power.high-performance", "power.ultimate-performance", "network.adapter-ipv6-binding-off", "explorer.show-extensions" ] }
            """;

        var profile = SetupProfile.Parse(json, AppCatalog.Default, Catalog);

        Assert.Equal(RetiredIds, profile.RetiredWindows);
        Assert.Equal(["explorer.show-extensions"], profile.Windows);
        var resolution = profile.ResolveWindows(PlanningContext.Reading(Catalog, null, new FakeWindows()));
        Assert.Equal(["explorer.show-extensions"], resolution.Operations.Select(o => o.Id));
        Assert.DoesNotContain("power.high-performance", profile.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_retired_id_can_not_be_applied_by_any_path()
    {
        var context = PlanningContext.Reading(Catalog, null, new FakeWindows());
        foreach (var id in RetiredIds)
        {
            // The command line's --id and every other path resolve ids through the catalog: a retired one is an unknown id, refused before anything runs.
            Assert.Throws<ArgumentException>(() => LevelPlanning.Resolve([], [id], context, ExplicitOperations.AsNamed));
            Assert.Throws<ArgumentException>(() => LevelPlanning.Resolve([], [id], context, ExplicitOperations.LevelConditions));
        }
    }

    [Fact]
    public void A_history_record_of_a_retired_power_plan_still_undoes_to_the_previous_plan()
    {
        // Records as 0.8 wrote them, made with the 0.8 definitions (kept in the engine fixtures), undone by today's engine trusting today's catalog.
        var pc = new FakeWindows();
        pc.Schemes.Add(HighPerformance);
        var high = new WindowsConfigurator(pc, pc).Apply([EngineFixtures.Op("power.high-performance")], TestContext.Current.CancellationToken).Single();
        Assert.Equal(HighPerformance, pc.Active);

        Assert.Equal(WindowsOpStatus.Reverted, new WindowsConfigurator(pc, pc, Catalog).Revert([high]).Single().Status);
        Assert.Equal(Balanced, pc.Active);

        var ultimate = new WindowsConfigurator(pc, pc).Apply([EngineFixtures.Op("power.ultimate-performance")], TestContext.Current.CancellationToken).Single();
        Assert.Contains(Ultimate, pc.Schemes);
        Assert.Equal(WindowsOpStatus.Reverted, new WindowsConfigurator(pc, pc, Catalog).Revert([ultimate]).Single().Status);
        Assert.Equal(Balanced, pc.Active);
        Assert.DoesNotContain(Ultimate, pc.Schemes);   // the plan it installed is removed again
    }

    private const string WifiGuid = "{C1000000-0000-0000-0000-000000000001}";

    private static AdapterReading WifiWithIpv6() => new(WifiGuid, "Wi-Fi", "Intel(R) Wi-Fi 6 AX201 160MHz", "Intel", 0x8086, "23.80.0.7", "2026-07-29", AdapterKind.WiFi, true, true,
        new Dictionary<string, AdapterPropertyReading>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["ms_tcpip6"] = true, ["ms_lldp"] = true },
        true);

    [Fact]
    public void A_history_record_of_the_retired_ipv6_unbinding_still_undoes_and_a_forged_one_is_refused()
    {
        var pc = new FakeWindows { Adapters = [WifiWithIpv6()] };
        var unbinding = WindowsCatalog.Retired.ById("network.adapter-ipv6-binding-off")!;
        var record = new WindowsConfigurator(pc, pc).Apply([unbinding], TestContext.Current.CancellationToken).Single();
        Assert.False(pc.Adapters![0].Bindings["ms_tcpip6"]);

        Assert.Equal(WindowsOpStatus.Reverted, new WindowsConfigurator(pc, pc, Catalog).Revert([record]).Single().Status);
        Assert.True(pc.Adapters![0].Bindings["ms_tcpip6"]);

        // A record under the same id claiming another binding: the retired declaration names only ms_tcpip6, so nothing is undone.
        var forged = record with { Changes = [.. record.Changes.Select(c => c with { Name = "ms_lldp", Target = c.Target.Replace("ms_tcpip6", "ms_lldp", StringComparison.Ordinal) })] };
        Assert.Equal(WindowsOpStatus.Blocked, new WindowsConfigurator(pc, pc, Catalog).Revert([forged]).Single().Status);
    }
}
