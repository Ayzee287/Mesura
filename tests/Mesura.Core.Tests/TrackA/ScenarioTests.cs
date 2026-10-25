using Mesura.Core.TrackA;

namespace Mesura.Core.Tests.TrackA;

/// <summary>Scenarios (spec §9): the owner's list, the five statuses (plus incomplete), and what decides them.</summary>
public sealed class ScenarioTests
{
    [Fact]
    public void The_catalog_holds_the_owner_s_scenarios()
    {
        Assert.Equal(
            ["baseline", "game-load", "display-high-refresh", "clone", "motherboard-display", "pci-e-idle-load", "steam-storage"],
            ScenarioCatalog.All.Select(scenario => scenario.Id));
        Assert.Equal([ScenarioCatalog.IdleStep, ScenarioCatalog.LoadStep], ScenarioCatalog.Require(ScenarioCatalog.PcieIdleLoad).Steps.Select(step => step.Name));
        Assert.All(ScenarioCatalog.All.Where(scenario => scenario.Id != ScenarioCatalog.PcieIdleLoad), scenario => Assert.Null(Assert.Single(scenario.Steps).Name));
    }

    [Theory]
    [InlineData("benchmark")]
    [InlineData("Baseline")]
    [InlineData("pcie-idle-load")]
    [InlineData(null)]
    public void An_unknown_scenario_is_refused(string? id)
    {
        Assert.Null(ScenarioCatalog.Find(id));
        Assert.Throws<ArgumentException>(() => ScenarioCatalog.Require(id));
        Assert.Throws<ArgumentException>(() => ScenarioCatalog.Require(ScenarioCatalog.Baseline).Step("idle"));
    }

    [Fact]
    public void Baseline_needs_no_external_tool_and_asks_no_machine_fact()
    {
        var baseline = ScenarioCatalog.Require(ScenarioCatalog.Baseline).Step(null);

        Assert.Empty(baseline.Evidence);
        Assert.False(baseline.InGame);
        Assert.Equal([ObservationCatalog.PhysicalChassisType], baseline.Fields);
    }

    [Theory]
    [InlineData(ScenarioDisposition.Performed, false, true, true, ScenarioStatus.Completed)]
    [InlineData(ScenarioDisposition.NotApplicable, true, false, false, ScenarioStatus.NotApplicable)]
    [InlineData(ScenarioDisposition.Skipped, true, false, false, ScenarioStatus.Skipped)]
    [InlineData(ScenarioDisposition.Performed, true, false, false, ScenarioStatus.Unavailable)]
    [InlineData(ScenarioDisposition.Performed, false, false, false, ScenarioStatus.Incomplete)]
    [InlineData(ScenarioDisposition.Performed, false, true, false, ScenarioStatus.NeedsManualEvidence)]
    public void The_status_follows_a_fixed_order_and_the_machine_outranks_a_declaration(
        ScenarioDisposition disposition, bool sourceUnavailable, bool proceduresConfirmed, bool evidenceComplete, ScenarioStatus expected)
    {
        Assert.Equal(expected, Scenario.StatusOf(disposition, sourceUnavailable, proceduresConfirmed, evidenceComplete));
    }

    [Theory]
    [InlineData("gpu-z-idle.png", EvidenceTool.GpuZ, EvidenceState.Idle, EvidenceKind.Screenshot)]
    [InlineData("GPU-Z-LOAD.PNG", EvidenceTool.GpuZ, EvidenceState.Load, EvidenceKind.Screenshot)]
    [InlineData("hwinfo-idle-2.png", EvidenceTool.Hwinfo, EvidenceState.Idle, EvidenceKind.Screenshot)]
    [InlineData("hwinfo-load-rootport.jpg", EvidenceTool.Hwinfo, EvidenceState.Load, EvidenceKind.Screenshot)]
    [InlineData("devmgr-idle.png", EvidenceTool.DeviceManager, EvidenceState.Idle, EvidenceKind.Screenshot)]
    [InlineData("pcie-probe-idle-20260929T082922Z.json", EvidenceTool.PcieProbe, EvidenceState.Idle, EvidenceKind.TextOutput)]
    [InlineData("pcie-probe-game-20260929T090000Z.json", EvidenceTool.PcieProbe, EvidenceState.Load, EvidenceKind.TextOutput)]
    [InlineData("pcie-probe-after-20260929T091000Z.json", EvidenceTool.PcieProbe, EvidenceState.After, EvidenceKind.TextOutput)]
    public void Evidence_names_follow_the_convention(string name, EvidenceTool tool, EvidenceState state, EvidenceKind kind)
    {
        Assert.Equal(new EvidenceName(tool, state, kind), EvidenceConvention.Read(name));
    }

    [Theory]
    [InlineData("Screenshot 2026-09-29 Dave's PC.png")]
    [InlineData("gpu-z.png")]
    [InlineData("gpu-z-idle.exe")]
    [InlineData("gpu-z-idle")]
    [InlineData("..\\gpu-z-idle.png")]
    [InlineData("gpu-z-idle.png.lnk")]
    [InlineData("gpu-z-idle-maria.png")] // a suffix is never free text: it could be a person's name
    [InlineData("hwinfo-idle-DESKTOP7F3K.png")]
    [InlineData("hwinfo-idle-1234.png")]
    public void Other_names_are_not_evidence(string name) => Assert.Null(EvidenceConvention.Read(name));

    [Fact]
    public void Evidence_belongs_to_the_step_whose_state_it_names()
    {
        Assert.True(EvidenceConvention.BelongsTo(EvidenceState.Idle, ScenarioCatalog.IdleStep));
        Assert.False(EvidenceConvention.BelongsTo(EvidenceState.Load, ScenarioCatalog.IdleStep));
        Assert.True(EvidenceConvention.BelongsTo(EvidenceState.After, ScenarioCatalog.LoadStep));
        Assert.True(EvidenceConvention.BelongsTo(EvidenceState.Load, null));
    }
}
