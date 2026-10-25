namespace Mesura.Core.TrackA;

/// <summary>
/// Every question the Track A kit can ask (architecture §4.7). Each is something the software can't read: where a
/// cable goes, what the board manual says, what the operator set, what GPU-Z showed. Machine facts are never asked;
/// they are in the normalised facts, and the kit shows them to the operator instead.
/// </summary>
public static class ObservationCatalog
{
    private static readonly string[] YesNo = ["yes", "no"];
    private static readonly string[] Widths = ["x16", "x8", "x4", "x2", "x1"];
    private static readonly string[] Generations = ["gen1", "gen2", "gen3", "gen4", "gen5", "gen6"];
    private static readonly string[] DriveKinds = ["hdd", "sataSsd", "nvmeSsd", "usb", "other"];

    public static readonly ObservationField OperatorNote = Note(
        "operatorNote", "Note (optional; no names, e-mail addresses, serial numbers or account details)", "Free text for the owner. It is never read as a fact.");

    public static readonly ObservationField GameNamesConsent = Choose(
        "gameNamesConsent", ObservationRole.Consent,
        "Has this PC's owner agreed that the names of games on it may be kept in the Track A corpus?",
        "The capture holds the installed Steam games' names, and any game you name. Without a yes the package can't join the corpus.",
        YesNo);

    public static readonly ObservationField PhysicalChassisType = Choose(
        "physicalChassisType", ObservationRole.OperatorGroundTruth,
        "Operator observation: physical chassis type",
        "What the PC physically is. The firmware's own chassis type is read automatically; this is your independent label.",
        ["desktop", "laptop", "miniCompact", "allInOne"]);

    public static readonly ObservationField GameName = Label(
        "gameName", ObservationRole.OperatorGroundTruth, "Game running during this capture", "As its title reads.");

    public static readonly ObservationField GameDisplayMode = Choose(
        "gameDisplayMode", ObservationRole.OperatorGroundTruth, "The game's display mode", "From the game's video settings.",
        ["fullscreen", "borderless", "windowed"]);

    public static readonly ObservationField StableGameplayReached = Choose(
        "stableGameplayReached", ObservationRole.ProcedureCompleted,
        "Were you in stable gameplay (not a menu or loading screen) when the start beep sounded?", "Your own account of the procedure.", YesNo);

    public static readonly ObservationField GameStayedInForeground = Choose(
        "gameStayedInForeground", ObservationRole.ProcedureCompleted,
        "Did the game stay in the foreground, unpaused, from the start beep to the end beep?",
        "Many games pause or cap themselves in the background, and the graphics card then idles.", YesNo);

    public static readonly ObservationField IdleProcedureFollowed = Choose(
        "idleProcedureFollowed", ObservationRole.ProcedureCompleted,
        "Was the PC idle when the scan ran (restarted, logged in, nothing open, about 3 minutes' wait)?", "Your own account of the procedure.", YesNo);

    public static readonly ObservationField PhysicalPcieSlot = Label(
        "physicalPcieSlot", ObservationRole.OperatorGroundTruth,
        "Physical PCIe slot label of the graphics card [optional]",
        "As printed on the board or in its manual, e.g. PCIEX16_1. Leave empty if you don't know; the software never infers it.");

    public static readonly ObservationField PcieSlotConnectedTo = Choose(
        "pcieSlotConnectedTo", ObservationRole.OperatorGroundTruth,
        "That slot's lanes come from", "The board manual's block diagram or slot table.", ["cpu", "chipset"]);

    public static readonly ObservationField PcieSlotManualWidth = Choose(
        "pcieSlotManualWidth", ObservationRole.OperatorGroundTruth,
        "That slot's electrical width, per the board manual", "Not the slot's physical size. Note lane sharing with M.2 slots in the note.", Widths);

    public static readonly ObservationField PcieSlotManualGeneration = Choose(
        "pcieSlotManualGeneration", ObservationRole.OperatorGroundTruth,
        "That slot's PCIe generation, per the board manual", "With this CPU, if the manual distinguishes CPUs.", Generations);

    public static readonly ObservationField GpuSpecPcieGeneration = Choose(
        "gpuSpecPcieGeneration", ObservationRole.OperatorGroundTruth,
        "The graphics card's PCIe generation, per its vendor's specification", "The card's spec page.", Generations);

    public static readonly ObservationField GpuSpecPcieWidth = Choose(
        "gpuSpecPcieWidth", ObservationRole.OperatorGroundTruth,
        "The graphics card's PCIe width, per its vendor's specification", "The card's spec page.", Widths);

    public static readonly ObservationField GpuZVersion = Label(
        "gpuZVersion", ObservationRole.ExternalToolObservation, "GPU-Z version", "Shown in GPU-Z's title bar.", "GPU-Z");

    public static readonly ObservationField GpuZBusInterface = Label(
        "gpuZBusInterface", ObservationRole.ExternalToolObservation,
        "GPU-Z 'Bus Interface' field, exactly as you read it for this step", "e.g. PCIe x16 4.0 @ x16 1.1; for the load step, read while the game was in the foreground. It stays GPU-Z's reading; it never becomes a fact.", "GPU-Z");

    public static readonly ObservationField HwinfoVersion = Label(
        "hwinfoVersion", ObservationRole.ExternalToolObservation, "HWiNFO version", "Help > About.", "HWiNFO");

    public static readonly ObservationField HwinfoGpuLink = Label(
        "hwinfoGpuLink", ObservationRole.ExternalToolObservation,
        "HWiNFO: the graphics card's PCIe link, current and maximum, as you read it for this step", "e.g. current x16 16 GT/s, max x16 16 GT/s; for the load step, read while the game was in the foreground.", "HWiNFO");

    public static readonly ObservationField MonitorSpecMaxRefreshHz = Integer(
        "monitorSpecMaxRefreshHz", "The monitor's maximum refresh rate per its specification (Hz)", "The box or the maker's spec page, not Windows.", 24, 1000);

    public static readonly ObservationField RefreshBelowMaxIntended = Choose(
        "refreshBelowMaxIntended", ObservationRole.OperatorGroundTruth,
        "Is this monitor deliberately set below its highest refresh rate?",
        "Your intent. The scan itself records the current refresh and the highest Windows offers.", YesNo);

    public static readonly ObservationField DynamicRefreshIntended = Choose(
        "dynamicRefreshIntended", ObservationRole.OperatorGroundTruth,
        "Is Windows' \"Dynamic refresh rate\" switched on for this display?",
        "Settings > Display > Advanced display. n/a if Windows doesn't offer it here.", YesNo, allowsNotApplicable: true);

    public static readonly ObservationField CloneModeIntended = Choose(
        "cloneModeIntended", ObservationRole.OperatorGroundTruth,
        "Did you set Windows to \"Duplicate these displays\"?", "Settings > Display. The capture shows independently whether outputs share a source.", YesNo);

    public static readonly ObservationField PhysicalMonitorsConnected = Integer(
        "physicalMonitorsConnected", "How many monitors are physically cabled to this PC right now?", "Count cables, including monitors that are off or inactive.", 0, 16);

    public static readonly ObservationField MonitorCabling = Choose(
        "monitorCabling", ObservationRole.OperatorGroundTruth,
        "Where is the monitor cable plugged in?", "motherboard: the ports on the back I/O panel; graphicsCard: the card's ports.",
        ["motherboard", "graphicsCard", "both"]);

    public static readonly ObservationField MotherboardOutputIntent = Choose(
        "motherboardOutputIntent", ObservationRole.OperatorGroundTruth,
        "Is a monitor on the motherboard output on purpose?", "e.g. a deliberate second screen. n/a if no monitor is on the motherboard output.",
        ["intended", "notIntended"], allowsNotApplicable: true);

    public static readonly ObservationField GameLibraryDrive = Choose(
        "gameLibraryDrive", ObservationRole.OperatorGroundTruth,
        "Drive letter of the Steam library that is meant to hold the games", "Your intent, not what the scan found. n/a if there is none.",
        [.. Enumerable.Range('A', 26).Select(letter => ((char)letter).ToString())], allowsNotApplicable: true);

    public static readonly ObservationField ExpectedGameDiskKind = Choose(
        "expectedGameDiskKind", ObservationRole.OperatorGroundTruth,
        "What kind of drive is that library on, physically?", "From the drive itself or its purchase record.", DriveKinds);

    public static readonly ObservationField TestGameInstalledThere = Choose(
        "testGameInstalledThere", ObservationRole.OperatorGroundTruth,
        "Is a game you know of installed in that library?", "Your own knowledge.", YesNo);

    public static readonly ObservationField InstalledDriveKinds = Multi(
        "installedDriveKinds", "Which kinds of drive are physically installed? (several: separate with commas)", "From the hardware, not from Windows.", DriveKinds);

    /// <summary>Every field, in the order observations are written.</summary>
    public static IReadOnlyList<ObservationField> All { get; } =
    [
        PhysicalChassisType, GameName, GameDisplayMode, StableGameplayReached, GameStayedInForeground, IdleProcedureFollowed,
        PhysicalPcieSlot, PcieSlotConnectedTo, PcieSlotManualWidth, PcieSlotManualGeneration, GpuSpecPcieGeneration, GpuSpecPcieWidth,
        GpuZVersion, GpuZBusInterface, HwinfoVersion, HwinfoGpuLink,
        MonitorSpecMaxRefreshHz, RefreshBelowMaxIntended, DynamicRefreshIntended, CloneModeIntended, PhysicalMonitorsConnected, MonitorCabling, MotherboardOutputIntent,
        GameLibraryDrive, ExpectedGameDiskKind, TestGameInstalledThere, InstalledDriveKinds,
        GameNamesConsent, OperatorNote,
    ];

    /// <summary>The procedure confirmations: a scenario whose step asked one and got anything but "yes" isn't completed.</summary>
    public static IReadOnlyList<ObservationField> Procedures { get; } = [StableGameplayReached, GameStayedInForeground, IdleProcedureFollowed];

    public static ObservationField? Find(string? id) => All.FirstOrDefault(field => field.Id == id);

    /// <summary>A field's place in <see cref="All"/>: the canonical order of observations.</summary>
    public static int OrderOf(string id)
    {
        for (var i = 0; i < All.Count; i++)
        {
            if (All[i].Id == id)
            {
                return i;
            }
        }

        throw new ArgumentException($"'{id}' is not an observation field.", nameof(id));
    }

    private static ObservationField Choose(string id, ObservationRole role, string question, string help, IReadOnlyList<string> choices, bool allowsNotApplicable = false) =>
        new(id, role, ObservationKind.Choice, question, help, choices, 0, 0, allowsNotApplicable, Optional: false, Tool: null);

    private static ObservationField Multi(string id, string question, string help, IReadOnlyList<string> choices) =>
        new(id, ObservationRole.OperatorGroundTruth, ObservationKind.MultiChoice, question, help, choices, 0, 0, AllowsNotApplicable: false, Optional: false, Tool: null);

    private static ObservationField Integer(string id, string question, string help, int minimum, int maximum) =>
        new(id, ObservationRole.OperatorGroundTruth, ObservationKind.Integer, question, help, [], minimum, maximum, AllowsNotApplicable: false, Optional: false, Tool: null);

    private static ObservationField Label(string id, ObservationRole role, string question, string help, string? tool = null) =>
        new(id, role, ObservationKind.Label, question, help, [], 0, 0, AllowsNotApplicable: false, Optional: false, tool);

    private static ObservationField Note(string id, string question, string help) =>
        new(id, ObservationRole.ContextualNote, ObservationKind.Note, question, help, [], 0, 0, AllowsNotApplicable: false, Optional: true, Tool: null);
}
