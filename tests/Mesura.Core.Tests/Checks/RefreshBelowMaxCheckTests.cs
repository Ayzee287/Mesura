using System.Numerics;
using Mesura.Core.Checks;
using Mesura.Core.Checks.Serialization;
using Mesura.Core.Facts;
using Mesura.Core.Raw;
using static Mesura.Core.Tests.Checks.FactsBuilder;

namespace Mesura.Core.Tests.Checks;

/// <summary>
/// Check 1 on built facts (architecture §6.4's cases, §6.5, §4.6). Every Problem and Ok is Likely under the product's
/// gate; a test gate shows what the rule itself allows once an owner-labelled fixture covers the condition.
/// </summary>
public sealed class RefreshBelowMaxCheckTests
{
    private static readonly RefreshBelowMaxCheck Check = new();

    [Fact]
    public void Sixty_of_a_highest_offered_165_is_a_problem_held_at_likely_by_the_gate()
    {
        var result = Single(Desktop().WithDisplays(Display(signalHz: 60)));

        Assert.Equal((Verdict.Problem, Certainty.Likely), (result.Verdict, result.Certainty));
        Assert.Equal("problem/external", result.Condition);
        Assert.Equal(["local-session", "external-display", "refresh-vs-highest-offered", "unvalidated-condition"], result.Trace.Select(step => step.Code));
        Assert.Equal(TraceKind.Gate, result.Trace[^1].Kind);
        Assert.Equal(CheckValue.Of(new RefreshRate(60, 1)), Param(result, "current"));
        Assert.Equal(CheckValue.Of(165), Param(result, "highestOffered"));
        Assert.Equal(CheckValue.Of(new PixelSize(2560, 1440)), Param(result, "resolution"));
        Assert.Equal(CheckValue.Of(ConnectionKind.DisplayPort), Param(result, "connection"));
        Assert.Equal(CheckValue.Of(1), Param(result, "tolerancePercent"));
    }

    [Fact]
    public void The_same_problem_is_confirmed_only_where_a_labelled_fixture_unlocks_its_condition()
    {
        var facts = Desktop().WithDisplays(Display(signalHz: 60));

        var unlocked = Single(facts, TestGates.Only(CheckIds.RefreshBelowMax, "problem/external"));
        var otherCheck = Single(facts, TestGates.Only(CheckIds.OnIntegratedGpu, "problem/external"));

        Assert.Equal(Certainty.Confirmed, unlocked.Certainty);
        Assert.DoesNotContain(unlocked.Trace, step => step.Kind is TraceKind.Gate or TraceKind.Downgrade);
        Assert.Equal(Certainty.Likely, otherCheck.Certainty);
    }

    [Fact]
    public void The_highest_offered_is_ok_and_likely_until_its_condition_is_unlocked()
    {
        var facts = Desktop();

        Assert.Equal((Verdict.Ok, Certainty.Likely, "ok/external"), Outcome(Single(facts)));
        Assert.Equal((Verdict.Ok, Certainty.Confirmed, "ok/external"), Outcome(Single(facts, TestGates.All)));
    }

    [Theory]
    [InlineData(60000u, 1001u, new[] { 60 })] // 59.94 against 60: within tolerance
    [InlineData(60000u, 1001u, new[] { 59 })] // 59.94 listed as 59: not a conflict, not a problem
    [InlineData(60000u, 1001u, new[] { 59, 60 })]
    [InlineData(24000u, 1001u, new[] { 23, 24 })]
    [InlineData(100u, 1u, new[] { 101 })] // exactly at the threshold: 101 = 100 × 1.01
    [InlineData(1605u, 10u, new[] { 60, 144, 160 })] // 160.5 Hz: half a hertz above the highest is not a conflict
    public void Rates_within_the_tolerance_or_a_listing_truncation_are_ok(uint numerator, uint denominator, int[] offered)
    {
        var result = Single(Desktop().WithDisplays(Display() with
        {
            SignalRefresh = Known(new RefreshRate(numerator, denominator)),
            PathRefresh = Known(new RefreshRate(numerator, denominator)),
            OfferedModes = List([.. offered.Select(hz => Mode(hz))]),
        }));

        Assert.Equal(Verdict.Ok, result.Verdict);
    }

    [Theory]
    [InlineData(99u, 1u, 100)] // exactly 1% below the highest: the tolerance is 1% of the current rate (99.99 < 100)
    [InlineData(99999u, 1000u, 101)] // just beyond the threshold
    [InlineData(23000u, 1000u, 24)] // a true 23 Hz against 24 is 4% below (23.976 Hz against 24 is not: see the Ok cases)
    public void Rates_beyond_the_tolerance_are_a_problem(uint numerator, uint denominator, int highest)
    {
        var result = Single(Desktop().WithDisplays(Display() with
        {
            SignalRefresh = Known(new RefreshRate(numerator, denominator)),
            PathRefresh = Known(new RefreshRate(numerator, denominator)),
            OfferedModes = List(Mode(highest)),
        }));

        Assert.Equal(Verdict.Problem, result.Verdict);
    }

    /// <summary>The rule is exact over the whole range the facts allow; 64-bit arithmetic would wrap here (§R7).</summary>
    [Theory]
    [InlineData(1u, uint.MaxValue, int.MaxValue)]
    [InlineData(uint.MaxValue, uint.MaxValue, int.MaxValue)]
    [InlineData(uint.MaxValue, 1u, int.MaxValue)]
    [InlineData(uint.MaxValue, 2u, int.MaxValue)]
    [InlineData(1u, 1u, 1)]
    [InlineData(101u, 100u, 1)]
    [InlineData(uint.MaxValue, uint.MaxValue - 1, 1)]
    [InlineData(uint.MaxValue, 85_899_346u, int.MaxValue)]
    public void The_comparisons_are_exact_at_the_extremes(uint numerator, uint denominator, int highest)
    {
        var rate = new RefreshRate(numerator, denominator);
        var exactBelow = 100 * (BigInteger)highest * denominator > 101 * (BigInteger)numerator;
        var exactAbove = (BigInteger)numerator >= ((BigInteger)highest + 1) * denominator;

        Assert.Equal(exactBelow, RefreshBelowMaxCheck.IsBelow(rate, highest));
        Assert.Equal(exactAbove, RefreshBelowMaxCheck.IsAboveOffered(rate, highest));
        Assert.True(RefreshBelowMaxCheck.AreEqual(rate, new RefreshRate(numerator, denominator)));
    }

    [Fact]
    public void A_problem_that_64_bit_arithmetic_would_wrap_into_an_ok_stays_a_problem()
    {
        // About 50 Hz against the largest mode a fact can hold: 100 × highest × denominator is just past 2^64.
        const uint denominator = 85_899_346;
        var rate = new RefreshRate(uint.MaxValue, denominator);
        var wrapped = unchecked(100UL * int.MaxValue * denominator) > 101UL * uint.MaxValue;

        Assert.True(RefreshBelowMaxCheck.IsBelow(rate, int.MaxValue));
        Assert.False(wrapped); // the naive 64-bit form gives the wrong answer here
    }

    [Fact]
    public void Zero_is_never_a_rate_or_a_mode_so_no_comparison_can_divide_by_or_compare_with_it()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RefreshRate(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RefreshRate(60, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VideoMode(2560, 1440, 0, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VideoMode(0, 1440, 60, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VideoMode(2560, 1440, -60, false));
    }

    [Fact]
    public void A_current_rate_a_whole_hertz_above_every_offered_mode_is_conflicting_evidence()
    {
        var result = Single(Desktop().WithDisplays(Display(signalHz: 166)));

        Assert.Equal(Verdict.CannotDetermine, result.Verdict);
        Assert.Equal(["current-above-offered"], result.Missing.Select(info => info.Code));
        Assert.Contains(result.Trace, step => step.Kind == TraceKind.Comparison && step.Code == "current-above-offered");
    }

    [Fact]
    public void A_higher_refresh_offered_only_at_another_resolution_is_not_this_checks_business()
    {
        var result = Single(Desktop().WithDisplays(Display(signalHz: 165)));

        Assert.Equal(Verdict.Ok, result.Verdict); // 240 Hz exists at 1920 × 1080 only
        Assert.Equal(CheckValue.Of(165), Param(result, "highestOffered"));
    }

    [Theory]
    [InlineData(true)] // interlaced modes only
    [InlineData(false)] // no mode at all at the resolution
    public void With_no_progressive_mode_at_the_resolution_nothing_is_compared(bool interlacedOnly)
    {
        var modes = interlacedOnly ? List(Mode(60, interlaced: true), Mode(120, interlaced: true)) : List(new VideoMode(1920, 1080, 60, false));

        var result = Single(Desktop().WithDisplays(Display(signalHz: 60) with { OfferedModes = modes }));

        Assert.Equal(Verdict.CannotDetermine, result.Verdict);
        Assert.Equal(["no-progressive-mode-at-resolution"], result.Missing.Select(info => info.Code));
    }

    [Fact]
    public void An_interlaced_mode_never_counts_as_the_highest()
    {
        var result = Single(Desktop().WithDisplays(Display(signalHz: 60) with { OfferedModes = List(Mode(60), Mode(120, interlaced: true)) }));

        Assert.Equal(Verdict.Ok, result.Verdict);
    }

    [Fact]
    public void A_portrait_desktop_is_compared_with_modes_listed_in_the_same_orientation()
    {
        var portrait = Display(signalHz: 60) with
        {
            DesktopResolution = Known(new PixelSize(1440, 2560)),
            OfferedModes = List(new VideoMode(1440, 2560, 60, false), new VideoMode(1440, 2560, 144, false), Mode(165)),
        };

        var result = Single(Desktop().WithDisplays(portrait));

        Assert.Equal(Verdict.Problem, result.Verdict);
        Assert.Equal(CheckValue.Of(144), Param(result, "highestOffered")); // the landscape 165 Hz mode is another resolution
    }

    public static TheoryData<string, Func<DisplayFacts, DisplayFacts>, string> MissingPremises() => new()
    {
        { "resolution", display => display with { DesktopResolution = Missing<PixelSize>(FactGap.Implausible) }, "desktop-resolution-missing" },
        { "signal", display => display with { SignalRefresh = Missing<RefreshRate>() }, "signal-refresh-missing" },
        { "modes", display => display with { OfferedModes = Missing<IReadOnlyList<VideoMode>>(FactGap.SourceFailed) }, "offered-modes-missing" },
        { "connection", display => display with { Connection = Missing<ConnectionKind>(FactGap.SourceFailed) }, "connection-missing" },
    };

    /// <summary>Missing each required fact, one at a time: Couldn't check, never a Problem, an Ok or Not applicable.</summary>
    [Theory]
    [MemberData(nameof(MissingPremises))]
    public void A_missing_required_display_fact_is_couldnt_check(string name, Func<DisplayFacts, DisplayFacts> remove, string code)
    {
        var result = Single(Desktop().WithDisplays(remove(Display(signalHz: 60))), TestGates.All);

        Assert.True(Verdict.CannotDetermine == result.Verdict, name);
        Assert.Equal([code], result.Missing.Select(info => info.Code));
        Assert.Null(result.Certainty);
    }

    [Fact]
    public void A_missing_session_is_couldnt_check_for_each_display()
    {
        var result = Single(Desktop().WithSession(Missing<SessionType>(FactGap.SourceFailed)).WithDisplays(Display(signalHz: 60)));

        Assert.Equal(Verdict.CannotDetermine, result.Verdict);
        Assert.Equal(["session-missing"], result.Missing.Select(info => info.Code));
    }

    [Fact]
    public void Missing_displays_are_one_couldnt_check_for_the_machine()
    {
        var results = Check.Evaluate(Desktop() with { Displays = Missing<IReadOnlyList<DisplayFacts>>(FactGap.TimedOut) }, FixtureGate.Validated);

        var result = Assert.Single(results);
        Assert.Equal(("machine", Verdict.CannotDetermine), (result.Subject, result.Verdict));
        Assert.Equal(["displays-missing"], result.Missing.Select(info => info.Code));
    }

    [Fact]
    public void A_remote_session_is_not_applicable_even_when_nothing_else_is_known()
    {
        var facts = Desktop().WithSession(Known(SessionType.Remote)) with { Displays = Missing<IReadOnlyList<DisplayFacts>>() };

        var result = Assert.Single(Check.Evaluate(facts, FixtureGate.Validated));

        Assert.Equal(("machine", Verdict.NotApplicable), (result.Subject, result.Verdict));
        Assert.Equal(["remote-session"], result.Trace.Select(step => step.Code));
    }

    [Fact]
    public void No_active_display_path_is_not_applicable_even_with_the_session_missing()
    {
        var facts = Desktop().WithSession(Missing<SessionType>()).WithDisplays();

        var result = Assert.Single(Check.Evaluate(facts, FixtureGate.Validated));

        Assert.Equal(Verdict.NotApplicable, result.Verdict);
        Assert.Equal(["no-active-display-path"], result.Trace.Select(step => step.Code));
    }

    [Theory]
    [InlineData(ConnectionKind.IndirectVirtual)]
    [InlineData(ConnectionKind.Miracast)]
    public void A_virtual_display_is_not_applicable_even_with_the_session_missing(ConnectionKind connection)
    {
        var result = Single(Desktop().WithSession(Missing<SessionType>()).WithDisplays(Display(connection: connection, signalHz: 60)));

        Assert.Equal(Verdict.NotApplicable, result.Verdict);
        Assert.Equal(["virtual-display"], result.Trace.Select(step => step.Code));
    }

    [Fact]
    public void Two_monitors_one_below_its_highest_give_exactly_one_problem()
    {
        var results = Check.Evaluate(Desktop().WithDisplays(Display(signalHz: 60), Display(id: "display-2", connection: ConnectionKind.Hdmi)), FixtureGate.Validated);

        Assert.Equal([("display-1", Verdict.Problem), ("display-2", Verdict.Ok)], results.Select(result => (result.Subject, result.Verdict)));
    }

    [Theory]
    [InlineData(false, "on-battery")]
    [InlineData(null, "power-unknown")]
    public void An_internal_panel_off_known_ac_power_is_likely_at_best_either_way(bool? onAc, string downgrade)
    {
        var power = onAc is { } value ? Known(value) : Missing<bool>(FactGap.TimedOut);
        foreach (var signal in new uint[] { 60, 165 })
        {
            var result = Single(Desktop().WithPower(onAc: power).WithDisplays(Display(connection: ConnectionKind.DisplayPortEmbedded, signalHz: signal)), TestGates.All);

            Assert.Equal(Certainty.Likely, result.Certainty);
            Assert.Contains(result.Trace, step => step.Kind == TraceKind.Downgrade && step.Code == downgrade);
            Assert.Contains(result.Evidence, item => item.FactPath == "/power/onAcPower" && item.Role == EvidenceRole.Downgrade);
        }
    }

    [Fact]
    public void An_internal_panel_on_ac_is_ok_under_its_own_condition_and_never_a_confirmed_problem()
    {
        var ok = Single(Desktop().WithDisplays(Display(connection: ConnectionKind.Internal)), TestGates.All);
        var problem = Single(Desktop().WithDisplays(Display(connection: ConnectionKind.Internal, signalHz: 60)), TestGates.All);

        Assert.Equal((Verdict.Ok, Certainty.Confirmed, "ok/internal/ac"), Outcome(ok));
        Assert.Contains(ok.Evidence, item => item.FactPath == "/power/onAcPower" && item.Role == EvidenceRole.Premise);
        Assert.Equal((Verdict.Problem, Certainty.Likely), (problem.Verdict, problem.Certainty));
        Assert.Contains(problem.Trace, step => step.Code == "dynamic-refresh-unobserved"); // dynamic refresh can't be ruled out yet
    }

    [Fact]
    public void A_path_rate_that_differs_from_the_signal_is_the_dynamic_refresh_signature_and_lowers_certainty()
    {
        var differ = Single(Desktop().WithDisplays(Display(signalHz: 60) with { PathRefresh = Known(new RefreshRate(120, 1)) }), TestGates.All);
        var unknown = Single(Desktop().WithDisplays(Display(signalHz: 60) with { PathRefresh = Missing<RefreshRate>() }), TestGates.All);

        Assert.Equal((Verdict.Problem, Certainty.Likely), (differ.Verdict, differ.Certainty));
        Assert.Contains(differ.Trace, step => step.Code == "path-signal-differ");
        Assert.Equal((Verdict.Problem, Certainty.Likely), (unknown.Verdict, unknown.Certainty));
        Assert.Contains(unknown.Trace, step => step.Code == "path-refresh-unknown");
    }

    [Theory]
    [InlineData(FactGap.Unmatched)]
    [InlineData(FactGap.Ambiguous)]
    [InlineData(FactGap.TimedOut)]
    public void An_hdmi_output_no_adapter_claims_is_never_confirmed(FactGap gap)
    {
        // Virtual display drivers can report HDMI; an adapter-less output is the one observable hint of one.
        var result = Single(Desktop().WithDisplays(Display(connection: ConnectionKind.Hdmi, signalHz: 60) with { Gpu = Missing<string>(gap) }), TestGates.All);

        Assert.Equal((Verdict.Problem, Certainty.Likely), (result.Verdict, result.Certainty));
        Assert.Contains(result.Trace, step => step.Code == "display-adapter-unattributed");
    }

    [Fact]
    public void A_clone_output_shares_its_sources_mode_list_so_it_is_likely_at_best()
    {
        var shared = Display(signalHz: 60) with { SharesSourceWith = List("display-2") };
        var unknown = Display(signalHz: 60) with { SharesSourceWith = Missing<IReadOnlyList<string>>(FactGap.Ambiguous) };

        var clone = Single(Desktop().WithDisplays(shared), TestGates.All);
        var ambiguous = Single(Desktop().WithDisplays(unknown), TestGates.All);

        Assert.Equal(Certainty.Likely, clone.Certainty);
        Assert.Equal(CheckValue.Symbols(["display-2"]), clone.Trace.Single(step => step.Code == "shared-source").Values.Single().Value);
        Assert.Equal(Certainty.Likely, ambiguous.Certainty);
        Assert.Contains(ambiguous.Trace, step => step.Code == "shared-source-unknown");
    }

    [Fact]
    public void An_unrecognised_connection_is_evaluated_but_never_confirmed()
    {
        var result = Single(Desktop().WithDisplays(Display(connection: ConnectionKind.Other, signalHz: 60)), TestGates.All);

        Assert.Equal((Verdict.Problem, Certainty.Likely), (result.Verdict, result.Certainty));
        Assert.Equal(["local-session", "unrecognised-display", "refresh-vs-highest-offered", "connection-unrecognised"], result.Trace.Select(step => step.Code));
    }

    [Fact]
    public void An_external_display_on_battery_is_not_lowered_by_the_battery()
    {
        var result = Single(Desktop().WithPower(onAc: Known(false), battery: Known(true)).WithDisplays(Display(signalHz: 60)), TestGates.All);

        Assert.Equal(Certainty.Confirmed, result.Certainty); // power saving is an internal panel's matter (§6.5)
        Assert.DoesNotContain(result.Evidence, item => item.FactPath == "/power/onAcPower");
    }

    [Fact]
    public void The_same_facts_give_the_same_results_byte_for_byte()
    {
        var facts = Desktop().WithDisplays(Display(signalHz: 60), Display(id: "display-2", connection: ConnectionKind.Internal) with { SharesSourceWith = List("display-1") });

        var once = Serialize(facts);
        var again = Serialize(facts);

        Assert.Equal(once, again);
    }

    private static CheckResult Single(NormalizedSnapshot facts, FixtureGate? gate = null) =>
        Assert.Single(Check.Evaluate(facts, gate ?? FixtureGate.Validated));

    private static (Verdict, Certainty?, string?) Outcome(CheckResult result) => (result.Verdict, result.Certainty, result.Condition);

    private static CheckValue Param(CheckResult result, string name) => result.Params.Single(value => value.Name == name).Value;

    private static byte[] Serialize(NormalizedSnapshot facts) =>
        CheckResultsJson.Serialize(new CheckResults(1, 2, facts.CapturedAtUtc, [], [], Check.Evaluate(facts, FixtureGate.Validated)));
}
