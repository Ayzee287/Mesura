using Mesura.Core.Windows;

namespace Mesura.Core.Tests.Windows;

/// <summary>
/// The AMD Anti-Lag operation (0.6; the owner's decision of 2026-11-04 put it in Maximum performance): which GPUs it is for, what it refuses, how it is recorded, verified and undone through the same
/// engine as every other operation. The driver is a stand-in (<c>FakeWindows.Gpu</c>); the real writer and reader were proven on a real RX 9070 XT separately (docs/hardware-test-plan-amd-radeon.md).
/// </summary>
public sealed class GpuOperationTests
{
    private const string Id = "gpu.amd-anti-lag-on";

    private static WindowsOperation Op => WindowsCatalog.Default.ById(Id) ?? throw new InvalidOperationException(Id);

    private static GpuFeatureReading Radeon(bool enabled, int? level = 0, bool? supported = true) =>
        new(GpuAvailability.Available, "AMD Radeon RX 9070 XT", 0, supported, enabled, level, null);

    private static FakeWindows Pc(GpuFeatureReading? gpu) => new() { Gpu = gpu };

    private static WindowsOpOutcome Apply(FakeWindows pc) => new WindowsConfigurator(pc, pc).Apply([Op], TestContext.Current.CancellationToken).Single();

    [Fact]
    public void The_operation_names_one_switch_the_original_anti_lag_and_only_Maximum_performance_applies_it()
    {
        var setting = Assert.Single(Op.GpuSettings);
        Assert.Equal(new GpuSetting("amd", "antilag", true), setting);
        Assert.True(Op.NeedsElevation);
        Assert.Empty(Op.AdapterSettings);

        Assert.Contains(Id, WindowsCatalog.Default.PresetById("maximum-performance")!.OperationIds);
        Assert.DoesNotContain(Id, WindowsCatalog.Default.PresetById("maximum-performance")!.OptionalIds);
        Assert.All(WindowsCatalog.Default.Presets.Where(p => p.Id != "maximum-performance"), p =>
        {
            Assert.DoesNotContain(Id, p.OperationIds);
            Assert.DoesNotContain(Id, p.OptionalIds);
        });
    }

    [Fact]
    public void The_catalog_refuses_a_gpu_switch_it_does_not_write_and_a_gpu_operation_that_does_other_things()
    {
        string Operation(string extra) => $$"""
            { "kind": "Mesura.WindowsCatalog", "schemaVersion": 5, "operations": [ { "id": "a.b", "name": "A", "description": "D", "risk": "systemConfiguration", {{extra}} } ] }
            """;

        var anti = WindowsCatalog.Parse(Operation("""
            "gpu": [ { "vendor": "amd", "feature": "antilag", "enabled": true } ]
            """));
        Assert.Single(anti.ById("a.b")!.GpuSettings);

        Assert.Contains("isn't one Mesura writes", Assert.Throws<FormatException>(() => WindowsCatalog.Parse(Operation("""
            "gpu": [ { "vendor": "amd", "feature": "antilagplus", "enabled": true } ]
            """))).Message, StringComparison.Ordinal);
        Assert.Contains("isn't one Mesura writes", Assert.Throws<FormatException>(() => WindowsCatalog.Parse(Operation("""
            "gpu": [ { "vendor": "nvidia", "feature": "antilag", "enabled": true } ]
            """))).Message, StringComparison.Ordinal);
        Assert.Contains("only GPU switches", Assert.Throws<FormatException>(() => WindowsCatalog.Parse(Operation("""
            "gpu": [ { "vendor": "amd", "feature": "antilag", "enabled": true } ],
            "registry": [ { "hive": "HKCU", "path": "Software\\X", "name": "V", "type": "dword", "value": "1" } ]
            """))).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, 0, OperationState.NotApplied)]
    [InlineData(true, 0, OperationState.Applied)]
    [InlineData(false, 1, OperationState.Blocked)]   // Anti-Lag Next is selected: never changed
    [InlineData(true, 1, OperationState.Blocked)]    // on, but not the original: not "applied" either
    [InlineData(false, 2, OperationState.Blocked)]   // an unknown level is not guessed
    [InlineData(false, null, OperationState.Unknown)]
    public void The_state_follows_the_driver_and_only_the_original_level_counts(bool enabled, int? level, OperationState expected)
    {
        Assert.Equal(expected, WindowsEvaluator.Evaluate(Op, Pc(Radeon(enabled, level))).State);
    }

    [Fact]
    public void A_pc_without_a_radeon_or_a_card_that_does_not_offer_it_is_not_for_this_pc_and_an_unreadable_driver_is_unknown()
    {
        Assert.Equal(OperationState.Inapplicable, WindowsEvaluator.Evaluate(Op, Pc(GpuFeatureReading.None(GpuAvailability.NoAdapter))).State);
        Assert.Equal(OperationState.Inapplicable, WindowsEvaluator.Evaluate(Op, Pc(GpuFeatureReading.None(GpuAvailability.NoDriver))).State);
        Assert.Equal(OperationState.Inapplicable, WindowsEvaluator.Evaluate(Op, Pc(Radeon(false, supported: false))).State);
        Assert.Equal(OperationState.Unknown, WindowsEvaluator.Evaluate(Op, Pc(GpuFeatureReading.None(GpuAvailability.Unreadable))).State);
        Assert.Equal(OperationState.Unknown, WindowsEvaluator.Evaluate(Op, Pc(null)).State);
        Assert.Equal(OperationState.Unknown, WindowsEvaluator.Evaluate(Op, Pc(Radeon(false) with { Supported = null })).State);
        var none = WindowsEvaluator.Evaluate(Op, Pc(GpuFeatureReading.None(GpuAvailability.NoAdapter)));
        Assert.Contains("no AMD graphics adapter", none.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Applying_it_writes_the_switch_records_the_writers_record_for_the_undo_and_the_undo_puts_it_back()
    {
        var pc = Pc(Radeon(false));

        var outcome = Apply(pc);

        Assert.Equal(WindowsOpStatus.Applied, outcome.Status);
        Assert.Equal(["gpu amd antilag #0=True"], pc.Writes);
        var change = Assert.Single(outcome.Changes);
        Assert.Equal(ChangeKind.GpuFeature, change.Kind);
        Assert.Equal("amd antilag", change.Name);
        Assert.Equal("20261104T000000000Z-gpu-antilag", change.Path);   // the GPU writer's own record
        Assert.Equal("off", change.Before);
        Assert.Equal("on", change.After);
        Assert.Equal(OperationState.Applied, WindowsEvaluator.Evaluate(Op, pc).State);

        var reverted = new WindowsConfigurator(pc, pc).Revert([outcome]).Single();

        Assert.Equal(WindowsOpStatus.Reverted, reverted.Status);
        Assert.Equal("gpu undo 20261104T000000000Z-gpu-antilag", pc.Writes[^1]);
        Assert.False(pc.Gpu!.Enabled);
    }

    [Fact]
    public void An_operation_already_in_place_writes_nothing()
    {
        var pc = Pc(Radeon(true));

        var outcome = Apply(pc);

        Assert.Equal(WindowsOpStatus.AlreadyApplied, outcome.Status);
        Assert.Empty(pc.Writes);
    }

    [Fact]
    public void Anti_Lag_Next_or_an_unreadable_level_is_never_written_and_nothing_is_recorded()
    {
        foreach (var gpu in new[] { Radeon(false, level: 1), Radeon(false, level: null) })
        {
            var pc = Pc(gpu);

            var outcome = Apply(pc);

            Assert.NotEqual(WindowsOpStatus.Applied, outcome.Status);
            Assert.Empty(pc.Writes);
            Assert.Empty(outcome.Changes);
        }
    }

    [Fact]
    public void A_driver_that_answers_success_and_changes_nothing_fails_the_operation_and_the_writers_record_is_undone()
    {
        var pc = Pc(Radeon(false));
        pc.GpuIgnoresWrites = true;

        var outcome = Apply(pc);

        Assert.Equal(WindowsOpStatus.Failed, outcome.Status);
        Assert.Contains("doesn't report", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("gpu undo 20261104T000000000Z-gpu-antilag", pc.Writes);   // what the writer changed (Chill) goes back too
        Assert.False(pc.Gpu!.Enabled);
    }

    [Fact]
    public void A_writer_that_refuses_fails_the_operation_with_the_drivers_words_and_leaves_no_change()
    {
        var pc = Pc(Radeon(false));
        pc.GpuWriterRefuses = true;

        var outcome = Apply(pc);

        Assert.Equal(WindowsOpStatus.Failed, outcome.Status);
        Assert.Contains("refused", outcome.Detail, StringComparison.Ordinal);
        Assert.Empty(outcome.Changes);
        Assert.False(pc.Gpu!.Enabled);
    }

    [Fact]
    public void A_record_for_another_switch_is_not_trusted_for_this_operation()
    {
        var change = new RecordedChange(ChangeKind.GpuFeature, "x", null, "id", "amd freesync", null, "off", "on");
        Assert.False(Op.Declares(change));
        Assert.True(Op.Declares(change with { Name = "amd antilag" }));
    }
}
