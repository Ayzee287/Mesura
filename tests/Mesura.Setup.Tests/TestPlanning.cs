using Mesura.Core.Hardware;
using Mesura.Core.Profiles;
using Mesura.Core.Windows;

namespace Mesura.Setup.Tests;

/// <summary>
/// What the setup tests plan a profile's Windows part from (0.9: <see cref="Mesura.Setup.Windows.Profiles.ProfileRunner.Plan"/> always plans for a PC). A
/// stand-in Windows where nothing is set and nothing can be written, for profiles whose Windows part isn't what the test is about; nothing real is read.
/// </summary>
internal static class TestPlanning
{
    public static PlanningContext For(ISystemReader reader, HardwareProfile? hardware = null) => PlanningContext.Reading(WindowsCatalog.Default, hardware, reader);

    /// <summary>A context over <see cref="EmptyWindows"/> with no hardware read.</summary>
    public static PlanningContext Empty => For(new EmptyWindows());

    /// <summary>A desktop with an NVMe drive, an NVIDIA card and a Realtek Ethernet adapter (synthetic, for planning only).</summary>
    public static HardwareProfile Desktop(bool nvme = true) => new(
        new WindowsPlatform(26100, "Professional", "24H2", true), DeviceForm.Desktop, new PowerPlatform(false, false, true),
        new CpuProfile("Test CPU", CpuMaker.Intel, 8, 16, false), 32UL << 30,
        [new GpuProfile("Test GPU", GpuMaker.Nvidia, 0x10DE, 1, "1.0", false)],
        nvme ? [new DiskProfile("NVMe", StorageClass.Nvme, 1, true, Mesura.Core.Raw.BusKind.Nvme)] : [new DiskProfile("SSD", StorageClass.Ssd, 1, true, Mesura.Core.Raw.BusKind.Sata)],
        [new NetworkProfile("Ethernet", AdapterKind.Ethernet, "Realtek", true)]);

    internal sealed class EmptyWindows : ISystemReader
    {
        public int Build => 26100;

        public bool IsElevated => false;

        public UserContext User { get; init; } = new("S-1-5-21-1-1-1-1001", "PC-owner", "", ProcessIsUser: true);

        public bool? HasBattery => false;

        public bool? ModernStandby => false;

        public bool? PolicyEdition => true;

        public RegistryReading ReadRegistry(string hive, string path, string name) => new(true, false, null, null);

        public ServiceReading ReadService(string name) => ServiceReading.Absent;

        public FeatureState ReadFeature(string name) => FeatureState.Absent;

        public FeatureState ReadTask(string path) => FeatureState.Absent;

        public PowerReading ReadPower() => new(null, new HashSet<string>());

        public uint? ReadPowerSetting(string scheme, string subgroup, string setting) => null;
    }
}
