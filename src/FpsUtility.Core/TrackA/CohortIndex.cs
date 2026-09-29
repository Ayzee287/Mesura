using System.Globalization;
using System.Text.Json.Serialization;
using FpsUtility.Core.Raw;
using FpsUtility.Core.Raw.Serialization;

namespace FpsUtility.Core.TrackA;

/// <summary>An accepted session as the corpus holds it: its manifest, the operator's answers and its acceptance record.</summary>
public sealed record AcceptedSession(CaptureManifest Manifest, OperatorObservations Observations, AcceptanceRecord Acceptance)
{
    /// <summary>
    /// The scenario status after acceptance: the capture's status, except that a step counts as needing manual evidence
    /// when, for a tool it expects, no evidence file of that step was accepted by the person who reviewed it.
    /// </summary>
    public ScenarioStatus Status
    {
        get
        {
            var captured = Manifest.Scenario.Status;
            if (captured != ScenarioStatus.Completed)
            {
                return captured;
            }

            var step = ScenarioCatalog.Require(Manifest.Scenario.Id).Step(Manifest.Scenario.Step);
            var accepted = Acceptance.Evidence
                .Where(record => record.Decision == EvidenceDecision.Accepted)
                .Select(record => EvidenceConvention.Read(record.File))
                .OfType<EvidenceName>()
                .Where(name => EvidenceConvention.BelongsTo(name.State, step.Name))
                .ToList();
            return step.Evidence.All(expected => accepted.Any(name => name.Tool == expected.Tool)) ? captured : ScenarioStatus.NeedsManualEvidence;
        }
    }
}

/// <summary>How a scenario stands on one machine, from its accepted sessions.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<CoverageState>))]
public enum CoverageState
{
    Completed,
    NeedsManualEvidence,
    Incomplete,
    Unavailable,
    NotApplicable,
    Skipped,

    /// <summary>The PCIe spike has one of its two steps accepted but not the other.</summary>
    UnpairedStep,

    /// <summary>No accepted session.</summary>
    Missing,
}

/// <param name="Series">The PCIe spike's series (the idle session's id).</param>
public sealed record IndexedSession(
    string SessionId,
    string Scenario,
    ScenarioStatus Status,
    WindowsFamily Windows,
    int Build,
    string ProducerCommit,
    string AcceptedWithCommit,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Step = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Series = null);

/// <summary>What the machine's accepted sessions show, as sets in a closed vocabulary (no model strings, no names).</summary>
/// <param name="ChassisObserved">The operator's independent chassis label, from the baseline answers.</param>
/// <param name="DiscreteGpu">Yes when any session shows a discrete GPU; no when some session establishes there is none and none shows one; otherwise unknown.</param>
/// <param name="HighestOfferedHz">The highest progressive refresh any of its displays was offered, in any session.</param>
public sealed record MachineInventory(
    IReadOnlyList<WindowsFamily> Windows,
    IReadOnlyList<int> Builds,
    IReadOnlyList<DeclaredFormFactor> FormFactorDeclared,
    IReadOnlyList<string> ChassisObserved,
    TraitState DiscreteGpu,
    IReadOnlyList<GpuVendor> DiscreteGpuVendors,
    IReadOnlyList<GpuVendor> IntegratedGpuVendors,
    IReadOnlyList<MediaKind> DiskMedia,
    IReadOnlyList<BusKind> DiskBuses,
    IReadOnlyList<SteamState> Steam,
    IReadOnlyList<DisplayClass> DisplayKinds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? HighestOfferedHz = null);

public sealed record ScenarioCoverage(string Scenario, CoverageState State, int Sessions);

/// <summary>One of the owner's first cohort targets (spec §32) on one machine, and what met it (or didn't).</summary>
/// <param name="Basis">Tokens naming each condition and whether it came from the machine (<c>machine:</c>) or the operator (<c>operator:</c>); a missing one is prefixed <c>missing:</c>.</param>
public sealed record TargetResult(string Target, bool Met, IReadOnlyList<string> Basis);

public sealed record CohortMachine(
    string CohortId,
    IReadOnlyList<IndexedSession> Sessions,
    MachineInventory Inventory,
    IReadOnlyList<ScenarioCoverage> Scenarios,
    IReadOnlyList<CheckPath> CheckPaths,
    IReadOnlyList<TargetResult> Targets,
    IReadOnlyList<string> Warnings);

/// <summary>A count of machines with one value of one dimension, e.g. (<c>gpu</c>, <c>nvidia</c>, 2). Counts, never a score.</summary>
public sealed record Tally(string Dimension, string Value, int Machines);

/// <summary>
/// <c>cohort-index.json</c> (architecture §4.7): the Track A inventory, generated from the accepted sessions and never
/// edited by hand. It answers what the cohort covers (machines, GPU vendors, Windows, form factors, Steam, storage,
/// refresh, scenarios, real-hardware check paths, the owner's first targets) and holds only cohort ids, session ids,
/// commits and tokens from closed vocabularies: no serial, no model string, no name, no user text.
/// Engineering validation of known, manually enrolled PCs: not telemetry, not analytics, not statistics about users.
/// </summary>
public sealed record CohortIndex(int SchemaVersion, IReadOnlyList<Tally> Summary, IReadOnlyList<CohortMachine> Machines)
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>The owner's first cohort targets (spec §32), in the owner's order.</summary>
    public static IReadOnlyList<string> Targets { get; } =
    [
        "nvidiaDesktop", "amdDesktop", "desktopMotherboardOutput", "steamMultipleStorageKinds", "hddAndSsd",
        "windows11Above60Hz", "dynamicRefresh", "cloneMode", "windows10",
    ];

    public static CohortIndex Build(IEnumerable<AcceptedSession> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        var machines = sessions
            .GroupBy(session => session.Manifest.CohortId, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => Machine(group.Key, [.. group.OrderBy(session => session.Manifest.SessionId, StringComparer.Ordinal)]))
            .ToList();

        return new CohortIndex(CurrentSchemaVersion, Summarise(machines), machines);
    }

    private static CohortMachine Machine(string cohortId, IReadOnlyList<AcceptedSession> sessions)
    {
        var traits = sessions.Select(session => session.Acceptance.Traits).ToList();
        var gpus = traits.SelectMany(trait => trait.Gpus).ToList();
        var disks = traits.SelectMany(trait => trait.Disks).ToList();
        var displays = traits.SelectMany(trait => trait.Displays).ToList();

        var inventory = new MachineInventory(
            Set(traits.Select(trait => trait.Windows).OfType<WindowsFamily>()),
            [.. traits.Select(trait => trait.Build).OfType<int>().Distinct().Order()],
            Set(traits.Select(trait => trait.FormFactor)),
            [.. sessions.Select(session => session.Observations.Find(ObservationCatalog.PhysicalChassisType)?.Text).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            traits.Any(trait => trait.DiscreteGpu == TraitState.Yes) ? TraitState.Yes : traits.Any(trait => trait.DiscreteGpu == TraitState.No) ? TraitState.No : TraitState.Unknown,
            Set(gpus.Where(gpu => gpu.Hardware == TraitState.Yes && gpu.Integrated == TraitState.No).Select(gpu => gpu.Vendor)),
            Set(gpus.Where(gpu => gpu.Integrated == TraitState.Yes).Select(gpu => gpu.Vendor)),
            Set(disks.Select(disk => disk.Media).OfType<MediaKind>()),
            Set(disks.Select(disk => disk.Bus).OfType<BusKind>()),
            Set(traits.Select(trait => trait.Steam.Registration)),
            Set(displays.Select(display => display.Kind)),
            displays.Select(display => display.HighestOfferedHz).Max());

        var scenarios = ScenarioCatalog.All.Select(scenario => Coverage(scenario, sessions)).ToList();
        var warnings = new List<string>();
        if (traits.Select(trait => trait.ModelSignature).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            warnings.Add("modelSignatureDiffers");
        }

        return new CohortMachine(
            cohortId,
            [.. sessions.Select(session => new IndexedSession(
                session.Manifest.SessionId,
                session.Manifest.Scenario.Id,
                session.Status,
                session.Manifest.Platform.Family,
                session.Manifest.Platform.Build,
                session.Manifest.Producer.Commit!,
                session.Acceptance.AcceptedWith.Commit!,
                session.Manifest.Scenario.Step,
                session.Manifest.Scenario.Series?.Id))],
            inventory,
            scenarios,
            CheckPath.Distinct(sessions.SelectMany(session => session.Acceptance.CheckPaths)),
            [.. TargetsOf(inventory, sessions, scenarios)],
            warnings);
    }

    private static ScenarioCoverage Coverage(Scenario scenario, IReadOnlyList<AcceptedSession> sessions)
    {
        var mine = sessions.Where(session => session.Manifest.Scenario.Id == scenario.Id).ToList();
        if (mine.Count == 0)
        {
            return new ScenarioCoverage(scenario.Id, CoverageState.Missing, 0);
        }

        if (scenario.Steps.Count == 1)
        {
            return new ScenarioCoverage(scenario.Id, mine.Select(session => StateOf(session.Status)).Min(), mine.Count);
        }

        // A two-step scenario counts per series: both steps accepted, and the series is as good as its weaker step. A series
        // the operator declared not applicable or skipped at its first step has no second step, and keeps that declaration.
        var best = mine
            .GroupBy(session => session.Manifest.Scenario.Series!.Id, StringComparer.Ordinal)
            .Select(series => scenario.Steps.All(step => series.Any(session => session.Manifest.Scenario.Step == step.Name))
                ? scenario.Steps.Select(step => series.Where(session => session.Manifest.Scenario.Step == step.Name).Select(session => StateOf(session.Status)).Min()).Max()
                : series.All(session => session.Status is ScenarioStatus.NotApplicable or ScenarioStatus.Skipped)
                    ? series.Select(session => StateOf(session.Status)).Min()
                    : CoverageState.UnpairedStep)
            .Min();

        return new ScenarioCoverage(scenario.Id, best, mine.Count);
    }

    private static CoverageState StateOf(ScenarioStatus status) => status switch
    {
        ScenarioStatus.Completed => CoverageState.Completed,
        ScenarioStatus.NeedsManualEvidence => CoverageState.NeedsManualEvidence,
        ScenarioStatus.Incomplete => CoverageState.Incomplete,
        ScenarioStatus.Unavailable => CoverageState.Unavailable,
        ScenarioStatus.NotApplicable => CoverageState.NotApplicable,
        ScenarioStatus.Skipped => CoverageState.Skipped,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    private static IEnumerable<TargetResult> TargetsOf(MachineInventory inventory, IReadOnlyList<AcceptedSession> sessions, IReadOnlyList<ScenarioCoverage> scenarios)
    {
        var declaredDesktop = inventory.FormFactorDeclared.Contains(DeclaredFormFactor.FullDesktop);
        var operatorDesktop = inventory.ChassisObserved.Contains("desktop");
        string Desktop() => declaredDesktop ? "machine:fullDesktop" : operatorDesktop ? "operator:desktop" : "missing:desktop";
        bool IsDesktop() => declaredDesktop || operatorDesktop;
        bool Completed(string scenario) => scenarios.Single(coverage => coverage.Scenario == scenario).State == CoverageState.Completed;
        IEnumerable<AcceptedSession> CompletedSessions(string scenario) =>
            sessions.Where(session => session.Manifest.Scenario.Id == scenario && session.Status == ScenarioStatus.Completed);
        static string Part(bool met, string token) => (met ? "" : "missing:") + token;

        foreach (var (target, vendor) in new[] { ("nvidiaDesktop", GpuVendor.Nvidia), ("amdDesktop", GpuVendor.Amd) })
        {
            var gpu = inventory.DiscreteGpuVendors.Contains(vendor);
            yield return new(target, gpu && IsDesktop(), [Part(gpu, "machine:discreteGpu:" + Name(vendor)), Desktop()]);
        }

        {
            var session = CompletedSessions(ScenarioCatalog.MotherboardDisplay).ToList();
            var cabled = session.Any(item => item.Observations.Find(ObservationCatalog.MonitorCabling)?.Text is "motherboard" or "both");
            var onIntegrated = session.Any(item => item.Acceptance.Traits.Displays.Any(display => display.Kind == DisplayClass.External && display.OnIntegratedGpu == TraitState.Yes));
            yield return new("desktopMotherboardOutput", IsDesktop() && cabled && onIntegrated,
                [Desktop(), Part(cabled, "operator:monitorCabledToMotherboard"), Part(onIntegrated, "machine:externalDisplayOnIntegratedGpu")]);
        }

        // Storage targets count internal drives of a known kind only: never the kit's own USB stick, a virtual disk, or a
        // disk whose media or bus is missing.
        var internalKinds = sessions.SelectMany(session => session.Acceptance.Traits.Disks).Select(InternalKind).OfType<string>().Distinct(StringComparer.Ordinal).ToList();

        {
            var steam = inventory.Steam.Contains(SteamState.Registered);
            var kinds = internalKinds.Count >= 2;
            yield return new("steamMultipleStorageKinds", steam && kinds, [Part(steam, "machine:steamRegistered"), Part(kinds, "machine:twoOrMoreInternalDriveKinds")]);
        }

        {
            var hdd = internalKinds.Contains("hdd");
            var ssd = internalKinds.Any(kind => kind is "sataSsd" or "nvmeSsd" or "ssd");
            yield return new("hddAndSsd", hdd && ssd, [Part(hdd, "machine:internalHdd"), Part(ssd, "machine:internalSsd")]);
        }

        {
            var windows11 = inventory.Windows.Contains(WindowsFamily.Windows11);
            var above60 = inventory.HighestOfferedHz > 60;
            yield return new("windows11Above60Hz", windows11 && above60, [Part(windows11, "machine:windows11"), Part(above60, "machine:displayOfferedAbove60Hz")]);
        }

        {
            var completed = Completed(ScenarioCatalog.DisplayHighRefresh);
            var declared = CompletedSessions(ScenarioCatalog.DisplayHighRefresh).Any(session => session.Observations.Find(ObservationCatalog.DynamicRefreshIntended)?.Text == "yes");
            yield return new("dynamicRefresh", completed && declared,
                [Part(completed, "scenario:displayHighRefreshCompleted"), Part(declared, "operator:dynamicRefreshOn"), "machine:notObservable"]);
        }

        {
            var completed = Completed(ScenarioCatalog.Clone);
            var shared = CompletedSessions(ScenarioCatalog.Clone).Any(session => session.Acceptance.Traits.Displays.Any(display => display.SharesSource == TraitState.Yes));
            yield return new("cloneMode", completed && shared, [Part(completed, "scenario:cloneCompleted"), Part(shared, "machine:sharedSource")]);
        }

        {
            var windows10 = inventory.Windows.Contains(WindowsFamily.Windows10);
            yield return new("windows10", windows10, [Part(windows10, "machine:windows10")]);
        }
    }

    /// <summary>An internal drive's physical kind, or <c>null</c> when it isn't internal or its media or bus is unknown.</summary>
    private static string? InternalKind(DiskTrait disk) => (disk.Media, disk.Bus) switch
    {
        (null, _) or (_, null) or (MediaKind.Unspecified, _) or (MediaKind.Scm, _) => null,
        (_, BusKind.Usb or BusKind.FileBackedVirtual or BusKind.StorageSpaces or BusKind.Unknown or BusKind.Iscsi or BusKind.Sd or BusKind.Mmc or BusKind.Ieee1394) => null,
        (MediaKind.Hdd, _) => "hdd",
        (MediaKind.Ssd, BusKind.Nvme) => "nvmeSsd",
        (MediaKind.Ssd, BusKind.Sata or BusKind.Ata or BusKind.Atapi) => "sataSsd",
        (MediaKind.Ssd, _) => "ssd",
        _ => null,
    };

    private static IReadOnlyList<Tally> Summarise(IReadOnlyList<CohortMachine> machines)
    {
        var tallies = new List<Tally> { new("machines", "enrolled", machines.Count) };
        void Count(string dimension, string value, Func<CohortMachine, bool> predicate) => tallies.Add(new(dimension, value, machines.Count(predicate)));

        foreach (var vendor in new[] { GpuVendor.Nvidia, GpuVendor.Amd, GpuVendor.Intel, GpuVendor.Other })
        {
            Count("discreteGpu", Name(vendor), machine => machine.Inventory.DiscreteGpuVendors.Contains(vendor));
        }

        Count("discreteGpu", "none", machine => machine.Inventory.DiscreteGpu == TraitState.No);
        Count("discreteGpu", "unknown", machine => machine.Inventory.DiscreteGpu == TraitState.Unknown);
        foreach (var family in Enum.GetValues<WindowsFamily>())
        {
            Count("windows", Name(family), machine => machine.Inventory.Windows.Contains(family));
        }

        foreach (var form in Enum.GetValues<DeclaredFormFactor>())
        {
            Count("formFactorDeclared", Name(form), machine => machine.Inventory.FormFactorDeclared.Contains(form));
        }

        foreach (var chassis in ObservationCatalog.PhysicalChassisType.Choices)
        {
            Count("chassisObserved", chassis, machine => machine.Inventory.ChassisObserved.Contains(chassis));
        }

        foreach (var steam in Enum.GetValues<SteamState>())
        {
            Count("steam", Name(steam), machine => machine.Inventory.Steam.Contains(steam));
        }

        foreach (var media in Enum.GetValues<MediaKind>())
        {
            Count("diskMedia", Name(media), machine => machine.Inventory.DiskMedia.Contains(media));
        }

        foreach (var bus in new[] { BusKind.Nvme, BusKind.Sata, BusKind.Usb, BusKind.Raid, BusKind.StorageSpaces })
        {
            Count("diskBus", Name(bus), machine => machine.Inventory.DiskBuses.Contains(bus));
        }

        Count("display", "offeredAbove60Hz", machine => machine.Inventory.HighestOfferedHz > 60);
        Count("display", "external", machine => machine.Inventory.DisplayKinds.Contains(DisplayClass.External));

        foreach (var scenario in ScenarioCatalog.All)
        {
            foreach (var state in Enum.GetValues<CoverageState>())
            {
                var count = machines.Count(machine => machine.Scenarios.Single(coverage => coverage.Scenario == scenario.Id).State == state);
                if (count > 0 || state is CoverageState.Completed or CoverageState.Missing)
                {
                    tallies.Add(new("scenario:" + scenario.Id, Name(state), count));
                }
            }
        }

        foreach (var target in Targets)
        {
            Count("target", target, machine => machine.Targets.Single(result => result.Target == target).Met);
        }

        return tallies;
    }

    private static IReadOnlyList<T> Set<T>(IEnumerable<T> values)
        where T : struct, Enum => [.. values.Distinct().Order()];

    internal static string Name<TEnum>(TEnum value)
        where TEnum : struct, Enum => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    /// <summary>The build a number is shown with, e.g. <c>26200</c>.</summary>
    internal static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
