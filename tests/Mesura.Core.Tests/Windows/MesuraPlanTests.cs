using Mesura.Core.Windows;

namespace Mesura.Core.Tests.Windows;

/// <summary>
/// The two Mesura power plans (0.5): copies of Balanced under their own GUIDs, written on mains power only, with nothing changed on battery
/// and no built-in scheme touched. These pin the behaviour of the mechanism and the shape of both plans, not the catalog's wording.
/// </summary>
public sealed class MesuraPlanTests
{
    private const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private const string BalancedMesura = "b71ddbcf-9d21-46ea-9def-6f2a2378e65a";
    private const string MaximumMesura = "76d36892-93d0-4c33-9e09-c2e1fb87cf49";
    private const string UsbSuspend = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";
    private const string Aspm = "ee12f906-d277-404b-b6da-e5fa1a576df5";
    private const string DiskIdle = "6738e2c4-e8a5-4a42-b16a-e040e769756e";
    private const string MinState = "893dee8e-2bef-41e0-89c6-b55d0929964c";
    private const string MinStateClass1 = "893dee8e-2bef-41e0-89c6-b55d0929964d";
    private const string Epp = "36687f9e-e3a5-4dbf-b1dc-15eb381c6863";

    private static WindowsOperation BalancedOp => EngineFixtures.Op("power.mesura-balanced-plan");

    private static WindowsOperation MaximumOp => EngineFixtures.Op("power.mesura-maximum-plan");

    /// <summary>A PC whose Balanced has every setting of both plans registered, each at a value the plans do not want (so every one has to change).</summary>
    private static FakeWindows Pc(bool battery = false, bool hybrid = false)
    {
        var pc = new FakeWindows { HasBattery = battery, ModernStandby = false, HybridCpu = hybrid };
        foreach (var value in BalancedOp.PowerPlan!.PlanValues.Concat(MaximumOp.PowerPlan!.PlanValues))
        {
            var seed = value.Ac == 0 ? 7u : 0u;
            pc.PowerSettings[$"{Balanced}|{value.Setting}"] = seed;
            pc.PowerSettingsDc[$"{Balanced}|{value.Setting}"] = 42; // the battery side, which no plan may change
        }

        return pc;
    }

    [Fact]
    public void Both_plans_are_copies_of_balanced_with_their_own_guids_and_change_nothing_on_battery()
    {
        foreach (var (op, guid, name) in new[] { (BalancedOp, BalancedMesura, "Balanced Mesura"), (MaximumOp, MaximumMesura, "Maximum Performance Mesura") })
        {
            var plan = op.PowerPlan!;
            Assert.True(plan.IsMesuraPlan);
            Assert.Equal(Balanced, plan.BasedOn);
            Assert.Equal(guid, plan.SchemeGuid);
            Assert.Equal(name, plan.Name);
            Assert.All(plan.PlanValues, v => Assert.Null(v.Dc)); // the battery side is Balanced's, so a laptop away from its charger behaves as with Balanced
            Assert.Equal(plan.PlanValues.Count, plan.PlanValues.Select(v => v.Setting).Distinct().Count());
            Assert.True(op.NeedsElevation);
        }

        Assert.NotEqual(BalancedMesura, MaximumMesura);
    }

    [Fact]
    public void Maximum_performance_asks_for_speed_where_the_processor_honours_it_and_balanced_mesura_only_lifts_the_brakes()
    {
        var max = MaximumOp.PowerPlan!.PlanValues.ToDictionary(v => v.Setting, v => v);
        Assert.Equal(100u, max[MinState].Ac);
        Assert.Equal(0u, max[Epp].Ac);
        Assert.Equal(0u, max[Aspm].Ac);
        Assert.Equal(0u, max[UsbSuspend].Ac);
        Assert.Equal(0u, max[DiskIdle].Ac);
        Assert.True(max.Count >= 25);
        // The efficiency-core values only exist for a processor that has efficiency cores.
        Assert.Equal([DeviceRequirement.HybridCpu], max[MinStateClass1].Needs);

        var balanced = BalancedOp.PowerPlan!.PlanValues.ToDictionary(v => v.Setting, v => v);
        Assert.Equal(5, balanced.Count);
        Assert.DoesNotContain(MinState, balanced.Keys); // no pinned processor in the everyday plan
        Assert.DoesNotContain(Epp, balanced.Keys);
        Assert.Equal(1200u, balanced[DiskIdle].Ac);
        Assert.Equal([DeviceRequirement.NoBattery], balanced[Aspm].Needs);
    }

    [Fact]
    public void The_preview_before_the_plan_exists_reads_the_values_it_would_copy_and_says_not_applied()
    {
        var evaluation = WindowsEvaluator.Evaluate(MaximumOp, Pc());

        Assert.Equal(OperationState.NotApplied, evaluation.State);
        Assert.Contains(evaluation.Items, i => i.Target.Contains("Minimum processor state") && i.Current == "0" && i.Desired == "100" && !i.Matches);
        Assert.Contains(evaluation.Items, i => i.Target.StartsWith("power scheme Maximum Performance Mesura") && i.Current == "not installed");
    }

    [Fact]
    public void Applying_makes_the_copy_writes_the_mains_values_activates_it_and_leaves_balanced_and_the_battery_side_alone()
    {
        var pc = Pc();

        var outcome = new WindowsConfigurator(pc, pc).Apply([MaximumOp], TestContext.Current.CancellationToken).Single();

        Assert.Equal(WindowsOpStatus.Applied, outcome.Status);
        Assert.Contains(MaximumMesura, pc.Schemes);
        Assert.Equal(MaximumMesura, pc.Active);
        Assert.Equal(100u, pc.PowerSettings[$"{MaximumMesura}|{MinState}"]);
        Assert.Equal(0u, pc.PowerSettings[$"{MaximumMesura}|{Epp}"]);
        Assert.Equal(0u, pc.PowerSettings[$"{MaximumMesura}|{UsbSuspend}"]);
        Assert.Equal(7u, pc.PowerSettings[$"{Balanced}|{UsbSuspend}"]); // Balanced untouched
        Assert.Equal(42u, pc.PowerSettingsDc[$"{MaximumMesura}|{MinState}"]); // and the copy's battery side is Balanced's
        Assert.DoesNotContain(pc.Writes, w => w.Contains(" dc ", StringComparison.Ordinal));
        Assert.DoesNotContain(pc.Writes, w => w.StartsWith("delete scheme", StringComparison.Ordinal));
        Assert.Equal(OperationState.Applied, WindowsEvaluator.Evaluate(MaximumOp, pc).State);
        Assert.Equal(WindowsOpStatus.AlreadyApplied, new WindowsConfigurator(pc, pc).Apply([MaximumOp], TestContext.Current.CancellationToken).Single().Status);
    }

    [Fact]
    public void Efficiency_core_values_are_written_only_on_a_hybrid_processor()
    {
        var plain = Pc(hybrid: false);
        new WindowsConfigurator(plain, plain).Apply([MaximumOp], TestContext.Current.CancellationToken);
        Assert.DoesNotContain(plain.Writes, w => w.Contains(MinStateClass1, StringComparison.Ordinal));
        Assert.Equal(OperationState.Applied, WindowsEvaluator.Evaluate(MaximumOp, plain).State);

        var hybrid = Pc(hybrid: true);
        new WindowsConfigurator(hybrid, hybrid).Apply([MaximumOp], TestContext.Current.CancellationToken);
        Assert.Equal(100u, hybrid.PowerSettings[$"{MaximumMesura}|{MinStateClass1}"]);

        // A processor type that can't be read leaves those values out; it doesn't stop the plan.
        var unknown = Pc();
        unknown.HybridCpu = null;
        Assert.Equal(WindowsOpStatus.Applied, new WindowsConfigurator(unknown, unknown).Apply([MaximumOp], TestContext.Current.CancellationToken).Single().Status);
        Assert.DoesNotContain(unknown.Writes, w => w.Contains(MinStateClass1, StringComparison.Ordinal));
    }

    [Fact]
    public void On_a_laptop_the_link_state_value_of_balanced_mesura_is_left_out()
    {
        var pc = Pc(battery: true);

        var outcome = new WindowsConfigurator(pc, pc).Apply([BalancedOp], TestContext.Current.CancellationToken).Single();

        Assert.Equal(WindowsOpStatus.Applied, outcome.Status);
        Assert.Equal(BalancedMesura, pc.Active);
        Assert.Equal(pc.PowerSettings[$"{Balanced}|{Aspm}"], pc.PowerSettings[$"{BalancedMesura}|{Aspm}"]); // Balanced's, not the plan's
        Assert.Equal(1200u, pc.PowerSettings[$"{BalancedMesura}|{DiskIdle}"]);
        Assert.Equal(OperationState.Applied, WindowsEvaluator.Evaluate(BalancedOp, pc).State);
    }

    [Fact]
    public void A_setting_this_pc_does_not_register_is_not_written()
    {
        var pc = Pc();
        pc.PowerSettings.Remove($"{Balanced}|{Epp}");

        new WindowsConfigurator(pc, pc).Apply([MaximumOp], TestContext.Current.CancellationToken);

        Assert.DoesNotContain(pc.Writes, w => w.Contains(Epp, StringComparison.Ordinal));
        Assert.Equal(MaximumMesura, pc.Active);
    }

    [Fact]
    public void Undo_activates_the_previous_plan_again_and_deletes_the_copy()
    {
        var pc = Pc();
        var configurator = new WindowsConfigurator(pc, pc, WindowsCatalog.Default);
        var applied = configurator.Apply([MaximumOp], TestContext.Current.CancellationToken);

        var reverted = configurator.Revert(applied).Single();

        Assert.Equal(WindowsOpStatus.Reverted, reverted.Status);
        Assert.Equal(Balanced, pc.Active);
        Assert.DoesNotContain(MaximumMesura, pc.Schemes);
        Assert.Contains(Balanced, pc.Schemes);
        Assert.Equal(7u, pc.PowerSettings[$"{Balanced}|{UsbSuspend}"]);
    }

    [Fact]
    public void A_failure_part_way_puts_everything_back()
    {
        var pc = Pc();
        pc.FailSchemeValue = DiskIdle;

        var outcome = new WindowsConfigurator(pc, pc).Apply([MaximumOp], TestContext.Current.CancellationToken).Single();

        Assert.Equal(WindowsOpStatus.Failed, outcome.Status);
        Assert.Equal(Balanced, pc.Active);
        Assert.DoesNotContain(MaximumMesura, pc.Schemes);
    }

    [Fact]
    public void A_plan_that_was_edited_in_windows_is_corrected_with_a_before_image_and_the_edit_comes_back_on_undo()
    {
        var pc = Pc();
        var configurator = new WindowsConfigurator(pc, pc, WindowsCatalog.Default);
        configurator.Apply([BalancedOp], TestContext.Current.CancellationToken);
        pc.PowerSettings[$"{BalancedMesura}|{DiskIdle}"] = 600; // the user set ten minutes in Windows
        Assert.Equal(OperationState.Partial, WindowsEvaluator.Evaluate(BalancedOp, pc).State);

        var again = configurator.Apply([BalancedOp], TestContext.Current.CancellationToken);
        var outcome = again.Single();

        Assert.Equal(WindowsOpStatus.Applied, outcome.Status);
        Assert.Equal(1200u, pc.PowerSettings[$"{BalancedMesura}|{DiskIdle}"]);
        var change = Assert.Single(outcome.Changes);
        Assert.Equal("600", change.Before);
        Assert.Equal(BalancedMesura, change.Scheme);

        configurator.Revert(again);
        Assert.Equal(600u, pc.PowerSettings[$"{BalancedMesura}|{DiskIdle}"]);
        Assert.Contains(BalancedMesura, pc.Schemes); // the first run's copy is still there to undo
    }

    [Fact]
    public void The_two_plans_cannot_be_applied_together_and_an_undo_record_is_trusted_only_for_a_plans_own_scheme_and_settings()
    {
        Assert.Single(WindowsConfigurator.Conflicts([BalancedOp, MaximumOp]));

        var sub = "2a737441-1930-4402-8d77-b2bebba308a3";
        Assert.True(MaximumOp.Declares(new RecordedChange(ChangeKind.PowerSetting, "x", null, sub, UsbSuspend, null, "1", "0", Scheme: MaximumMesura)));
        Assert.False(MaximumOp.Declares(new RecordedChange(ChangeKind.PowerSetting, "x", null, sub, UsbSuspend, null, "1", "0", Scheme: Balanced)));
        Assert.False(MaximumOp.Declares(new RecordedChange(ChangeKind.PowerSetting, "x", null, sub, UsbSuspend, null, "1", "0", Scheme: BalancedMesura)));
        Assert.False(BalancedOp.Declares(new RecordedChange(ChangeKind.PowerSetting, "x", null, "54533251-82be-4824-96c1-47b60b740d00", MinState, null, "5", "100", Scheme: BalancedMesura)));
        Assert.True(BalancedOp.Declares(new RecordedChange(ChangeKind.PowerSchemeCreated, "x", null, null, null, null, null, BalancedMesura)));
    }

    [Theory]
    [InlineData("\"basedOn\": \"8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c\"", "copied from the Balanced scheme")]
    [InlineData("\"scheme\": \"381b4222-f694-41f0-9685-ff5bb260df2e\"", "needs a GUID of its own")]
    [InlineData("\"name\": \"Evil; calc.exe\"", "plain name")]
    public void A_catalog_cannot_point_a_plan_at_another_base_a_built_in_guid_or_a_command(string replacement, string expected)
    {
        var field = replacement.Split(':')[0];
        var plan = """{ "scheme": "14d03a10-c875-45f4-afff-4dfa8218c74a", "name": "P", "basedOn": "381b4222-f694-41f0-9685-ff5bb260df2e", "settings": [ { "subgroup": "2a737441-1930-4402-8d77-b2bebba308a3", "setting": "48e6b7a6-50f5-4782-a5d4-53bb8f07e226", "name": "U", "ac": 0 } ] }""";
        var start = plan.IndexOf(field, StringComparison.Ordinal);
        var end = plan.IndexOf(',', start);
        var edited = plan[..start] + replacement + plan[end..];
        var json = "{ \"kind\": \"Mesura.WindowsCatalog\", \"schemaVersion\": 5, \"operations\": [ { \"id\": \"a\", \"name\": \"A\", \"description\": \"d\", \"risk\": \"systemConfiguration\", \"powerPlan\": " + edited + " } ] }";

        var e = Assert.Throws<FormatException>(() => WindowsCatalog.Parse(json));
        Assert.Contains(expected, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_three_network_adapter_entries_are_out_of_the_catalog_but_their_old_records_still_undo()
    {
        foreach (var id in new[] { "network.interrupt-moderation-off", "network.ethernet-eee-off", "network.nic-selective-suspend-off" })
        {
            Assert.Null(WindowsCatalog.Default.ById(id));
            Assert.DoesNotContain(WindowsCatalog.Default.Presets, p => p.OperationIds.Contains(id) || p.OptionalIds.Contains(id));
            var retired = WindowsCatalog.Retired.ById(id)!;
            var each = Assert.Single(retired.RegistryEachSettings);
            var change = new RecordedChange(ChangeKind.Registry, "x", "HKLM", @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\0003", each.Name, RegistryKind.String, "1", "0", RegistryKind.String);
            Assert.True(retired.Declares(change), id);
        }
    }
}
