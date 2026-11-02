using Mesura.Core.Windows;

namespace Mesura.Core.Tests.Windows;

/// <summary>
/// An operation whose registry value Windows may store and ignore (hardware-accelerated GPU scheduling on a card that can't use it,
/// the NTFS last-access setting under system management) is "set" only when Windows itself says it is in effect (0.5).
/// </summary>
public sealed class EffectCheckTests
{
    private const string Key = @"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\HwSchMode";

    private static WindowsOperation Hags => WindowsCatalog.Default.ById("gpu.hardware-scheduling-on")!;

    private static FakeWindows Pc(string? effect, string? stored)
    {
        var pc = new FakeWindows { Effect = effect };
        if (stored is not null)
        {
            pc.Registry[Key] = (RegistryKind.DWord, stored);
        }

        return pc;
    }

    [Fact]
    public void The_session_checks_for_the_hotkeys_and_pointer_acceleration_wait_for_a_sign_out_not_a_restart()
    {
        var hotkeys = WindowsCatalog.Default.ById("input.accessibility-shortcuts-off")!;
        var pointer = WindowsCatalog.Default.ById("input.pointer-precision-off")!;
        Assert.Equal(new EffectCheck("accessibilityHotkeys", "disabled"), hotkeys.Effect);
        Assert.Equal(new EffectCheck("pointerAcceleration", "disabled"), pointer.Effect);

        var pc = new FakeWindows { Effect = "enabled" };
        foreach (var setting in pointer.Registry)
        {
            pc.Registry[$@"{setting.Hive}\{setting.Path}\{setting.Name}"] = (RegistryKind.String, "0");
        }

        var evaluation = WindowsEvaluator.Evaluate(pointer, pc);

        Assert.Equal(OperationState.PendingRestart, evaluation.State); // stored; the session still accelerates
        Assert.Contains("sign out", evaluation.Reason, StringComparison.Ordinal);
        var outcome = new WindowsConfigurator(pc, pc).Apply([pointer], TestContext.Current.CancellationToken).Single();
        Assert.Equal(Activation.SignOut, outcome.Activation); // not a restart
        pc.Effect = "disabled";
        Assert.Equal(OperationState.Applied, WindowsEvaluator.Evaluate(pointer, pc).State);
    }

    [Fact]
    public void Both_confirmed_operations_carry_the_check_the_audit_asked_for()
    {
        Assert.Equal(new EffectCheck("gpuScheduling", "enabled"), Hags.Effect);
        Assert.Equal(new EffectCheck("ntfsLastAccess", "disabled"), WindowsCatalog.Default.ById("system.ntfs-last-access-off")!.Effect);
    }

    [Fact]
    public void A_card_that_cannot_use_it_is_not_for_this_pc_and_nothing_is_written()
    {
        var pc = Pc("unsupported", stored: null);

        Assert.Equal(OperationState.Inapplicable, WindowsEvaluator.Evaluate(Hags, pc).State);
        var outcome = new WindowsConfigurator(pc, pc).Apply([Hags], TestContext.Current.CancellationToken).Single();

        Assert.Equal(WindowsOpStatus.Inapplicable, outcome.Status);
        Assert.Empty(pc.Writes);
    }

    [Fact]
    public void A_stored_value_that_windows_does_not_report_in_effect_is_not_shown_as_applied()
    {
        var pc = Pc("disabled", stored: "2");

        var evaluation = WindowsEvaluator.Evaluate(Hags, pc);

        Assert.Equal(OperationState.PendingRestart, evaluation.State);
        var outcome = new WindowsConfigurator(pc, pc).Apply([Hags], TestContext.Current.CancellationToken).Single();
        Assert.Empty(outcome.Changes);
        Assert.Equal(Activation.Reboot, outcome.Activation); // told to restart; nothing was written and nothing is claimed
        Assert.Empty(pc.Writes);
    }

    [Fact]
    public void Applied_needs_the_value_and_the_report_to_agree_and_an_unreadable_report_is_unknown()
    {
        Assert.Equal(OperationState.Applied, WindowsEvaluator.Evaluate(Hags, Pc("enabled", "2")).State);
        Assert.Equal(OperationState.Unknown, WindowsEvaluator.Evaluate(Hags, Pc(null, "2")).State);
        Assert.Equal(OperationState.NotApplied, WindowsEvaluator.Evaluate(Hags, Pc("disabled", "1")).State);
    }

    [Fact]
    public void A_fresh_write_is_reported_as_applied_with_a_restart_not_as_confirmed()
    {
        var pc = Pc("disabled", stored: null);

        var outcome = new WindowsConfigurator(pc, pc).Apply([Hags], TestContext.Current.CancellationToken).Single();

        Assert.Equal(WindowsOpStatus.Applied, outcome.Status);
        Assert.Equal(Activation.Reboot, outcome.Activation);
        Assert.Contains("restart", outcome.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(OperationState.PendingRestart, WindowsEvaluator.Evaluate(Hags, pc).State); // and still not "set" until Windows says so
    }

    [Theory]
    [InlineData("{ \"kind\": \"nothing\", \"expect\": \"enabled\" }")]
    [InlineData("{ \"kind\": \"gpuScheduling\", \"expect\": \"disabled\" }")]
    public void A_catalog_can_only_name_the_readings_mesura_knows(string effect)
    {
        var json = "{ \"kind\": \"Mesura.WindowsCatalog\", \"schemaVersion\": 5, \"operations\": [ { \"id\": \"a\", \"name\": \"A\", \"description\": \"d\", \"risk\": \"systemConfiguration\", "
            + "\"registry\": [ { \"hive\": \"HKLM\", \"path\": \"X\", \"name\": \"y\", \"type\": \"dword\", \"value\": \"1\" } ], \"effect\": " + effect + " } ] }";

        Assert.Throws<FormatException>(() => WindowsCatalog.Parse(json));
    }
}
