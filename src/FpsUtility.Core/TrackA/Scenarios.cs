using System.Text.Json.Serialization;
using FpsUtility.Core.Raw.Serialization;

namespace FpsUtility.Core.TrackA;

/// <summary>A raw source by its name in the snapshot's <c>sources</c> (architecture §4.4).</summary>
public static class RawSources
{
    public const string Display = "display";
    public const string Edid = "edid";
    public const string Adapters = "adapters";
    public const string Smbios = "smbios";
    public const string Pcie = "pcie";
    public const string Storage = "storage";
    public const string Steam = "steam";
    public const string Power = "power";

    public static IReadOnlyList<string> All { get; } = [Display, Edid, Adapters, Smbios, Pcie, Storage, Steam, Power];
}

/// <summary>
/// What the operator declared about a scenario before the scan: carried out, not applicable to this PC, or skipped.
/// A declaration only; the machine's own evidence is recorded beside it.
/// </summary>
[JsonConverter(typeof(CamelCaseEnumConverter<ScenarioDisposition>))]
public enum ScenarioDisposition
{
    Performed,
    NotApplicable,
    Skipped,
}

/// <summary>A scenario's outcome in one session (architecture §4.7).</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<ScenarioStatus>))]
public enum ScenarioStatus
{
    /// <summary>Carried out, its required sources read, its procedure confirmed, its expected evidence supplied.</summary>
    Completed,

    /// <summary>The operator declared it doesn't apply to this PC.</summary>
    NotApplicable,

    /// <summary>The operator chose not to carry it out this time.</summary>
    Skipped,

    /// <summary>Carried out, but a source it needs couldn't be read (machine-observed).</summary>
    Unavailable,

    /// <summary>Carried out, but the operator didn't confirm a procedure step (stable gameplay, the game in the foreground, an idle PC).</summary>
    Incomplete,

    /// <summary>Carried out, but external evidence it expects wasn't supplied.</summary>
    NeedsManualEvidence,
}

/// <summary>External evidence a scenario step expects: at least one file from the tool (<see cref="EvidenceConvention"/>).</summary>
public sealed record ExpectedEvidence(EvidenceTool Tool);

/// <summary>One capture within a scenario. Most scenarios have one; the PCIe spike has an idle and a load step.</summary>
/// <param name="Name"><c>null</c> for a single-step scenario, else <c>idle</c> or <c>load</c>.</param>
/// <param name="Setup">What the operator does before the scan, one instruction per line. <c>{evidence}</c> stands for the session's evidence folder.</param>
/// <param name="InGame">The scan runs while a game is played: the kit waits for readiness, counts down and beeps at the start and end.</param>
/// <param name="Fields">The questions asked after the scan (the consent, when game names would be kept, and the note are added to every step).</param>
/// <param name="AfterScan">What the operator does after the scan, before the questions (screenshots, the probe).</param>
public sealed record ScenarioStep(
    string? Name,
    IReadOnlyList<string> Setup,
    bool InGame,
    IReadOnlyList<ObservationField> Fields,
    IReadOnlyList<ExpectedEvidence> Evidence,
    IReadOnlyList<string> AfterScan);

/// <param name="Id">The scenario id (kebab-case), as the owner's Track A plan names it.</param>
/// <param name="RequiredSources">Raw sources the scenario can't be judged without; one that is unavailable makes the session <see cref="ScenarioStatus.Unavailable"/>.</param>
public sealed record Scenario(string Id, string Summary, IReadOnlyList<string> RequiredSources, IReadOnlyList<ScenarioStep> Steps)
{
    public ScenarioStep Step(string? name) =>
        Steps.FirstOrDefault(step => step.Name == name) ?? throw new ArgumentException($"Scenario {Id} has no step '{name}'.", nameof(name));

    /// <summary>
    /// The session's status, in this order: a declared not-applicable or skip; a required source the machine couldn't
    /// read (machine-observed); a procedure step the operator didn't confirm; expected evidence that wasn't supplied;
    /// otherwise completed. The operator's declaration never outranks what the machine shows.
    /// </summary>
    public static ScenarioStatus StatusOf(ScenarioDisposition disposition, bool requiredSourceUnavailable, bool proceduresConfirmed, bool evidenceComplete) =>
        disposition switch
        {
            ScenarioDisposition.NotApplicable => ScenarioStatus.NotApplicable,
            ScenarioDisposition.Skipped => ScenarioStatus.Skipped,
            ScenarioDisposition.Performed when requiredSourceUnavailable => ScenarioStatus.Unavailable,
            ScenarioDisposition.Performed when !proceduresConfirmed => ScenarioStatus.Incomplete,
            ScenarioDisposition.Performed when !evidenceComplete => ScenarioStatus.NeedsManualEvidence,
            ScenarioDisposition.Performed => ScenarioStatus.Completed,
            _ => throw new ArgumentOutOfRangeException(nameof(disposition), disposition, null),
        };
}

/// <summary>The Track A scenarios (architecture §4.7). None of them is a benchmark, and none launches anything.</summary>
public static class ScenarioCatalog
{
    public const string Baseline = "baseline";
    public const string GameLoad = "game-load";
    public const string DisplayHighRefresh = "display-high-refresh";
    public const string Clone = "clone";
    public const string MotherboardDisplay = "motherboard-display";
    public const string PcieIdleLoad = "pci-e-idle-load";
    public const string SteamStorage = "steam-storage";

    public const string IdleStep = "idle";
    public const string LoadStep = "load";

    private static readonly string[] GameSetup =
    [
        "Start the game you want to use yourself (the kit never launches anything). Use windowed or borderless mode.",
        "Play until you are in stable gameplay: not a menu, a loading screen or a cut-scene.",
        "Then come back to this window, press Enter and return to the game at once. A beep marks the start of the scan and",
        "another its end: keep the game in the foreground, and keep playing, between the two.",
    ];

    private static readonly ObservationField[] GameFields =
    [
        ObservationCatalog.GameName, ObservationCatalog.GameDisplayMode, ObservationCatalog.StableGameplayReached, ObservationCatalog.GameStayedInForeground,
    ];

    private static readonly ExpectedEvidence[] PcieEvidence = [new(EvidenceTool.GpuZ), new(EvidenceTool.Hwinfo), new(EvidenceTool.PcieProbe)];

    public static IReadOnlyList<Scenario> All { get; } =
    [
        new(Baseline, "The PC as it is: no external tools, nothing to set up.", [],
            [new(null, ["Nothing to set up. Close nothing you normally keep open: the scan reads the PC as it is."], false,
                [ObservationCatalog.PhysicalChassisType], [], [])]),

        new(GameLoad, "A scan taken while a game runs. Not a benchmark: frame rate and GPU load are not measured.", [RawSources.Adapters],
            [new(null, GameSetup, true, GameFields, [], [])]),

        new(DisplayHighRefresh, "A monitor that offers more than 60 Hz, and how its refresh is set.", [RawSources.Display],
            [new(null,
                [
                    "Connect the high-refresh monitor you want to record, directly if you can.",
                    "Set its refresh in Settings > Display > Advanced display the way you want it recorded (the scan reads what Windows set).",
                    "If Windows offers \"Dynamic refresh rate\" for it, leave that as you want it recorded.",
                ], false,
                [ObservationCatalog.MonitorSpecMaxRefreshHz, ObservationCatalog.RefreshBelowMaxIntended, ObservationCatalog.DynamicRefreshIntended], [], [])]),

        new(Clone, "Two or more monitors showing the same desktop (Windows' \"Duplicate these displays\").", [RawSources.Display],
            [new(null,
                [
                    "Connect two or more monitors.",
                    "Settings > Display > \"Duplicate these displays\". The scan records independently whether outputs really share a source.",
                ], false,
                [ObservationCatalog.CloneModeIntended, ObservationCatalog.PhysicalMonitorsConnected], [], [])]),

        new(MotherboardDisplay, "A desktop with a monitor on the motherboard's own video output.", [RawSources.Display, RawSources.Adapters, RawSources.Pcie],
            [new(null,
                [
                    "On a desktop with a graphics card: plug the monitor into the motherboard's rear I/O video port.",
                    "Some boards switch that output off when a card is fitted. If no picture appears, record that: answer n/a, or skip.",
                ], false,
                [ObservationCatalog.MonitorCabling, ObservationCatalog.MotherboardOutputIntent, ObservationCatalog.PhysicalMonitorsConnected], [], [])]),

        new(PcieIdleLoad, "The PCIe step-0 spike (architecture section 16, step 0): one capture idle and one in a game, with GPU-Z, HWiNFO and the probe as external evidence. Research, not check 6.",
            [RawSources.Adapters, RawSources.Pcie],
            [
                new(IdleStep,
                    [
                        "Restart the PC, log in, close everything and wait about 3 minutes before you start the kit.",
                        "Don't open GPU-Z or HWiNFO yet: the scan runs first, so their sensor polling can't wake the link.",
                    ], false,
                    [
                        ObservationCatalog.IdleProcedureFollowed, ObservationCatalog.PhysicalPcieSlot, ObservationCatalog.PcieSlotConnectedTo,
                        ObservationCatalog.PcieSlotManualWidth, ObservationCatalog.PcieSlotManualGeneration, ObservationCatalog.GpuSpecPcieGeneration,
                        ObservationCatalog.GpuSpecPcieWidth, ObservationCatalog.GpuZVersion, ObservationCatalog.GpuZBusInterface,
                        ObservationCatalog.HwinfoVersion, ObservationCatalog.HwinfoGpuLink,
                    ],
                    PcieEvidence,
                    [
                        "Run the probe from the folder you copied it to (it is not part of the kit):",
                        "  powershell -ExecutionPolicy Bypass -File .\\pcie-probe.ps1 -Label idle -Samples 30 -IntervalMs 1000 -OutFolder \"{evidence}\"",
                        "Open GPU-Z (Graphics Card tab, Bus Interface visible; don't start its render test) and save a screenshot as gpu-z-idle.png in the evidence folder.",
                        "Open HWiNFO64 in full mode. Screenshot the GPU, every bridge above it and the root port with their PCIe link fields: hwinfo-idle.png, hwinfo-idle-2.png, ...",
                        "Optional: Device Manager, View > Devices by connection, the GPU's branch expanded: devmgr-idle.png.",
                        "Keep usernames, the computer name and disk or memory serial numbers out of every screenshot.",
                    ]),
                new(LoadStep,
                    [
                        "Keep GPU-Z and HWiNFO open where you can see them beside the game.",
                        "In another PowerShell window, start the probe now:",
                        "  powershell -ExecutionPolicy Bypass -File .\\pcie-probe.ps1 -Label load -DelaySeconds 60 -Samples 120 -IntervalMs 1000 -OutFolder \"{evidence}\"",
                        .. GameSetup,
                    ],
                    true,
                    [.. GameFields, ObservationCatalog.GpuZBusInterface, ObservationCatalog.HwinfoGpuLink],
                    PcieEvidence,
                    [
                        "While still playing, with the game in the foreground, save GPU-Z and HWiNFO screenshots in the evidence folder: gpu-z-load.png, hwinfo-load.png, hwinfo-load-2.png, ...",
                        "Wait until the probe window has finished (about 3 minutes after you started it).",
                        "Optional: quit the game, wait 2 minutes and run the probe once more with -Label after.",
                    ]),
            ]),

        new(SteamStorage, "Steam libraries and the drives they are on.", [RawSources.Steam, RawSources.Storage],
            [new(null, ["Nothing to set up. Steam may stay closed: its files are read, never changed."], false,
                [ObservationCatalog.GameLibraryDrive, ObservationCatalog.ExpectedGameDiskKind, ObservationCatalog.TestGameInstalledThere, ObservationCatalog.InstalledDriveKinds],
                [], [])]),
    ];

    public static Scenario? Find(string? id) => All.FirstOrDefault(scenario => scenario.Id == id);

    /// <exception cref="ArgumentException">Not a scenario id.</exception>
    public static Scenario Require(string? id) =>
        Find(id) ?? throw new ArgumentException($"'{id}' is not a Track A scenario ({string.Join(", ", All.Select(scenario => scenario.Id))}).", nameof(id));
}
