using Mesura.Core.Setup;
using Mesura.Core.Windows;

namespace Mesura.Core.Tests.Setup;

/// <summary>What a set value still waits for (a restart, a sign-out, an Explorer restart) is derived from the recorded runs and the time Windows started.</summary>
public sealed class PendingActivationTests
{
    private static readonly DateTimeOffset Booted = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);

    private static SetupRun Run(string finishedUtc, params WindowsOpOutcome[] steps) =>
        SetupRun.Create("r" + finishedUtc, "apply", null, finishedUtc, finishedUtc, [], [], steps);

    private static WindowsOpOutcome Step(string id, WindowsOpStatus status, Activation activation) => new(id, id, status, "", activation, []);

    [Fact]
    public void A_setting_applied_since_windows_started_waits_for_what_its_activation_says()
    {
        var pending = PendingActivation.For(
            [Run("2026-11-01T09:00:00Z", Step("a", WindowsOpStatus.Applied, Activation.Reboot), Step("b", WindowsOpStatus.Applied, Activation.SignOut), Step("c", WindowsOpStatus.Applied, Activation.ExplorerRestart), Step("d", WindowsOpStatus.Applied, Activation.Immediate))],
            Booted);

        Assert.Equal(Activation.Reboot, pending["a"]);
        Assert.Equal(Activation.SignOut, pending["b"]);
        Assert.Equal(Activation.ExplorerRestart, pending["c"]);
        Assert.DoesNotContain("d", pending.Keys); // it took effect at once
    }

    [Fact]
    public void A_restart_since_the_run_clears_the_wait()
    {
        var pending = PendingActivation.For([Run("2026-11-01T07:59:59Z", Step("a", WindowsOpStatus.Applied, Activation.Reboot))], Booted);

        Assert.Empty(pending);
    }

    [Fact]
    public void Only_a_change_that_was_made_counts_and_the_latest_run_decides()
    {
        var pending = PendingActivation.For(
            [
                Run("2026-11-01T11:00:00Z", Step("a", WindowsOpStatus.Applied, Activation.Immediate), Step("b", WindowsOpStatus.AlreadyApplied, Activation.Reboot), Step("c", WindowsOpStatus.Failed, Activation.Reboot)),
                Run("2026-11-01T09:00:00Z", Step("a", WindowsOpStatus.Applied, Activation.Reboot)), // older, listed second: order of the list doesn't matter
            ],
            Booted);

        Assert.DoesNotContain("a", pending.Keys); // the later run changed it again and it took effect at once
        Assert.DoesNotContain("b", pending.Keys); // nothing was changed by that run
        Assert.DoesNotContain("c", pending.Keys); // a failure waits for nothing
    }

    [Fact]
    public void An_undo_that_takes_a_restart_waits_for_it_too_and_an_unreadable_time_counts_for_nothing()
    {
        var pending = PendingActivation.For(
            [Run("2026-11-01T10:00:00Z", Step("a", WindowsOpStatus.Reverted, Activation.Reboot)), Run("not a time", Step("b", WindowsOpStatus.Applied, Activation.Reboot))],
            Booted);

        Assert.Equal(Activation.Reboot, pending["a"]);
        Assert.DoesNotContain("b", pending.Keys);
    }
}
