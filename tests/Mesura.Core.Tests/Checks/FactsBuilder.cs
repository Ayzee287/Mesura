using Mesura.Core.Checks;
using Mesura.Core.Facts;
using Mesura.Core.Raw;

namespace Mesura.Core.Tests.Checks;

/// <summary>
/// Normalised facts for check tests (architecture §11: "FactSet built with small test builders, one line per case").
/// The default is a healthy desktop: a tower on AC with no battery, a local session, an integrated GPU (gpu-1), a
/// discrete GPU (gpu-2) and a software adapter (gpu-3), and one DisplayPort monitor on the discrete GPU running
/// 2560 × 1440 at 165 Hz, the highest it is offered. Made-up data; provenance pointers are placeholders.
/// </summary>
internal static class FactsBuilder
{
    public const string From = "/test";

    public static Fact<T> Known<T>(T value, Basis basis = Basis.Authoritative) => Fact.Observed(value, basis, From);

    public static Fact<T> Missing<T>(FactGap gap = FactGap.NotPresent) => Fact.Missing<T>(gap, "test gap", From);

    public static Fact<IReadOnlyList<T>> List<T>(params T[] items) => Known<IReadOnlyList<T>>(items);

    public static NormalizedSnapshot Desktop() => new(
        SchemaVersion: NormalizedSnapshot.CurrentSchemaVersion,
        RawSchemaVersion: 2,
        CapturedAtUtc: new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero),
        Os: new OsFacts(
            Known(OsPlatform.Windows), Known("Professional"), Known("25H2"), Known(26200), Known(1), Known(MachineArchitecture.X64),
            Known(OsProductType.Workstation), Known(SessionType.Local)),
        Power: new PowerFacts(OnAcPower: Known(true), BatteryPresent: Known(false)),
        Machine: new MachineFacts(Known("Test"), Known("Test"), Known("Test"), Known("Test"), Known(ChassisType.Tower, Basis.Declared)),
        Processors: List<ProcessorFacts>(),
        MemoryDevices: List<MemoryDeviceFacts>(),
        Displays: List(Display()),
        Gpus: List(Integrated("gpu-1", "pci-1"), Discrete("gpu-2", "pci-2"), Software("gpu-3")),
        PciDevices: List(Pci("pci-1"), Pci("pci-2")),
        Disks: List<DiskFacts>(),
        Volumes: List<VolumeFacts>(),
        Steam: new SteamFacts(Known(SteamRegistration.NotRegistered), Missing<string>(), Missing<IReadOnlyList<SteamLibraryFacts>>()));

    /// <summary>A display at 2560 × 1440 offered 60, 144 and 165 Hz there (and 240 Hz only at 1920 × 1080).</summary>
    public static DisplayFacts Display(
        string id = "display-1",
        string gpu = "gpu-2",
        ConnectionKind connection = ConnectionKind.DisplayPort,
        uint signalHz = 165) => new(
        Id: id,
        Gpu: Known(gpu),
        SharesSourceWith: List<string>(),
        Connection: Known(connection),
        DesktopResolution: Known(new PixelSize(2560, 1440)),
        SignalRefresh: Known(new RefreshRate(signalHz, 1)),
        PathRefresh: Known(new RefreshRate(signalHz, 1)),
        OfferedModes: List(Mode(60), Mode(144), Mode(165), new VideoMode(1920, 1080, 240, false)),
        MonitorName: Known("TEST MONITOR"),
        Monitor: Missing<MonitorIdentity>(),
        EdidOverride: Known(false));

    public static VideoMode Mode(int hz, bool interlaced = false) => new(2560, 1440, hz, interlaced);

    public static GpuFacts Integrated(string id, string pci) => Gpu(id, hardware: true, integrated: true, Known(pci));

    public static GpuFacts Discrete(string id, string pci) => Gpu(id, hardware: true, integrated: false, Known(pci));

    public static GpuFacts Software(string id) => Gpu(id, hardware: false, integrated: false, Missing<string>(FactGap.Unmatched));

    public static GpuFacts Gpu(string id, bool hardware, bool integrated, Fact<string> pci) => new(
        Id: id,
        Name: Known("Test " + id),
        PciIdentity: Missing<PciFunctionId>(),
        PciSubsystem: Missing<PciSubsystemId>(),
        IsIntegrated: Known(integrated),
        IsHardware: Known(hardware),
        DriverVersion: Known("1.0"),
        DedicatedMemoryBytes: Known(0UL),
        PciDevice: pci);

    public static PciDeviceFacts Pci(string id, PciDeviceClass deviceClass = PciDeviceClass.Display) => Pci(id, Known(deviceClass));

    public static PciDeviceFacts Pci(string id, Fact<PciDeviceClass> deviceClass)
    {
        var noLink = new PcieLinkFacts(Missing<int>(), Missing<int>(), Missing<decimal>(), Missing<decimal>());
        return new PciDeviceFacts(id, deviceClass, Missing<PciFunctionId>(), Missing<PciSubsystemId>(), noLink, noLink);
    }

    public static NormalizedSnapshot WithDisplays(this NormalizedSnapshot facts, params DisplayFacts[] displays) =>
        facts with { Displays = List(displays) };

    public static NormalizedSnapshot WithGpus(this NormalizedSnapshot facts, params GpuFacts[] gpus) =>
        facts with { Gpus = List(gpus) };

    public static NormalizedSnapshot WithSession(this NormalizedSnapshot facts, Fact<SessionType> session) =>
        facts with { Os = facts.Os with { Session = session } };

    public static NormalizedSnapshot WithPower(this NormalizedSnapshot facts, Fact<bool>? onAc = null, Fact<bool>? battery = null) =>
        facts with { Power = new PowerFacts(onAc ?? facts.Power.OnAcPower, battery ?? facts.Power.BatteryPresent) };

    public static NormalizedSnapshot WithChassis(this NormalizedSnapshot facts, Fact<ChassisType> chassis) =>
        facts with { Machine = facts.Machine with { ChassisType = chassis } };

    public static NormalizedSnapshot WithChassis(this NormalizedSnapshot facts, ChassisType chassis) =>
        facts.WithChassis(Known(chassis, Basis.Declared));
}

/// <summary>Gates for tests. The product's gate is <see cref="FixtureGate.Validated"/>, which unlocks nothing.</summary>
internal static class TestGates
{
    /// <summary>Every condition checks 1 and 2 can name, unlocked: shows what a rule allows once its fixtures exist.</summary>
    public static FixtureGate All { get; } = new([
        new(CheckIds.RefreshBelowMax, "problem/external", "test.json"),
        new(CheckIds.RefreshBelowMax, "ok/external", "test.json"),
        new(CheckIds.RefreshBelowMax, "ok/internal/ac", "test.json"),
        new(CheckIds.RefreshBelowMax, "problem/internal/ac", "test.json"),
        new(CheckIds.OnIntegratedGpu, "problem/desktop", "test.json"),
        new(CheckIds.OnIntegratedGpu, "ok/desktop", "test.json"),
    ]);

    public static FixtureGate Only(string check, string condition) => new([new(check, condition, "test.json")]);
}
