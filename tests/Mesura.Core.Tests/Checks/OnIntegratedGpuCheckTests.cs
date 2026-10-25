using Mesura.Core.Checks;
using Mesura.Core.Facts;
using Mesura.Core.Raw;
using static Mesura.Core.Tests.Checks.FactsBuilder;

namespace Mesura.Core.Tests.Checks;

/// <summary>
/// Check 2 on built facts (architecture §6.5, §4.6). The default machine is a tower with no battery, an integrated GPU
/// (gpu-1), a discrete GPU (gpu-2) and a software adapter (gpu-3).
/// </summary>
public sealed class OnIntegratedGpuCheckTests
{
    private static readonly OnIntegratedGpuCheck Check = new();

    [Fact]
    public void An_external_display_on_the_integrated_gpu_of_a_desktop_whose_discrete_gpu_drives_nothing_is_a_problem()
    {
        var result = Single(Desktop().WithDisplays(Display(gpu: "gpu-1", connection: ConnectionKind.Hdmi)));

        Assert.Equal((Verdict.Problem, Certainty.Likely, "problem/desktop"), Outcome(result));
        Assert.Equal(
            ["external-display", "local-session", "desktop-chassis", "no-battery-reported", "discrete-gpu-present", "driving-gpu-kind", "unvalidated-condition"],
            result.Trace.Select(step => step.Code));
        Assert.Equal(CheckValue.Symbol("gpu-1"), Param(result, "drivingGpu"));
        Assert.Equal(CheckValue.Symbols(["gpu-2"]), Param(result, "discreteGpus"));
        Assert.Equal(CheckValue.Of(ConnectionKind.Hdmi), Param(result, "connection"));
    }

    [Fact]
    public void The_problem_is_confirmed_only_under_a_gate_that_unlocks_its_condition()
    {
        var facts = Desktop().WithDisplays(Display(gpu: "gpu-1"));

        Assert.Equal((Verdict.Problem, Certainty.Confirmed, "problem/desktop"), Outcome(Single(facts, TestGates.All)));
        Assert.Equal(Certainty.Likely, Single(facts, TestGates.Only(CheckIds.RefreshBelowMax, "problem/desktop")).Certainty);
    }

    [Fact]
    public void An_external_display_on_the_discrete_gpu_is_ok()
    {
        var result = Single(Desktop());

        Assert.Equal((Verdict.Ok, Certainty.Likely, "ok/desktop"), Outcome(result));
        Assert.Equal((Verdict.Ok, Certainty.Confirmed, "ok/desktop"), Outcome(Single(Desktop(), TestGates.All)));
        Assert.All(result.Evidence.Where(item => item.FactPath.StartsWith("/gpus/value/1/", StringComparison.Ordinal)), item => Assert.Equal(EvidenceRole.Premise, item.Role));
    }

    [Fact]
    public void When_both_gpus_drive_screens_the_integrated_one_may_be_an_intended_secondary_screen()
    {
        var results = Check.Evaluate(Desktop().WithDisplays(Display(gpu: "gpu-1"), Display(id: "display-2")), TestGates.All);

        Assert.Equal([(Verdict.Problem, Certainty.Likely), (Verdict.Ok, Certainty.Confirmed)], results.Select(result => (result.Verdict, result.Certainty)));
        var step = results[0].Trace.Single(step => step.Code == "discrete-gpu-drives-other-display");
        Assert.Equal(CheckValue.Symbols(["display-2"]), step.Values.Single().Value);
        Assert.Contains(results[0].Evidence, item => item.FactPath == "/displays/value/1/gpu" && item.Role == EvidenceRole.Downgrade);
    }

    [Fact]
    public void Another_display_whose_adapter_is_unknown_leaves_the_problem_likely()
    {
        var results = Check.Evaluate(Desktop().WithDisplays(Display(gpu: "gpu-1"), Display(id: "display-2") with { Gpu = Missing<string>(FactGap.Unmatched) }), TestGates.All);

        Assert.Equal((Verdict.Problem, Certainty.Likely), (results[0].Verdict, results[0].Certainty));
        Assert.Contains(results[0].Trace, step => step.Code == "display-attribution-incomplete");
        Assert.Equal(Verdict.CannotDetermine, results[1].Verdict);
        Assert.Equal(["display-gpu-missing"], results[1].Missing.Select(info => info.Code));
    }

    [Fact]
    public void A_laptop_with_a_discrete_gpu_and_a_monitor_on_the_integrated_one_is_not_applicable()
    {
        var result = Assert.Single(Check.Evaluate(Desktop().WithChassis(ChassisType.Notebook).WithDisplays(Display(gpu: "gpu-1")), TestGates.All));

        Assert.Equal(("machine", Verdict.NotApplicable), (result.Subject, result.Verdict));
        Assert.Equal(["portable-chassis"], result.Trace.Select(step => step.Code));
    }

    [Fact]
    public void An_integrated_gpu_with_only_a_software_adapter_beside_it_is_not_applicable()
    {
        // The software adapter isn't integrated either, and it still never counts as a discrete GPU.
        var facts = Desktop().WithGpus(Integrated("gpu-1", "pci-1"), Software("gpu-2")) with { PciDevices = List(Pci("pci-1")) };

        var result = Assert.Single(Check.Evaluate(facts.WithDisplays(Display(gpu: "gpu-1")), FixtureGate.Validated));

        Assert.Equal(Verdict.NotApplicable, result.Verdict);
        Assert.Equal(["no-discrete-gpu"], result.Trace.Select(step => step.Code));
        Assert.All(result.Evidence, item => Assert.True(item.IsKnown));
    }

    [Fact]
    public void Every_reason_for_not_applicable_is_traced_and_a_missing_session_does_not_hide_them()
    {
        var facts = Desktop().WithSession(Missing<SessionType>()).WithChassis(ChassisType.Notebook).WithGpus(Integrated("gpu-1", "pci-1")).WithDisplays() with { PciDevices = List(Pci("pci-1")) };

        var result = Assert.Single(Check.Evaluate(facts, FixtureGate.Validated));

        Assert.Equal(Verdict.NotApplicable, result.Verdict);
        Assert.Equal(["portable-chassis", "no-discrete-gpu", "no-active-display-path"], result.Trace.Select(step => step.Code));
    }

    [Fact]
    public void A_remote_session_is_not_applicable()
    {
        var result = Assert.Single(Check.Evaluate(Desktop().WithSession(Known(SessionType.Remote)), FixtureGate.Validated));

        Assert.Equal(Verdict.NotApplicable, result.Verdict);
        Assert.Equal(["remote-session"], result.Trace.Select(step => step.Code));
    }

    public static TheoryData<ChassisType?, bool?, Verdict, string?> FormFactors() => new()
    {
        // Full desktop: the battery can only lower certainty.
        { ChassisType.Tower, false, Verdict.Problem, null },
        { ChassisType.Desktop, true, Verdict.Problem, "chassis-battery-inconsistent" },
        { ChassisType.MiniTower, null, Verdict.Problem, "battery-unknown" },

        // Compact: applies, never confirmed.
        { ChassisType.MiniPc, false, Verdict.Problem, "compact-chassis" },
        { ChassisType.AllInOne, false, Verdict.Problem, "compact-chassis" },
        { ChassisType.SealedCasePc, true, Verdict.Problem, "compact-chassis" },

        // Portable: not applicable whatever the battery says.
        { ChassisType.Laptop, false, Verdict.NotApplicable, "portable-chassis" },
        { ChassisType.Convertible, true, Verdict.NotApplicable, "portable-chassis" },
        { ChassisType.Tablet, null, Verdict.NotApplicable, "portable-chassis" },

        // Unknown: no battery makes it apply (Likely); a battery or no answer leaves it undecided, never a laptop.
        { ChassisType.Other, false, Verdict.Problem, "chassis-unknown" },
        { ChassisType.Unknown, true, Verdict.CannotDetermine, "form-factor-undetermined" },
        { ChassisType.DockingStation, null, Verdict.CannotDetermine, "form-factor-undetermined" },
        { null, false, Verdict.Problem, "chassis-unknown" },
        { null, true, Verdict.CannotDetermine, "form-factor-undetermined" },
        { null, null, Verdict.CannotDetermine, "form-factor-undetermined" },
    };

    [Theory]
    [MemberData(nameof(FormFactors))]
    public void The_form_factor_comes_from_the_chassis_and_a_battery_can_only_lower_certainty(ChassisType? chassis, bool? battery, Verdict verdict, string? code)
    {
        var facts = Desktop()
            .WithChassis(chassis is { } declared ? Known(declared, Basis.Declared) : Missing<ChassisType>(FactGap.Implausible))
            .WithPower(battery: battery is { } reported ? Known(reported) : Missing<bool>(FactGap.NotSupported))
            .WithDisplays(Display(gpu: "gpu-1"));

        var result = Assert.Single(Check.Evaluate(facts, TestGates.All));

        Assert.Equal(verdict, result.Verdict);
        if (code is null)
        {
            Assert.Equal(Certainty.Confirmed, result.Certainty);
        }
        else
        {
            Assert.Contains(code, result.Trace.Select(step => step.Code).Concat(result.Missing.Select(info => info.Code)));
            Assert.NotEqual(Certainty.Confirmed, result.Certainty);
        }
    }

    [Fact]
    public void An_all_in_ones_own_panel_is_not_applicable_and_its_external_monitor_is_likely_at_best()
    {
        var facts = Desktop().WithChassis(ChassisType.AllInOne).WithDisplays(Display(gpu: "gpu-1", connection: ConnectionKind.DisplayPortEmbedded), Display(id: "display-2", gpu: "gpu-1"));

        var results = Check.Evaluate(facts, TestGates.All);

        Assert.Equal([Verdict.NotApplicable, Verdict.Problem], results.Select(result => result.Verdict));
        Assert.Equal(["internal-display"], results[0].Trace.Select(step => step.Code));
        Assert.Equal(Certainty.Likely, results[1].Certainty);
    }

    [Theory]
    [InlineData(ConnectionKind.IndirectVirtual, "virtual-display")]
    [InlineData(ConnectionKind.Miracast, "virtual-display")]
    [InlineData(ConnectionKind.Internal, "internal-display")]
    [InlineData(ConnectionKind.Lvds, "internal-display")]
    public void A_virtual_or_built_in_display_is_not_applicable(ConnectionKind connection, string reason)
    {
        var result = Single(Desktop().WithDisplays(Display(gpu: "gpu-1", connection: connection)));

        Assert.Equal(Verdict.NotApplicable, result.Verdict);
        Assert.Equal([reason], result.Trace.Select(step => step.Code));
    }

    [Fact]
    public void An_unrecognised_connection_is_evaluated_but_never_confirmed()
    {
        var result = Single(Desktop().WithDisplays(Display(gpu: "gpu-1", connection: ConnectionKind.Other)), TestGates.All);

        Assert.Equal((Verdict.Problem, Certainty.Likely), (result.Verdict, result.Certainty));
        Assert.Contains(result.Trace, step => step.Code == "connection-unrecognised");
    }

    [Theory]
    [InlineData(true)] // the hardware flag itself
    [InlineData(false)] // a hardware adapter's integrated flag
    public void An_adapter_that_cannot_be_classified_is_couldnt_check_never_not_applicable(bool hardwareFlag)
    {
        // Without the discrete GPU, a missing flag on the other adapter would otherwise read as "no discrete GPU".
        var gpu = Integrated("gpu-2", "pci-2");
        var unclassified = hardwareFlag
            ? gpu with { IsHardware = Missing<bool>(FactGap.SourceFailed) }
            : gpu with { IsIntegrated = Missing<bool>(FactGap.SourceFailed) };
        var facts = Desktop().WithGpus(Integrated("gpu-1", "pci-1"), unclassified).WithDisplays(Display(gpu: "gpu-1"));

        var result = Assert.Single(Check.Evaluate(facts, FixtureGate.Validated));

        Assert.Equal(Verdict.CannotDetermine, result.Verdict);
        Assert.Equal(["adapter-kind-missing"], result.Missing.Select(info => info.Code));
    }

    [Theory]
    [InlineData(FactGap.Unmatched)] // a USB or BMC adapter: no PCI display function of its identity
    [InlineData(FactGap.Ambiguous)] // two identical cards, or an unreadable device that might be it
    public void A_discrete_adapter_without_an_established_pci_function_is_couldnt_check(FactGap gap)
    {
        var facts = Desktop().WithGpus(Integrated("gpu-1", "pci-1"), Gpu("gpu-2", hardware: true, integrated: false, Missing<string>(gap))).WithDisplays(Display(gpu: "gpu-1")) with
        {
            PciDevices = List(Pci("pci-1")),
        };

        var result = Assert.Single(Check.Evaluate(facts, FixtureGate.Validated));

        Assert.Equal(Verdict.CannotDetermine, result.Verdict);
        Assert.Equal(["discrete-adapter-unattributed"], result.Missing.Select(info => info.Code));
    }

    [Fact]
    public void A_display_class_device_no_adapter_claims_blocks_not_applicable()
    {
        // A driverless or disabled card: PCI lists it, but no graphics adapter does.
        var facts = Desktop().WithGpus(Integrated("gpu-1", "pci-1")).WithDisplays(Display(gpu: "gpu-1"));

        var result = Assert.Single(Check.Evaluate(facts, FixtureGate.Validated));

        Assert.Equal(Verdict.CannotDetermine, result.Verdict);
        Assert.Equal(["unclaimed-display-device"], result.Missing.Select(info => info.Code));
        Assert.Equal(["/pciDevices/value/1/class"], result.Missing.Single().Facts);
    }

    [Fact]
    public void A_device_whose_class_was_not_reported_does_not_block_not_applicable()
    {
        var facts = Desktop().WithGpus(Integrated("gpu-1", "pci-1")).WithDisplays(Display(gpu: "gpu-1")) with
        {
            PciDevices = List(Pci("pci-1"), Pci("pci-2", Missing<PciDeviceClass>(FactGap.NotPresent))),
        };

        Assert.Equal(Verdict.NotApplicable, Assert.Single(Check.Evaluate(facts, FixtureGate.Validated)).Verdict);
    }

    public static TheoryData<string, Func<NormalizedSnapshot, NormalizedSnapshot>, string> MissingMachineFacts() => new()
    {
        { "session", facts => facts.WithSession(Missing<SessionType>(FactGap.SourceFailed)), "session-missing" },
        { "gpus", facts => facts with { Gpus = Missing<IReadOnlyList<GpuFacts>>(FactGap.TimedOut) }, "gpus-missing" },
        { "displays", facts => facts with { Displays = Missing<IReadOnlyList<DisplayFacts>>(FactGap.TimedOut) }, "displays-missing" },
    };

    /// <summary>Missing each required machine fact, one at a time: Couldn't check, never a Problem, an Ok or Not applicable.</summary>
    [Theory]
    [MemberData(nameof(MissingMachineFacts))]
    public void A_missing_required_machine_fact_is_couldnt_check(string name, Func<NormalizedSnapshot, NormalizedSnapshot> remove, string code)
    {
        var result = Assert.Single(Check.Evaluate(remove(Desktop().WithDisplays(Display(gpu: "gpu-1"))), TestGates.All));

        Assert.True(Verdict.CannotDetermine == result.Verdict, name);
        Assert.Contains(code, result.Missing.Select(info => info.Code));
    }

    [Fact]
    public void Missing_pci_devices_are_couldnt_check_when_they_are_needed_to_rule_out_a_hidden_card()
    {
        var facts = Desktop().WithGpus(Integrated("gpu-1", "pci-1")).WithDisplays(Display(gpu: "gpu-1")) with { PciDevices = Missing<IReadOnlyList<PciDeviceFacts>>(FactGap.SourceFailed) };

        var result = Assert.Single(Check.Evaluate(facts, FixtureGate.Validated));

        Assert.Equal(Verdict.CannotDetermine, result.Verdict);
        Assert.Equal(["pci-devices-missing"], result.Missing.Select(info => info.Code));
    }

    public static TheoryData<string, Func<DisplayFacts, DisplayFacts>, string> MissingDisplayFacts() => new()
    {
        { "gpu", display => display with { Gpu = Missing<string>(FactGap.Ambiguous) }, "display-gpu-missing" },
        { "connection", display => display with { Connection = Missing<ConnectionKind>(FactGap.SourceFailed) }, "connection-missing" },
        { "software", display => display with { Gpu = Known("gpu-3") }, "driving-adapter-software" },
    };

    [Theory]
    [MemberData(nameof(MissingDisplayFacts))]
    public void A_display_whose_driving_adapter_is_not_established_is_couldnt_check(string name, Func<DisplayFacts, DisplayFacts> change, string code)
    {
        var result = Single(Desktop().WithDisplays(change(Display(gpu: "gpu-1"))), TestGates.All);

        Assert.True(Verdict.CannotDetermine == result.Verdict, name);
        Assert.Equal([code], result.Missing.Select(info => info.Code));
    }

    [Fact]
    public void A_chassis_type_declared_by_firmware_supports_confirmed_as_a_declaration()
    {
        var result = Single(Desktop().WithDisplays(Display(gpu: "gpu-1")), TestGates.All);

        var chassis = result.Evidence.Single(item => item.FactPath == "/machine/chassisType");
        Assert.Equal((EvidenceRole.Applicability, Basis.Declared), (chassis.Role, chassis.Basis!.Value));
        Assert.Equal(Certainty.Confirmed, result.Certainty); // only because the test gate unlocked problem/desktop
    }

    private static CheckResult Single(NormalizedSnapshot facts, FixtureGate? gate = null) =>
        Assert.Single(Check.Evaluate(facts, gate ?? FixtureGate.Validated));

    private static (Verdict, Certainty?, string?) Outcome(CheckResult result) => (result.Verdict, result.Certainty, result.Condition);

    private static CheckValue Param(CheckResult result, string name) => result.Params.Single(value => value.Name == name).Value;
}
