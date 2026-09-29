using FpsUtility.Core.Checks;
using FpsUtility.Core.Facts;
using static FpsUtility.Core.Tests.Checks.FactsBuilder;

namespace FpsUtility.Core.Tests.Checks;

/// <summary>
/// The confidence model's rules (architecture §6.3, §4.6), enforced by <see cref="CheckResult"/>'s constructor: a result
/// that breaks one cannot be built, by a check or by anyone else.
/// </summary>
public sealed class CheckResultModelTests
{
    private static readonly Evidence Premise = Evidence.Of("/displays/value/0/signalRefresh", EvidenceRole.Premise, Known(new RefreshRate(60, 1)));
    private static readonly Evidence Applicability = Evidence.Of("/os/session", EvidenceRole.Applicability, Known(Raw.SessionType.Local));
    private static readonly TraceStep Comparison = new("refresh-vs-highest-offered", TraceKind.Comparison, new NamedValue("highestOffered", CheckValue.Of(165)));
    private static readonly TraceStep Gate = new("unvalidated-condition", TraceKind.Gate);
    private static readonly TraceStep Downgrade = new("on-battery", TraceKind.Downgrade);
    private static readonly TraceStep Applies = new("local-session", TraceKind.Applicability);

    [Fact]
    public void A_gated_likely_problem_and_a_confirmed_one_can_be_built()
    {
        var likely = Decided(Certainty.Likely, "problem/external", [Premise], [Comparison, Gate]);
        var confirmed = Decided(Certainty.Confirmed, "problem/external", [Premise], [Comparison]);

        Assert.Equal(Certainty.Likely, likely.Certainty);
        Assert.Equal(Certainty.Confirmed, confirmed.Certainty);
    }

    [Theory]
    [InlineData(Verdict.Problem, null)]
    [InlineData(Verdict.Ok, null)]
    [InlineData(Verdict.NotApplicable, Certainty.Likely)]
    [InlineData(Verdict.CannotDetermine, Certainty.Confirmed)]
    public void Certainty_belongs_to_problem_and_ok_only(Verdict verdict, Certainty? certainty)
    {
        Assert.Throws<ArgumentException>(() => new CheckResult(
            CheckIds.RefreshBelowMax, "refresh-vs-highest-offered", "display-1", verdict, certainty, null,
            [Premise], [Comparison], [], verdict == Verdict.CannotDetermine ? [new MissingInfo("x-missing", Premise.FactPath)] : []));
    }

    [Fact]
    public void Not_applicable_rests_only_on_known_applicability_facts_with_a_reason()
    {
        var missingSession = Evidence.Of("/os/session", EvidenceRole.Applicability, Missing<Raw.SessionType>());

        var valid = Build(Verdict.NotApplicable, null, null, [Applicability], [new TraceStep("remote-session", TraceKind.Applicability)], []);

        Assert.Equal(Verdict.NotApplicable, valid.Verdict);
        Assert.Throws<ArgumentException>(() => Build(Verdict.NotApplicable, null, null, [missingSession], [Applies], [])); // never from a missing fact
        Assert.Throws<ArgumentException>(() => Build(Verdict.NotApplicable, null, null, [], [Applies], []));
        Assert.Throws<ArgumentException>(() => Build(Verdict.NotApplicable, null, null, [Premise], [Applies], []));
        Assert.Throws<ArgumentException>(() => Build(Verdict.NotApplicable, null, null, [Applicability], [], []));
        Assert.Throws<ArgumentException>(() => Build(Verdict.NotApplicable, null, null, [Applicability], [Applies, Comparison], []));
    }

    [Fact]
    public void Couldnt_check_exactly_when_something_is_named_missing_and_each_named_fact_is_in_the_evidence()
    {
        var missing = Evidence.Of("/displays/value/0/signalRefresh", EvidenceRole.Premise, Missing<RefreshRate>());
        var info = new MissingInfo("signal-refresh-missing", "/displays/value/0/signalRefresh");

        var valid = Build(Verdict.CannotDetermine, null, null, [missing], [], [info]);

        Assert.Equal(FactGap.NotPresent, valid.Evidence.Single().Missing);
        Assert.Throws<ArgumentException>(() => Build(Verdict.CannotDetermine, null, null, [missing], [], []));
        Assert.Throws<ArgumentException>(() => Build(Verdict.CannotDetermine, null, null, [], [], [info]));
        Assert.Throws<ArgumentException>(() => Build(Verdict.Problem, Certainty.Likely, "problem/external", [Premise], [Comparison, Gate], [info]));
        Assert.Throws<ArgumentException>(() => new MissingInfo("signal-refresh-missing"));
    }

    [Fact]
    public void Couldnt_check_has_no_certainty_to_lower()
    {
        var missing = Evidence.Of("/displays/value/0/signalRefresh", EvidenceRole.Premise, Missing<RefreshRate>());
        var info = new MissingInfo("signal-refresh-missing", "/displays/value/0/signalRefresh");
        var lowered = Evidence.Of("/power/onAcPower", EvidenceRole.Downgrade, Known(false));

        Assert.Throws<ArgumentException>(() => Build(Verdict.CannotDetermine, null, null, [missing, lowered], [Downgrade], [info]));
        Assert.Throws<ArgumentException>(() => Build(Verdict.CannotDetermine, null, null, [missing], [Gate], [info]));
        Assert.Throws<ArgumentException>(() => Build(Verdict.CannotDetermine, null, "problem/external", [missing], [], [info]));
    }

    [Fact]
    public void A_problem_or_ok_rests_on_known_premises_and_a_comparison()
    {
        var missingPremise = Evidence.Of("/displays/value/0/signalRefresh", EvidenceRole.Premise, Missing<RefreshRate>());
        var missingApplicability = Evidence.Of("/os/session", EvidenceRole.Applicability, Missing<Raw.SessionType>());

        Assert.Throws<ArgumentException>(() => Decided(Certainty.Likely, "problem/external", [Applicability], [Comparison, Gate])); // no premise
        Assert.Throws<ArgumentException>(() => Decided(Certainty.Likely, "problem/external", [missingPremise], [Comparison, Gate]));
        Assert.Throws<ArgumentException>(() => Decided(Certainty.Likely, "problem/external", [Premise, missingApplicability], [Comparison, Gate]));
        Assert.Throws<ArgumentException>(() => Decided(Certainty.Likely, "problem/external", [Premise], [Gate])); // no comparison
    }

    [Fact]
    public void A_missing_fact_can_only_lower_certainty_and_must_say_how()
    {
        var unknownPower = Evidence.Of("/power/onAcPower", EvidenceRole.Downgrade, Missing<bool>(FactGap.TimedOut));

        var likely = Decided(Certainty.Likely, null, [Premise, unknownPower], [Comparison, new TraceStep("power-unknown", TraceKind.Downgrade)]);

        Assert.Equal(Certainty.Likely, likely.Certainty);
        Assert.Throws<ArgumentException>(() => Decided(Certainty.Likely, "problem/external", [Premise, unknownPower], [Comparison, Gate])); // silent downgrade fact
        Assert.Throws<ArgumentException>(() => Decided(Certainty.Confirmed, null, [Premise, unknownPower], [Comparison, new TraceStep("power-unknown", TraceKind.Downgrade)]));
    }

    [Fact]
    public void Likely_always_names_its_downgrade_or_the_gate()
    {
        Assert.Throws<ArgumentException>(() => Decided(Certainty.Likely, "problem/external", [Premise], [Comparison]));
    }

    [Fact]
    public void Confirmed_carries_no_downgrade_and_no_unresolved_gate()
    {
        Assert.Throws<ArgumentException>(() => Decided(Certainty.Confirmed, "problem/external", [Premise], [Comparison, Gate]));
        Assert.Throws<ArgumentException>(() => Decided(Certainty.Confirmed, null, [Premise], [Comparison, Downgrade]));
        Assert.Throws<ArgumentException>(() => Decided(Certainty.Confirmed, null, [Premise], [Comparison])); // no condition: nothing unlocked it
    }

    [Fact]
    public void A_condition_is_named_exactly_when_no_downgrade_applies_and_starts_with_its_verdict()
    {
        Assert.Throws<ArgumentException>(() => Decided(Certainty.Likely, "problem/external", [Premise], [Comparison, Downgrade]));
        Assert.Throws<ArgumentException>(() => Decided(Certainty.Likely, null, [Premise], [Comparison, Gate]));
        Assert.Throws<ArgumentException>(() => Decided(Certainty.Likely, "ok/external", [Premise], [Comparison, Gate])); // a Problem's key
        Assert.Throws<ArgumentException>(() => Decided(Certainty.Likely, "problem", [Premise], [Comparison, Gate]));
        Assert.Throws<ArgumentException>(() => Decided(Certainty.Likely, "problem/external", [Premise], [Comparison, Gate, Downgrade]));
    }

    [Fact]
    public void A_fact_has_one_role_in_a_result()
    {
        var sameFactAsDowngrade = Evidence.Of(Premise.FactPath, EvidenceRole.Downgrade, Known(new RefreshRate(60, 1)));

        Assert.Throws<ArgumentException>(() => Decided(Certainty.Likely, null, [Premise, sameFactAsDowngrade], [Comparison, Downgrade]));
    }

    [Theory]
    [InlineData("Refresh below max")]
    [InlineData("refresh_below")]
    [InlineData("Your monitor runs at 60 Hz")]
    [InlineData("")]
    public void Codes_are_tokens_so_no_prose_can_enter_a_result(string code)
    {
        Assert.ThrowsAny<ArgumentException>(() => new TraceStep(code, TraceKind.Comparison));
        Assert.ThrowsAny<ArgumentException>(() => new MissingInfo(code, "/os/session"));
        Assert.ThrowsAny<ArgumentException>(() => CheckValue.Symbol(code));
        Assert.ThrowsAny<ArgumentException>(() => new NamedValue(code, CheckValue.Of(1)));
        Assert.ThrowsAny<ArgumentException>(() => new CheckResult(CheckIds.RefreshBelowMax, code, "display-1", Verdict.Problem, Certainty.Likely, "problem/external", [Premise], [Comparison, Gate], [], []));
    }

    [Fact]
    public void A_name_appears_once_in_parameters_and_in_a_steps_values()
    {
        var value = new NamedValue("current", CheckValue.Of(60));

        Assert.Throws<ArgumentException>(() => new TraceStep("refresh-vs-highest-offered", TraceKind.Comparison, value, value));
        Assert.Throws<ArgumentException>(() => new CheckResult(
            CheckIds.RefreshBelowMax, "refresh-vs-highest-offered", "display-1", Verdict.Problem, Certainty.Likely, "problem/external",
            [Premise], [Comparison, Gate], [value, value], []));
    }

    [Fact]
    public void Evidence_copies_the_facts_state_and_raw_pointers_but_never_its_english_detail()
    {
        var derived = Fact.Derived("gpu-2", Basis.Authoritative, Rules.DisplayGpu, "/sources/display/data/outputs/0/adapterId", "/sources/adapters/data/adapters/1/adapterId");
        var missing = Fact.Missing<string>(FactGap.Ambiguous, "More than one adapter has the output's adapter id", "/sources/display/data/outputs/0/adapterId");

        var known = Evidence.Of("/displays/value/0/gpu", EvidenceRole.Premise, derived);
        var gap = Evidence.Of("/displays/value/0/gpu", EvidenceRole.Downgrade, missing);

        Assert.Equal("\"gpu-2\"", known.Value!.Value.GetRawText());
        Assert.Equal(Basis.Authoritative, known.Basis);
        Assert.Equal(Rules.DisplayGpu, known.Rule);
        Assert.Null(known.Missing);
        Assert.Equal(derived.From, known.From);
        Assert.Equal(FactGap.Ambiguous, gap.Missing);
        Assert.Null(gap.Value);
        Assert.Null(gap.Basis);
        Assert.Null(gap.Rule);
        Assert.Equal(missing.From, gap.From);
        Assert.DoesNotContain(typeof(Evidence).GetProperties(), property => property.Name == nameof(Fact<int>.Detail));
    }

    [Theory]
    [InlineData("displays/value/0")]
    [InlineData("/displays/value/0/signal refresh")]
    [InlineData("")]
    public void Evidence_points_into_the_facts_with_a_json_pointer(string pointer)
    {
        Assert.ThrowsAny<ArgumentException>(() => Evidence.Of(pointer, EvidenceRole.Premise, Known(1)));
    }

    /// <summary>
    /// Pins the bases a Confirmed may rest on. The compiler enforces the rest: a new <see cref="Basis"/> member makes
    /// <c>CheckResult</c> fail to build (CS8509) until someone decides whether a Confirmed may rest on it (§6.3: an
    /// Inferred or UserProvided fact makes a result Likely).
    /// </summary>
    [Fact]
    public void Confirmed_is_decided_for_every_basis_that_exists()
    {
        Assert.Equal([Basis.Authoritative, Basis.Declared], Enum.GetValues<Basis>());

        var declared = Evidence.Of("/machine/chassisType", EvidenceRole.Premise, Known(ChassisType.Tower, Basis.Declared));
        Assert.Equal(Certainty.Confirmed, Decided(Certainty.Confirmed, "problem/external", [declared], [Comparison]).Certainty);
    }

    [Fact]
    public void Verdicts_and_certainties_are_not_fact_states()
    {
        var judgements = Enum.GetNames<Verdict>().Concat(Enum.GetNames<Certainty>()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain(Enum.GetNames<FactGap>(), judgements.Contains);
        Assert.DoesNotContain(Enum.GetNames<Basis>(), judgements.Contains);
        Assert.DoesNotContain(typeof(Fact<int>).GetProperties(), property => property.PropertyType == typeof(Verdict) || property.PropertyType == typeof(Certainty?));
    }

    /// <summary>Decision A: the check layer chooses no remedy and plans no verification (architecture §4.6).</summary>
    [Fact]
    public void A_result_has_no_actions_and_no_verification_plan()
    {
        var names = typeof(CheckResult).GetProperties().Select(property => property.Name.ToLowerInvariant()).ToList();

        Assert.DoesNotContain(names, name => name.Contains("action", StringComparison.Ordinal) || name.Contains("verif", StringComparison.Ordinal)
            || name.Contains("recommend", StringComparison.Ordinal) || name.Contains("fix", StringComparison.Ordinal) || name.Contains("score", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(CheckResult).Assembly.GetTypes(), type => type.Name is "RecommendedAction" or "Verification");
    }

    [Fact]
    public void The_products_gate_unlocks_nothing_and_a_test_gate_unlocks_only_what_it_names()
    {
        var gate = TestGates.Only(CheckIds.RefreshBelowMax, "problem/external");

        Assert.Empty(FixtureGate.Validated.Unlocked); // Decision B: no owner-labelled fixture yet; PC-000 unlocks nothing
        Assert.True(gate.Unlocks(CheckIds.RefreshBelowMax, "problem/external"));
        Assert.False(gate.Unlocks(CheckIds.RefreshBelowMax, "ok/external"));
        Assert.False(gate.Unlocks(CheckIds.OnIntegratedGpu, "problem/external")); // keyed by check
    }

    [Fact]
    public void A_gated_condition_names_the_fixture_that_unlocks_it_once()
    {
        Assert.ThrowsAny<ArgumentException>(() => new GatedCondition(CheckIds.RefreshBelowMax, "problem/external", " "));
        Assert.ThrowsAny<ArgumentException>(() => new GatedCondition(CheckIds.RefreshBelowMax, "problem", "PC-001.json"));
        Assert.ThrowsAny<ArgumentException>(() => new FixtureGate([
            new(CheckIds.RefreshBelowMax, "problem/external", "PC-001.json"),
            new(CheckIds.RefreshBelowMax, "problem/external", "PC-002.json"),
        ]));
    }

    [Fact]
    public void Values_are_typed_and_compare_by_value()
    {
        Assert.Equal(CheckValue.Symbols(["gpu-1", "gpu-2"]), CheckValue.Symbols(["gpu-1", "gpu-2"]));
        Assert.NotEqual(CheckValue.Symbols(["gpu-1", "gpu-2"]), CheckValue.Symbols(["gpu-2", "gpu-1"]));
        Assert.Equal(CheckValue.Symbol("displayPort"), CheckValue.Of(Raw.ConnectionKind.DisplayPort));
        Assert.Equal(CheckValue.Of(new RefreshRate(60000, 1001)), CheckValue.Of(new RefreshRate(60000, 1001)));
        Assert.Throws<ArgumentOutOfRangeException>(() => CheckValue.Of((Raw.ConnectionKind)99));
    }

    private static CheckResult Decided(Certainty certainty, string? condition, IReadOnlyList<Evidence> evidence, IReadOnlyList<TraceStep> trace) =>
        Build(Verdict.Problem, certainty, condition, evidence, trace, []);

    private static CheckResult Build(Verdict verdict, Certainty? certainty, string? condition, IReadOnlyList<Evidence> evidence, IReadOnlyList<TraceStep> trace, IReadOnlyList<MissingInfo> missing) =>
        new(CheckIds.RefreshBelowMax, "refresh-vs-highest-offered", "display-1", verdict, certainty, condition, evidence, trace, [], missing);
}
