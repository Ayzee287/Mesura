using FpsUtility.Core.Facts;
using FpsUtility.Core.Raw;

namespace FpsUtility.Core.Checks;

/// <summary>
/// Check 2, <c>display.on-integrated-gpu</c> (architecture §6.5, §4.6): on a desktop with a discrete GPU, is each
/// active external display driven by the integrated adapter?
/// </summary>
/// <remarks>
/// The finding is about the display path the facts show: which adapter drives the output. It says nothing about
/// where games render (the render adapter isn't observed), whether the discrete GPU has display outputs, whether it
/// is used at all, or whether the setup is intended.
/// </remarks>
public sealed class OnIntegratedGpuCheck : ICheck
{
    /// <summary>The aspect evaluated: the kind of adapter driving the display (Decision C).</summary>
    public const string FindingCode = "driving-gpu-kind";

    private enum AdapterKind
    {
        Software,
        Integrated,

        /// <summary>Hardware, not integrated, with a known PCI function of its identity.</summary>
        Discrete,

        /// <summary>Hardware, not integrated, with no established PCI function: a USB or BMC adapter, or one of two identical cards.</summary>
        Unattributed,

        /// <summary>A flag the classification needs is missing.</summary>
        Unknown,
    }

    public string Id => CheckIds.OnIntegratedGpu;

    public IReadOnlyList<CheckResult> Evaluate(NormalizedSnapshot facts, FixtureGate gate)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(gate);

        var kinds = facts.Gpus.IsKnown ? facts.Gpus.Value!.Select(Classify).ToList() : null;

        if (NotApplicable(facts, kinds) is { } notApplicable)
        {
            return [notApplicable];
        }

        if (Undetermined(facts, kinds) is { } undetermined)
        {
            return [undetermined];
        }

        // It applies: a local session, an established form factor, known adapters with at least one discrete GPU,
        // and at least one active display path.
        var classified = kinds!;
        if (classified.All(kind => kind != AdapterKind.Discrete))
        {
            throw new InvalidOperationException("Check 2 reached its verdicts with no discrete GPU; the applicability rules are inconsistent.");
        }

        return [.. facts.Displays.Value!.Select((display, index) => Evaluate(facts, classified, display, index, gate))];
    }

    private static AdapterKind Classify(GpuFacts gpu) =>
        !gpu.IsHardware.IsKnown ? AdapterKind.Unknown
        : !gpu.IsHardware.Value ? AdapterKind.Software
        : !gpu.IsIntegrated.IsKnown ? AdapterKind.Unknown
        : gpu.IsIntegrated.Value ? AdapterKind.Integrated
        : gpu.PciDevice.IsKnown ? AdapterKind.Discrete
        : AdapterKind.Unattributed;

    /// <summary>
    /// Not applicable when any known fact decides it; every reason that holds is traced. A missing fact never decides
    /// it: an unknown chassis is never taken for a laptop, and a battery alone never makes one.
    /// </summary>
    private CheckResult? NotApplicable(NormalizedSnapshot facts, List<AdapterKind>? kinds)
    {
        var result = Machine();
        var any = false;

        var session = facts.Os.Session;
        if (session is { IsKnown: true, Value: SessionType.Remote })
        {
            result.Add(EvidenceRole.Applicability, FactPaths.Session, session).Step(TraceKind.Applicability, "remote-session");
            any = true;
        }

        var chassis = facts.Machine.ChassisType;
        if (chassis.IsKnown && Policies.FormFactorOf(chassis.Value) == FormFactor.Portable)
        {
            result.Add(EvidenceRole.Applicability, FactPaths.ChassisType, chassis)
                .Step(TraceKind.Applicability, "portable-chassis", new NamedValue("chassis", CheckValue.Of(chassis.Value)));
            any = true;
        }

        if (NoDiscreteGpu(facts, kinds))
        {
            var gpus = facts.Gpus.Value!;
            for (var index = 0; index < gpus.Count; index++)
            {
                result.Add(EvidenceRole.Applicability, FactPaths.Gpu(index, nameof(GpuFacts.IsHardware)), gpus[index].IsHardware);
                if (kinds![index] == AdapterKind.Integrated)
                {
                    result.Add(EvidenceRole.Applicability, FactPaths.Gpu(index, nameof(GpuFacts.IsIntegrated)), gpus[index].IsIntegrated);
                    if (gpus[index].PciDevice.IsKnown)
                    {
                        result.Add(EvidenceRole.Applicability, FactPaths.Gpu(index, nameof(GpuFacts.PciDevice)), gpus[index].PciDevice);
                    }
                }
            }

            foreach (var index in DisplayClassDevices(facts))
            {
                result.Add(EvidenceRole.Applicability, FactPaths.PciDevice(index, nameof(PciDeviceFacts.Class)), facts.PciDevices.Value![index].Class);
            }

            result.Step(TraceKind.Applicability, "no-discrete-gpu");
            any = true;
        }

        var displays = facts.Displays;
        if (displays is { IsKnown: true, Value.Count: 0 })
        {
            result.Add(EvidenceRole.Applicability, FactPaths.Displays, displays).Step(TraceKind.Applicability, "no-active-display-path");
            any = true;
        }

        return any ? result.NotApplicable() : null;
    }

    /// <summary>
    /// No discrete GPU, established: every adapter classified, none discrete or unattributed, and every known
    /// display-class PCI device claimed by an adapter (a driverless or disabled card would otherwise hide).
    /// </summary>
    private static bool NoDiscreteGpu(NormalizedSnapshot facts, List<AdapterKind>? kinds) =>
        kinds is not null
        && kinds.All(kind => kind is AdapterKind.Software or AdapterKind.Integrated)
        && facts.PciDevices.IsKnown
        && UnclaimedDisplayDevices(facts).Count == 0;

    /// <summary>Machine-level Couldn't check: every reason is named; any one is enough.</summary>
    private CheckResult? Undetermined(NormalizedSnapshot facts, List<AdapterKind>? kinds)
    {
        var result = Machine();
        RequireLocalSession(result, facts.Os.Session);

        if (!facts.Gpus.IsKnown)
        {
            result.Require(EvidenceRole.Applicability, FactPaths.Gpus, facts.Gpus, "gpus-missing");
        }

        if (!facts.Displays.IsKnown)
        {
            result.Require(EvidenceRole.Applicability, FactPaths.Displays, facts.Displays, "displays-missing");
        }

        var anyDiscrete = kinds?.Contains(AdapterKind.Discrete) == true;
        if (!facts.PciDevices.IsKnown && !anyDiscrete)
        {
            result.Require(EvidenceRole.Applicability, FactPaths.PciDevices, facts.PciDevices, "pci-devices-missing");
        }

        var chassis = facts.Machine.ChassisType;
        var battery = facts.Power.BatteryPresent;
        if (Policies.FormFactorOf(chassis) == FormFactor.Unknown && battery is not { IsKnown: true, Value: false })
        {
            // An unknown chassis with a battery (or none known) may be a laptop, or a desktop on a UPS: undecided.
            result.Add(EvidenceRole.Applicability, FactPaths.ChassisType, chassis)
                .Add(EvidenceRole.Applicability, FactPaths.BatteryPresent, battery)
                .Missing("form-factor-undetermined", FactPaths.ChassisType, FactPaths.BatteryPresent);
        }

        if (kinds is not null)
        {
            var gpus = facts.Gpus.Value!;
            var unknown = new List<string>();
            var unattributed = new List<string>();
            for (var index = 0; index < gpus.Count; index++)
            {
                if (kinds[index] == AdapterKind.Unknown)
                {
                    var (path, flag) = gpus[index].IsHardware.IsKnown
                        ? (FactPaths.Gpu(index, nameof(GpuFacts.IsIntegrated)), gpus[index].IsIntegrated)
                        : (FactPaths.Gpu(index, nameof(GpuFacts.IsHardware)), gpus[index].IsHardware);
                    result.Add(EvidenceRole.Applicability, path, flag);
                    unknown.Add(path);
                }
                else if (kinds[index] == AdapterKind.Unattributed)
                {
                    var path = FactPaths.Gpu(index, nameof(GpuFacts.PciDevice));
                    result.Add(EvidenceRole.Applicability, path, gpus[index].PciDevice);
                    unattributed.Add(path);
                }
            }

            if (unknown.Count > 0)
            {
                result.Missing("adapter-kind-missing", unknown);
            }

            if (unattributed.Count > 0)
            {
                // It may be a discrete GPU (one of two identical cards), a USB display adapter or a server's BMC.
                result.Missing("discrete-adapter-unattributed", unattributed);
            }

            if (!anyDiscrete && facts.PciDevices.IsKnown && UnclaimedDisplayDevices(facts) is { Count: > 0 } unclaimed)
            {
                var paths = unclaimed.Select(index => FactPaths.PciDevice(index, nameof(PciDeviceFacts.Class))).ToList();
                foreach (var (index, path) in unclaimed.Zip(paths))
                {
                    result.Add(EvidenceRole.Applicability, path, facts.PciDevices.Value![index].Class);
                }

                result.Missing("unclaimed-display-device", paths);
            }
        }

        return result.IsMissingSomething ? result.CannotDetermine() : null;
    }

    private CheckResult Evaluate(NormalizedSnapshot facts, List<AdapterKind> kinds, DisplayFacts display, int index, FixtureGate gate)
    {
        var result = new ResultBuilder(Id, FindingCode, display.Id);
        var connection = display.Connection;
        var connectionAt = FactPaths.Display(index, nameof(DisplayFacts.Connection));
        var kind = connection.IsKnown ? Policies.KindOf(connection.Value) : (DisplayKind?)null;
        if (kind is DisplayKind.Virtual or DisplayKind.Internal)
        {
            return result
                .Add(EvidenceRole.Applicability, connectionAt, connection)
                .Step(TraceKind.Applicability, kind == DisplayKind.Virtual ? "virtual-display" : "internal-display", new NamedValue("connection", CheckValue.Of(connection.Value)))
                .NotApplicable();
        }

        var gpuAt = FactPaths.Display(index, nameof(DisplayFacts.Gpu));
        result.Require(EvidenceRole.Applicability, connectionAt, connection, "connection-missing");
        result.Require(EvidenceRole.Premise, gpuAt, display.Gpu, "display-gpu-missing");
        if (result.IsMissingSomething)
        {
            return result.CannotDetermine();
        }

        var gpus = facts.Gpus.Value!;
        var driving = IndexOf(gpus, display.Gpu.Value!);
        var hardwareAt = FactPaths.Gpu(driving, nameof(GpuFacts.IsHardware));
        if (kinds[driving] == AdapterKind.Software)
        {
            return result
                .Add(EvidenceRole.Premise, hardwareAt, gpus[driving].IsHardware)
                .Missing("driving-adapter-software", gpuAt, hardwareAt)
                .CannotDetermine();
        }

        result.Step(TraceKind.Applicability, kind == DisplayKind.External ? "external-display" : "unrecognised-display", new NamedValue("connection", CheckValue.Of(connection.Value)));
        if (kind == DisplayKind.Unrecognised)
        {
            result.Step(TraceKind.Downgrade, "connection-unrecognised");
        }

        AddMachine(result, facts);

        // The discrete GPUs make the check apply; the driving adapter's own flags are what the verdict rests on.
        var discrete = Enumerable.Range(0, gpus.Count).Where(gpu => kinds[gpu] == AdapterKind.Discrete).ToList();
        var discreteIds = CheckValue.Symbols(discrete.Select(gpu => gpus[gpu].Id));
        foreach (var gpu in discrete)
        {
            var role = gpu == driving ? EvidenceRole.Premise : EvidenceRole.Applicability;
            result.Add(role, FactPaths.Gpu(gpu, nameof(GpuFacts.IsHardware)), gpus[gpu].IsHardware)
                .Add(role, FactPaths.Gpu(gpu, nameof(GpuFacts.IsIntegrated)), gpus[gpu].IsIntegrated)
                .Add(role, FactPaths.Gpu(gpu, nameof(GpuFacts.PciDevice)), gpus[gpu].PciDevice);
        }

        result.Step(TraceKind.Applicability, "discrete-gpu-present", new NamedValue("gpus", discreteIds));

        var verdict = kinds[driving] == AdapterKind.Integrated ? Verdict.Problem : Verdict.Ok;
        if (verdict == Verdict.Problem)
        {
            result.Add(EvidenceRole.Premise, hardwareAt, gpus[driving].IsHardware)
                .Add(EvidenceRole.Premise, FactPaths.Gpu(driving, nameof(GpuFacts.IsIntegrated)), gpus[driving].IsIntegrated);
        }

        result.Step(TraceKind.Comparison, FindingCode,
            new NamedValue("drivingGpu", CheckValue.Symbol(display.Gpu.Value!)),
            new NamedValue("kind", CheckValue.Symbol(verdict == Verdict.Problem ? "integrated" : "discrete")));

        if (verdict == Verdict.Problem)
        {
            AddOtherDisplays(result, facts, kinds, index);
        }

        result.Param("drivingGpu", CheckValue.Symbol(display.Gpu.Value!))
            .Param("discreteGpus", discreteIds)
            .Param("connection", CheckValue.Of(connection.Value));

        return result.Decide(verdict, "desktop", gate);
    }

    /// <summary>The form factor and power facts every decided result rests on, or is lowered by.</summary>
    private static void AddMachine(ResultBuilder result, NormalizedSnapshot facts)
    {
        RequireLocalSession(result, facts.Os.Session);

        var chassis = facts.Machine.ChassisType;
        NamedValue[] declared = chassis.IsKnown ? [new NamedValue("chassis", CheckValue.Of(chassis.Value))] : [];
        switch (Policies.FormFactorOf(chassis))
        {
            case FormFactor.FullDesktop:
                result.Add(EvidenceRole.Applicability, FactPaths.ChassisType, chassis).Step(TraceKind.Applicability, "desktop-chassis", declared);
                break;
            case FormFactor.Compact:
                // §6.3: an OEM small form factor or all-in-one may be wired for hybrid graphics by design.
                result.Downgrade(FactPaths.ChassisType, chassis, "compact-chassis", declared);
                break;
            default:
                // Only reached with no battery reported; the chassis alone doesn't make it a desktop.
                result.Downgrade(FactPaths.ChassisType, chassis, "chassis-unknown", declared);
                break;
        }

        var battery = facts.Power.BatteryPresent;
        if (battery is { IsKnown: true, Value: false })
        {
            result.Add(EvidenceRole.Applicability, FactPaths.BatteryPresent, battery).Step(TraceKind.Applicability, "no-battery-reported");
        }
        else
        {
            // A desktop case with a battery: most likely a UPS that reports as one. It can only lower certainty.
            result.Downgrade(FactPaths.BatteryPresent, battery, battery.IsKnown ? "chassis-battery-inconsistent" : "battery-unknown");
        }
    }

    /// <summary>
    /// For a Problem: the other displays' adapters. A discrete GPU driving one of them makes this display plausibly an
    /// intended secondary screen; one whose adapter is unknown leaves "the discrete GPU drives nothing" unshown.
    /// </summary>
    private static void AddOtherDisplays(ResultBuilder result, NormalizedSnapshot facts, List<AdapterKind> kinds, int index)
    {
        var displays = facts.Displays.Value!;
        var gpus = facts.Gpus.Value!;
        var onDiscrete = new List<string>();
        var unattributed = new List<string>();
        for (var other = 0; other < displays.Count; other++)
        {
            if (other == index)
            {
                continue;
            }

            var gpu = displays[other].Gpu;
            var path = FactPaths.Display(other, nameof(DisplayFacts.Gpu));
            if (!gpu.IsKnown)
            {
                result.Add(EvidenceRole.Downgrade, path, gpu);
                unattributed.Add(displays[other].Id);
            }
            else if (kinds[IndexOf(gpus, gpu.Value!)] == AdapterKind.Discrete)
            {
                result.Add(EvidenceRole.Downgrade, path, gpu);
                onDiscrete.Add(displays[other].Id);
            }
            else
            {
                result.Add(EvidenceRole.Premise, path, gpu);
            }
        }

        if (onDiscrete.Count > 0)
        {
            result.Step(TraceKind.Downgrade, "discrete-gpu-drives-other-display", new NamedValue("displays", CheckValue.Symbols(onDiscrete)));
        }

        if (unattributed.Count > 0)
        {
            result.Step(TraceKind.Downgrade, "display-attribution-incomplete", new NamedValue("displays", CheckValue.Symbols(unattributed)));
        }
    }

    private static List<int> UnclaimedDisplayDevices(NormalizedSnapshot facts)
    {
        var claimed = facts.Gpus.Value!.Where(gpu => gpu.PciDevice.IsKnown).Select(gpu => gpu.PciDevice.Value!).ToHashSet(StringComparer.Ordinal);
        return [.. DisplayClassDevices(facts).Where(index => !claimed.Contains(facts.PciDevices.Value![index].Id))];
    }

    /// <summary>PCI devices whose class is known to be display. One whose class wasn't reported isn't counted: an iGPU's host bridge often has none.</summary>
    private static IEnumerable<int> DisplayClassDevices(NormalizedSnapshot facts) =>
        facts.PciDevices.Value!
            .Select((device, index) => (device, index))
            .Where(pair => pair.device.Class is { IsKnown: true, Value: PciDeviceClass.Display })
            .Select(pair => pair.index);

    private static int IndexOf(IReadOnlyList<GpuFacts> gpus, string id)
    {
        for (var index = 0; index < gpus.Count; index++)
        {
            if (gpus[index].Id == id)
            {
                return index;
            }
        }

        throw new InvalidOperationException($"The facts name GPU '{id}', which isn't in their GPU list.");
    }

    private static void RequireLocalSession(ResultBuilder result, Fact<SessionType> session)
    {
        if (result.Require(EvidenceRole.Applicability, FactPaths.Session, session, "session-missing"))
        {
            result.Step(TraceKind.Applicability, "local-session");
        }
    }

    private ResultBuilder Machine() => new(Id, FindingCode, ResultBuilder.Machine);
}
