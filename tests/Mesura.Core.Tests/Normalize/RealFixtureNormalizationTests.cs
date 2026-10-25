using Mesura.Core.Facts;
using Mesura.Core.Normalize;
using Mesura.Core.Raw.Serialization;

namespace Mesura.Core.Tests.Normalize;

/// <summary>
/// The normaliser on real collector output: the notebook fixture (an invented ACMEPC notebook built on a scrubbed capture: i7-1165G7, Iris Xe
/// only, one NVMe, Windows 11 25H2). Each expectation is the machine's known configuration, recorded with the fixture,
/// so a wrong mapping of real data shows here. It proves this one machine, not a vendor or a model line.
/// </summary>
public sealed class RealFixtureNormalizationTests
{
    private static readonly NormalizedSnapshot Facts = Normalizer.Normalize(
        SnapshotJson.Deserialize(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "scrubbed", "PC-000-20260928T223014Z.json"))));

    [Fact]
    public void The_machine_is_a_notebook_as_its_firmware_declares()
    {
        Assert.Equal(ChassisType.Notebook, Facts.Machine.ChassisType.Value);
        Assert.Equal("ACMEPC", Facts.Machine.SystemManufacturer.Value);
        Assert.True(Facts.Power.BatteryPresent.Value);
    }

    [Fact]
    public void Its_processor_is_declared_with_four_cores_and_eight_threads()
    {
        var cpu = Facts.Processors.Value!.Single();

        Assert.Equal("11th Gen Intel(R) Core(TM) i7-1165G7 @ 2.80GHz", cpu.Version.Value);
        Assert.Equal(4, cpu.CoreCount.Value);
        Assert.Equal(8, cpu.ThreadCount.Value);
    }

    [Fact]
    public void Two_ddr4_modules_of_8_gib_on_two_controllers_and_six_empty_slots()
    {
        var populated = Facts.MemoryDevices.Value!.Where(slot => slot.Populated.Value).ToList();

        Assert.Equal(8, Facts.MemoryDevices.Value!.Count);
        Assert.Equal(["Controller0-ChannelA", "Controller1-ChannelA"], populated.Select(slot => slot.Locator.Value));
        Assert.All(populated, slot =>
        {
            Assert.Equal(8UL * 1024 * 1024 * 1024, slot.SizeBytes.Value);
            Assert.Equal(MemoryType.Ddr4, slot.Type.Value);
            Assert.Equal(3200, slot.ConfiguredSpeedMts.Value);
            Assert.StartsWith("EXMP2G64XX-3G2E1", slot.PartNumber.Value, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void The_internal_panel_is_driven_by_the_iris_xe_and_identified_by_its_edid()
    {
        var display = Facts.Displays.Value!.Single();
        var gpu = Facts.Gpus.Value!.Single(candidate => candidate.Id == display.Gpu.Value);

        Assert.Equal("Intel(R) Iris(R) Xe Graphics", gpu.Name.Value);
        Assert.True(gpu.IsIntegrated.Value);
        Assert.Equal(new MonitorIdentity("ACM", 0x0001), display.Monitor.Value);
        Assert.Equal(new PixelSize(2160, 1440), display.DesktopResolution.Value);
        Assert.Equal(FactGap.NotPresent, display.MonitorName.Missing); // the panel reports no name: recorded, not invented
    }

    [Fact]
    public void The_iris_xe_is_joined_to_its_pci_function_and_the_software_adapter_to_none()
    {
        var iris = Facts.Gpus.Value!.Single(gpu => gpu.IsHardware.Value);
        var basic = Facts.Gpus.Value!.Single(gpu => !gpu.IsHardware.Value);
        var pci = Facts.PciDevices.Value!.Single(device => device.Id == iris.PciDevice.Value);

        Assert.Equal(PciDeviceClass.Display, pci.Class.Value);
        Assert.Equal(FactGap.NotPresent, pci.Link.CurrentWidth.Missing); // an integrated GPU has no PCIe link
        Assert.Equal(FactGap.Unmatched, basic.PciDevice.Missing);
    }

    [Fact]
    public void The_nvme_carries_c_through_the_number_join_confirmed_by_its_location()
    {
        var nvme = Facts.Disks.Value!.Single();
        var c = Facts.Volumes.Value!.Single(volume => volume.DriveLetter.Value == "C");

        Assert.Equal("ACMEDISK MZV512XXXX-00000", nvme.Model.Value);
        Assert.Equal("PCI Slot 8 : Bus 1 : Device 0 : Function 0 : Adapter 0", nvme.Location.Value);
        Assert.Equal(4, nvme.Partitions.Value!.Count);
        Assert.Contains(nvme.Partitions.From, pointer => pointer.EndsWith("/location", StringComparison.Ordinal));
        Assert.Equal(nvme.Id, c.Disk.Value);
        Assert.Equal([@"C:\"], c.MountPoints.Value);
    }

    [Fact]
    public void Steam_is_not_registered_for_the_scanning_user()
    {
        Assert.Equal(SteamRegistration.NotRegistered, Facts.Steam.Registration.Value);
    }
}
