using FpsUtility.Core.Raw;

namespace FpsUtility.Core.Tests;

/// <summary>
/// A synthetic snapshot that exercises every section, known and missing readings, native codes,
/// byte payloads and an unavailable source. It is made-up data, not any real machine.
/// </summary>
internal static class SnapshotFixture
{
    public static readonly DateTimeOffset CapturedAt = new(2026, 9, 28, 12, 0, 0, 123, TimeSpan.Zero);

    /// <param name="reversed">Builds every list in the opposite order, as a platform or parallel collectors might return it.</param>
    public static RawSnapshot Create(bool reversed = false)
    {
        IReadOnlyList<T> Order<T>(params T[] items) => reversed ? [.. items.Reverse()] : items;

        var display = new DisplayData(Order(
            new DisplayOutput(
                adapterId: "00000000000A0001",
                sourceId: "0",
                targetId: "4353",
                sourceName: Reading.Known(@"\\.\DISPLAY1"),
                connection: Reading.Known(ConnectionKind.DisplayPort, 10),
                desktopResolution: Reading.Known(new Resolution(2560, 1440)),
                signalRefresh: Reading.Known(new Rational(60000, 1000)),
                pathRefresh: Reading.Known(new Rational(60000, 1000)),
                monitorName: Reading.Known("TEST MONITOR"),
                monitorDevicePath: Reading.Known(@"\\?\DISPLAY#TST0001#1&test&0&UID1#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}"),
                offeredModes: Reading.Known(Order(
                    new DisplayMode(1920, 1080, 60, false),
                    new DisplayMode(2560, 1440, 60, false),
                    new DisplayMode(2560, 1440, 165, false)))),
            new DisplayOutput(
                adapterId: "00000000000A0001",
                sourceId: "1",
                targetId: "4354",
                sourceName: Reading.Missing<string>(MissingReason.SourceFailed, "DisplayConfigGetDeviceInfo(GET_SOURCE_NAME) failed: ERROR_GEN_FAILURE (31)"),
                connection: Reading.Known(ConnectionKind.Hdmi, 5),
                desktopResolution: Reading.Known(new Resolution(1920, 1080)),
                signalRefresh: Reading.Missing<Rational>(MissingReason.NotPresent, "Target mode: the path has no mode of this kind"),
                pathRefresh: Reading.Known(new Rational(0, 0)),
                monitorName: Reading.Missing<string>(MissingReason.NotPresent, "no monitor name"),
                monitorDevicePath: Reading.Known(@"\\?\DISPLAY#TST0002#1&test&0&UID2#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}"),
                offeredModes: Reading.Missing<IReadOnlyList<DisplayMode>>(MissingReason.SourceFailed, "No source name to list modes with"))));

        var edid = new EdidData(Order(
            new MonitorEdid(
                @"\\?\DISPLAY#TST0001#1&test&0&UID1#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}",
                Reading.Known(new EdidPayload([0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x10, 0x20], [true], 1, Reading.Known(false)))),
            new MonitorEdid(
                @"\\?\DISPLAY#TST0002#1&test&0&UID2#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}",
                Reading.Missing<EdidPayload>(MissingReason.NotPresent, "The monitor's hardware key has no binary EDID value"))));

        var adapters = new AdapterData(Order(
            new GraphicsAdapter(
                AdapterId: "00000000000A0001",
                Description: Reading.Known("Test Discrete GPU"),
                PciIds: Reading.Known(new PciIds(0x10DE, 0x2484, 0x1458, 0x4029, 0xA1)),
                HardwareId: Reading.Known(new PciHardwareId(0x10DE, 0x2484, 0x40291458, 0xA1)),
                IsIntegrated: Reading.Known(false),
                IsHardware: Reading.Known(true),
                DriverVersion: Reading.Known("32.0.15.6094"),
                DedicatedMemoryBytes: Reading.Known(8_589_934_592UL)),
            new GraphicsAdapter(
                AdapterId: "00000000000A0002",
                Description: Reading.Known("Test Integrated GPU"),
                PciIds: Reading.Missing<PciIds>(MissingReason.NotSupported, "DXCore property HardwareIDParts is not supported on Windows build 19045"),
                HardwareId: Reading.Known(new PciHardwareId(0x8086, 0x4680, 0x7D131462, 0x0C)),
                IsIntegrated: Reading.Known(true),
                IsHardware: Reading.Known(true),
                DriverVersion: Reading.Known("31.0.101.5186"),
                DedicatedMemoryBytes: Reading.Known(0UL))));

        var smbios = new SmbiosData(3, 4, 0, [0x7F, 0x04, 0x00, 0x01, 0x00, 0x00], 2, null);

        var link = new PcieLink(
            CurrentWidth: Reading.Known(4),
            MaxWidth: Reading.Known(16),
            CurrentSpeedGtps: Reading.Known(2.5m, 1),
            MaxSpeedGtps: Reading.Known(16.0m, 4));
        var noLink = new PcieLink(
            CurrentWidth: Reading.Missing<int>(MissingReason.NotPresent, "DEVPKEY_PciDevice_CurrentLinkWidth: CR_NO_SUCH_VALUE"),
            MaxWidth: Reading.Missing<int>(MissingReason.NotPresent, "DEVPKEY_PciDevice_MaxLinkWidth: CR_NO_SUCH_VALUE"),
            CurrentSpeedGtps: Reading.Missing<decimal>(MissingReason.ParseFailed, "DEVPKEY_PciDevice_CurrentLinkSpeed: 0 is not a PCIe link-speed code", 0),
            MaxSpeedGtps: Reading.Missing<decimal>(MissingReason.NotPresent, "DEVPKEY_PciDevice_MaxLinkSpeed: CR_NO_SUCH_VALUE"));
        var pcie = new PcieData(Order(
            new PciDevice(@"PCI\VEN_10DE&DEV_2484&SUBSYS_40291458&REV_A1\4&test&0&0008", Reading.Known(3), Reading.Known(new PciIds(0x10DE, 0x2484, 0x1458, 0x4029, 0xA1)), link, Reading.Known(new PciParent(@"PCI\VEN_8086&DEV_A70D&SUBSYS_7D131462&REV_01\3&test&0&08", link))),
            new PciDevice(@"PCI\VEN_8086&DEV_4680&SUBSYS_7D131462&REV_0C\3&test&0&10", Reading.Missing<int>(MissingReason.AccessDenied, "DEVPKEY_PciDevice_BaseClass: CM_Get_DevNode_PropertyW returned CR_ACCESS_DENIED (51)"), Reading.Known(new PciIds(0x8086, 0x4680, 0x1462, 0x7D13, 0x0C)), noLink, Reading.Missing<PciParent>(MissingReason.SourceFailed, "CM_Get_Parent returned CR_FAILURE (19)"))));

        var storage = new StorageData(
            Order(
                new PhysicalDisk("0", Reading.Known(NvmeLocation), Reading.Known("TEST NVME 1TB"), Reading.Known(MediaKind.Ssd, 4), Reading.Known(BusKind.Nvme, 17), Reading.Known(HealthState.Healthy, 0), Reading.Known<IReadOnlyList<int>>([2]), Reading.Known(1_000_204_886_016UL)),
                new PhysicalDisk("1", Reading.Known(SataLocation), Reading.Known("TEST HDD 2TB"), Reading.Known(MediaKind.Hdd, 3), Reading.Known(BusKind.Sata, 11), Reading.Known(HealthState.Warning, 1), Reading.Known<IReadOnlyList<int>>([5, 2]), Reading.Known(2_000_398_934_016UL))),
            Order(
                new OsDisk(0, Reading.Known(BusKind.Nvme, 17), Reading.Known(NvmeLocation)),
                new OsDisk(1, Reading.Known(BusKind.Sata, 11), Reading.Missing<string>(MissingReason.NotPresent, "WMI returned no Location"))),
            Order(
                new DiskPartition(0, 1, Reading.Known(104_857_600UL), Order(@"\\?\Volume{00000000-0000-0000-0000-000000000001}\")),
                new DiskPartition(0, 2, Reading.Known(999_000_000_000UL), Order(@"C:\", @"\\?\Volume{00000000-0000-0000-0000-000000000002}\")),
                new DiskPartition(1, 1, Reading.Known(2_000_000_000_000UL), Order(@"D:\", @"\\?\Volume{00000000-0000-0000-0000-000000000003}\"))),
            Order(
                new StorageVolume(@"\\?\Volume{00000000-0000-0000-0000-000000000002}\", Reading.Known("C"), Reading.Known("NTFS"), Reading.Known(999_000_000_000UL), Reading.Known(400_000_000_000UL)),
                new StorageVolume(@"\\?\Volume{00000000-0000-0000-0000-000000000003}\", Reading.Known("D"), Reading.Known("NTFS"), Reading.Known(2_000_000_000_000UL), Reading.Known(90_000_000_000UL))));

        var steam = new SteamData(
            @"C:\Program Files (x86)\Steam",
            Reading.Known(new SteamFile(@"C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf", "\"libraryfolders\"\n{\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"D:\\\\SteamLibrary\"\n\t\t\"label\"\t\t\"redacted\"\n\t\t\"contentid\"\t\t\"redacted\"\n\t}\n}\n")),
            Reading.Known(Order(
                new SteamLibrary(@"C:\Program Files (x86)\Steam", listedByLibraryFolders: false, isInstallFolder: true, Reading.Known(Order(
                    new SteamFile(@"C:\Program Files (x86)\Steam\steamapps\appmanifest_10.acf", "\"AppState\"\n{\n\t\"appid\"\t\t\"10\"\n}\n"),
                    new SteamFile(@"C:\Program Files (x86)\Steam\steamapps\appmanifest_20.acf", "\"AppState\"\n{\n\t\"appid\"\t\t\"20\"\n}\n")))),
                new SteamLibrary(@"D:\SteamLibrary", listedByLibraryFolders: true, isInstallFolder: false, Reading.Missing<IReadOnlyList<SteamFile>>(MissingReason.NotPresent, "DirectoryNotFoundException")))));

        return new RawSnapshot(
            RawSnapshot.CurrentSchemaVersion,
            CapturedAt,
            new AppInfo("FpsUtility", "0.1.0", Reading.Known("0123456789abcdef0123456789abcdef01234567")),
            Os(),
            elevated: false,
            new SnapshotSources(
                SourceEntry.Ok("test.display", 12, display),
                SourceEntry.Ok("test.edid", 3, edid),
                SourceEntry.Ok("test.adapters", 20, adapters),
                SourceEntry.Ok("test.smbios", 1, smbios),
                SourceEntry.Ok("test.pcie", 9, pcie),
                SourceEntry.Ok("test.storage", 400, storage),
                SourceEntry.Ok("test.steam", 5, steam),
                SourceEntry.Unavailable<PowerData>("test.power", 10000, MissingReason.TimedOut, "No answer within 10 s")));
    }

    private const string NvmeLocation = "PCI Slot 2 : Bus 1 : Device 0 : Function 0 : Adapter 0";
    private const string SataLocation = "Integrated : Adapter 0 : Port 1 : Target 0 : LUN 0";

    public static OsInfo Os(
        int build = 19045,
        MachineArchitecture architecture = MachineArchitecture.X64,
        OsProductType productType = OsProductType.Workstation) =>
        new(
            OsPlatform.Windows,
            Reading.Known("Professional"),
            Reading.Known("22H2"),
            build,
            Reading.Known(6456),
            Reading.Known(architecture, architecture switch
            {
                MachineArchitecture.X64 => 0x8664,
                MachineArchitecture.Arm64 => 0xAA64,
                MachineArchitecture.X86 => 0x014C,
                _ => 0x01C4,
            }),
            Reading.Known(productType),
            Reading.Known(SessionType.Local));
}
