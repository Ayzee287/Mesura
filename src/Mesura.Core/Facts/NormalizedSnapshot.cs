using System.Text.Json.Serialization;
using Mesura.Core.Raw;
using Mesura.Core.Raw.Serialization;

namespace Mesura.Core.Facts;

/// <summary>
/// The normalised facts of one raw snapshot (architecture §4.2 "FactSet", §4.4 "As built in Milestone 2"): what the
/// product can state about the machine without judging it. Every value is a <see cref="Fact{T}"/>; a collection is
/// a fact too, so "none" (a known empty list) and "couldn't tell" (a missing list) stay apart.
/// </summary>
/// <remarks>
/// <para>
/// Subject ids (<c>display-1</c>, <c>gpu-1</c>, <c>disk-1</c>, …) are <b>scoped to this snapshot</b>: they are
/// assigned in each list's documented order and only link facts within it. They are never identities across scans.
/// Where the evidence offers a key that can outlast a restart, it is a fact of its own (a disk's
/// <see cref="DiskFacts.Location"/>), with its scope documented.
/// </para>
/// <para>
/// No fact is a verdict. Confirmed, Likely, Couldn't check and Not applicable belong to checks (§6.3).
/// </para>
/// </remarks>
/// <param name="SchemaVersion">The facts schema; bumped on any breaking change, including a changed meaning under the same name or rule id.</param>
/// <param name="RawSchemaVersion">The raw snapshot schema the facts were normalised from; <see cref="Fact{T}.From"/> pointers refer to it.</param>
/// <param name="CapturedAtUtc">When the raw snapshot was captured (copied from it; run-dependent).</param>
public sealed record NormalizedSnapshot(
    int SchemaVersion,
    int RawSchemaVersion,
    DateTimeOffset CapturedAtUtc,
    OsFacts Os,
    PowerFacts Power,
    MachineFacts Machine,
    Fact<IReadOnlyList<ProcessorFacts>> Processors,
    Fact<IReadOnlyList<MemoryDeviceFacts>> MemoryDevices,
    Fact<IReadOnlyList<DisplayFacts>> Displays,
    Fact<IReadOnlyList<GpuFacts>> Gpus,
    Fact<IReadOnlyList<PciDeviceFacts>> PciDevices,
    Fact<IReadOnlyList<DiskFacts>> Disks,
    Fact<IReadOnlyList<VolumeFacts>> Volumes,
    SteamFacts Steam)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>The operating system, as the platform reports it (raw <c>os</c>).</summary>
/// <param name="Edition">The edition identifier as reported, e.g. <c>Professional</c>.</param>
/// <param name="Version">The feature-update name as reported, e.g. <c>25H2</c>. Never used to decide support.</param>
/// <param name="Build">The build number, e.g. <c>26200</c>.</param>
/// <param name="UpdateRevision">The update revision, e.g. <c>9457</c> in build 26200.9457.</param>
public sealed record OsFacts(
    Fact<OsPlatform> Platform,
    Fact<string> Edition,
    Fact<string> Version,
    Fact<int> Build,
    Fact<int> UpdateRevision,
    Fact<MachineArchitecture> Architecture,
    Fact<OsProductType> ProductType,
    Fact<SessionType> Session);

/// <summary>Power state when the scan ran (raw <c>power</c>). A state, never an assessment.</summary>
public sealed record PowerFacts(Fact<bool> OnAcPower, Fact<bool> BatteryPresent);

/// <summary>The machine as its firmware declares it (SMBIOS types 1–3). Placeholders ("To Be Filled By O.E.M.") are kept as declared.</summary>
public sealed record MachineFacts(
    Fact<string> SystemManufacturer,
    Fact<string> SystemProduct,
    Fact<string> BoardManufacturer,
    Fact<string> BoardProduct,
    Fact<ChassisType> ChassisType);

/// <summary>The chassis type as SMBIOS declares it (DSP0134 §7.4.1). The value is the specification's code.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<ChassisType>))]
public enum ChassisType
{
    Other = 0x01,
    Unknown = 0x02,
    Desktop = 0x03,
    LowProfileDesktop = 0x04,
    PizzaBox = 0x05,
    MiniTower = 0x06,
    Tower = 0x07,
    Portable = 0x08,
    Laptop = 0x09,
    Notebook = 0x0A,
    HandHeld = 0x0B,
    DockingStation = 0x0C,
    AllInOne = 0x0D,
    SubNotebook = 0x0E,
    SpaceSaving = 0x0F,
    LunchBox = 0x10,
    MainServerChassis = 0x11,
    ExpansionChassis = 0x12,
    SubChassis = 0x13,
    BusExpansionChassis = 0x14,
    PeripheralChassis = 0x15,
    RaidChassis = 0x16,
    RackMountChassis = 0x17,
    SealedCasePc = 0x18,
    MultiSystem = 0x19,
    CompactPci = 0x1A,
    AdvancedTca = 0x1B,
    Blade = 0x1C,
    BladeEnclosure = 0x1D,
    Tablet = 0x1E,
    Convertible = 0x1F,
    Detachable = 0x20,
    IotGateway = 0x21,
    EmbeddedPc = 0x22,
    MiniPc = 0x23,
    StickPc = 0x24,
}
