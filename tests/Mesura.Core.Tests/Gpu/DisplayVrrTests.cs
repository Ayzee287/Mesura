using Mesura.Core.GpuControl;

namespace Mesura.Core.Tests.GpuVrr;

/// <summary>The owner's G1 policy as code: judged per display on what the driver says, never assumed; nothing forced; no frame limit ever suggested.</summary>
public sealed class DisplayVrrTests
{
    private static VrrDisplayFact Fact(string vendor = "NVIDIA", int index = 0, double? hz = 180, bool? possible = true, bool? enabled = false, string? problem = null) =>
        new(vendor, index, $"{vendor} monitor {index}", null, hz, possible, enabled, problem);

    [Fact]
    public void A_display_the_driver_says_cannot_do_variable_refresh_is_explained_not_forced_and_points_at_the_monitor_menu()
    {
        var verdict = Assert.Single(DisplayVrr.Assess([Fact(possible: false, enabled: false)]).Displays);

        Assert.Equal(VrrState.NotAvailable, verdict.State);
        Assert.Contains("monitor's own menu", verdict.Explanation, StringComparison.Ordinal);
        Assert.Contains("Adaptive-Sync", verdict.Action, StringComparison.Ordinal);
    }

    [Fact]
    public void A_display_where_it_is_possible_and_off_can_be_enabled_with_the_vendors_own_route_and_one_that_is_on_needs_nothing()
    {
        var assessment = DisplayVrr.Assess([Fact("AMD", index: 1, possible: true, enabled: false), Fact("NVIDIA", possible: true, enabled: false), Fact("NVIDIA", index: 2, possible: true, enabled: true)]);

        Assert.Equal(VrrState.CanEnable, assessment.Displays[0].State);
        Assert.Contains("--display 1", assessment.Displays[0].Action, StringComparison.Ordinal);
        Assert.Equal(VrrState.CanEnable, assessment.Displays[1].State);
        Assert.Contains("NVIDIA Control Panel", assessment.Displays[1].Action, StringComparison.Ordinal);
        Assert.Equal(VrrState.Active, assessment.Displays[2].State);
        Assert.Null(assessment.Displays[2].Action);
    }

    [Fact]
    public void An_unanswered_display_is_unknown_and_nothing_is_proposed_on_a_guess()
    {
        var verdict = Assert.Single(DisplayVrr.Assess([Fact(possible: null, enabled: null, problem: "VRR info: INVALID_ARGUMENT")]).Displays);

        Assert.Equal(VrrState.Unknown, verdict.State);
        Assert.Null(verdict.Action);
        Assert.Contains("INVALID_ARGUMENT", verdict.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Displays_at_different_refresh_rates_are_noted_and_each_is_judged_on_its_own()
    {
        var assessment = DisplayVrr.Assess([Fact(hz: 180, possible: true, enabled: true), Fact("AMD", 0, hz: 200, possible: true, enabled: false), Fact("AMD", 1, hz: 60, possible: false, enabled: null)]);

        Assert.True(assessment.MixedRefreshRates); // 180 and 200: a 60 Hz display that cannot do VRR does not count
        Assert.Equal([VrrState.Active, VrrState.CanEnable, VrrState.NotAvailable], assessment.Displays.Select(d => d.State));
        Assert.Contains("no frame-rate limit", assessment.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_in_a_verdict_ever_suggests_a_frame_rate_cap_enabling_variable_refresh_is_not_limiting_performance()
    {
        var assessment = DisplayVrr.Assess([Fact(possible: true, enabled: false), Fact("AMD", 1, possible: false, enabled: null), Fact(possible: null, enabled: null)]);

        foreach (var verdict in assessment.Displays)
        {
            Assert.DoesNotContain("cap", verdict.Action ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("frame limit", verdict.Action ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void No_displays_is_said_plainly()
    {
        var assessment = DisplayVrr.Assess([]);

        Assert.Empty(assessment.Displays);
        Assert.Contains("No display", assessment.Summary, StringComparison.Ordinal);
    }
}
