using Mesura.Core.Checks;
using Mesura.Core.Facts;
using Mesura.Core.Normalize;
using Mesura.Core.Raw.Serialization;

namespace Mesura.Core.Tests.Checks;

/// <summary>
/// Checks 1 and 2 on the notebook fixture (an invented ACMEPC notebook built on a scrubbed capture: a notebook, Iris Xe only, a 2160 × 1440 internal
/// panel offered only 60 Hz, captured on battery). The expected outcomes are the Milestone 3 pre-flight's, from the
/// machine's known configuration. PC-000 exercises the Likely and Not applicable paths only: it validates no Problem
/// path and unlocks no Confirmed condition.
/// </summary>
public sealed class RealFixtureCheckTests
{
    private static readonly NormalizedSnapshot Facts = Normalizer.Normalize(
        SnapshotJson.Deserialize(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "scrubbed", "PC-000-20260928T223014Z.json"))));

    [Fact]
    public void Check_1_is_ok_and_likely_because_the_panel_ran_on_battery()
    {
        var result = Assert.Single(new RefreshBelowMaxCheck().Evaluate(Facts, FixtureGate.Validated));

        Assert.Equal(("display-1", Verdict.Ok, (Certainty?)Certainty.Likely), (result.Subject, result.Verdict, result.Certainty));
        Assert.Equal(["local-session", "internal-display", "refresh-vs-highest-offered", "on-battery"], result.Trace.Select(step => step.Code));
        Assert.Equal(CheckValue.Of(60), result.Params.Single(value => value.Name == "highestOffered").Value);
        Assert.Equal(CheckValue.Of(new RefreshRate(20602000, 343360)), result.Params.Single(value => value.Name == "current").Value);
        Assert.Null(result.Condition); // a downgrade, not the gate, holds it at Likely
    }

    [Fact]
    public void Check_1_stays_likely_even_under_a_gate_that_unlocks_everything()
    {
        var result = Assert.Single(new RefreshBelowMaxCheck().Evaluate(Facts, TestGates.All));

        Assert.Equal(Certainty.Likely, result.Certainty); // captured on battery: it can't exercise the Confirmed condition
    }

    [Fact]
    public void Check_2_is_not_applicable_on_a_notebook_without_a_discrete_gpu()
    {
        var result = Assert.Single(new OnIntegratedGpuCheck().Evaluate(Facts, FixtureGate.Validated));

        Assert.Equal(("machine", Verdict.NotApplicable), (result.Subject, result.Verdict));
        Assert.Equal(["portable-chassis", "no-discrete-gpu"], result.Trace.Select(step => step.Code));
        Assert.All(result.Evidence, item => Assert.True(item.IsKnown && item.Role == EvidenceRole.Applicability));
    }

    [Fact]
    public void The_basic_render_driver_is_software_and_never_counts_as_a_discrete_gpu()
    {
        var result = Assert.Single(new OnIntegratedGpuCheck().Evaluate(Facts, FixtureGate.Validated));
        var basic = Facts.Gpus.Value!.Select((gpu, index) => (gpu, index)).Single(pair => pair.gpu.Name.Value == "Microsoft Basic Render Driver");

        Assert.False(basic.gpu.IsIntegrated.Value); // not integrated, and still not discrete
        Assert.Contains(result.Evidence, item => item.FactPath == $"/gpus/value/{basic.index}/isHardware" && item.Value!.Value.GetRawText() == "false");
    }

    [Fact]
    public void Pc_000_gives_no_problem_and_no_confirmed_result_and_unlocks_nothing()
    {
        var results = CheckRunner.Run(Facts, FixtureGate.Validated);

        Assert.DoesNotContain(results.Results, result => result.Verdict == Verdict.Problem || result.Certainty == Certainty.Confirmed);
        Assert.Empty(FixtureGate.Validated.Unlocked);
    }
}
