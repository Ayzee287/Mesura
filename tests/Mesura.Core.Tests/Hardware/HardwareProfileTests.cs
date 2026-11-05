using Mesura.Core.Facts;
using Mesura.Core.Hardware;
using Mesura.Core.Normalize;
using Mesura.Core.Raw;
using Mesura.Core.Windows;

namespace Mesura.Core.Tests.Hardware;

public sealed class HardwareProfileTests
{
    /// <summary>The synthetic snapshot's facts: a discrete NVIDIA card and an Intel integrated GPU, two disks (volume C on the second), and no chassis, no processor and no power reading.</summary>
    private static NormalizedSnapshot Synthetic() => Normalizer.Normalize(SnapshotFixture.Create());

    private static Fact<T> Seen<T>(T value) => Fact.Observed(value, Basis.Declared, "/test");

    private static ProcessorFacts Socket(string id, int cores, int threads, bool populated = true, string manufacturer = "Intel(R) Corporation", string brand = "Test CPU") =>
        new(id, Seen("U3E1"), Seen(populated), Seen(manufacturer), Seen(brand), Seen(4000), Seen(cores), Seen(cores), Seen(threads));

    private static AdapterReading Adapter(string name, AdapterKind kind, bool physical = true, string vendor = "Intel", bool up = true) =>
        new(Guid.NewGuid().ToString("B"), name, name, vendor, null, "1.0", "2026-01-01", kind, physical, up, new Dictionary<string, AdapterPropertyReading>(), new Dictionary<string, bool>(), null);

    [Fact]
    public void What_the_snapshot_does_not_say_stays_unknown_and_is_never_guessed()
    {
        var profile = HardwareProfileBuilder.Build(Synthetic(), new HardwareExtras { WindowsBuild = 26200 });

        Assert.Equal(DeviceForm.Unknown, profile.Form);
        Assert.Equal(CoolingClass.Unknown, profile.Cooling);
        Assert.False(profile.IsPortable);
        Assert.Null(profile.Cpu);
        Assert.Null(profile.MemoryBytes);
        Assert.Null(profile.Power.HasBattery);
        Assert.Null(profile.Power.OnMains);
        Assert.Null(profile.Windows.PolicyEdition);
        Assert.Empty(profile.Network);
    }

    [Fact]
    public void A_discrete_nvidia_card_is_named_by_its_pci_vendor_with_its_driver()
    {
        var profile = HardwareProfileBuilder.Build(Synthetic(), new HardwareExtras { WindowsBuild = 26200 });

        Assert.Equal([GpuMaker.Nvidia, GpuMaker.Intel], profile.Gpus.Select(g => g.Maker));
        var gpu = profile.Gpus[0];
        Assert.True(gpu.IsDiscrete);
        Assert.Equal("32.0.15.6094", gpu.DriverVersion);
        Assert.False(profile.Gpus[1].IsDiscrete);
        Assert.True(profile.HasNvidia);
        Assert.True(profile.HasDiscreteGpu);
        Assert.False(profile.HasAmdGpu);
    }

    [Fact]
    public void A_software_adapter_is_not_hardware()
    {
        var facts = Synthetic();
        var software = facts.Gpus.Value![0] with { IsHardware = Seen(false) };
        var profile = HardwareProfileBuilder.Build(facts with { Gpus = Seen<IReadOnlyList<GpuFacts>>([software, facts.Gpus.Value[1]]) }, new HardwareExtras { WindowsBuild = 26200 });

        var remaining = Assert.Single(profile.Gpus);
        Assert.Equal(GpuMaker.Intel, remaining.Maker);
    }

    [Fact]
    public void The_system_disk_is_the_one_that_holds_the_system_drive_volume()
    {
        var facts = Synthetic();
        var volume = facts.Volumes.Value!.Single(v => v.DriveLetter.Value == "C");

        var profile = HardwareProfileBuilder.Build(facts, new HardwareExtras { WindowsBuild = 26200, SystemDriveLetter = "C:" });

        var holder = Assert.Single(profile.Disks, d => d.HoldsWindows);
        Assert.Equal(facts.Disks.Value!.Single(d => d.Id == volume.Disk.Value).Model.Value, holder.Model);
        Assert.Same(holder, profile.SystemDisk);
    }

    [Fact]
    public void Without_a_system_drive_no_disk_is_claimed_to_hold_windows()
    {
        var profile = HardwareProfileBuilder.Build(Synthetic(), new HardwareExtras { WindowsBuild = 26200 });

        Assert.NotEmpty(profile.Disks);
        Assert.DoesNotContain(profile.Disks, d => d.HoldsWindows);
        Assert.Null(profile.SystemDisk);
    }

    [Fact]
    public void A_notebook_chassis_is_a_laptop_whose_case_limits_cooling()
    {
        var facts = Synthetic();
        facts = facts with { Machine = facts.Machine with { ChassisType = Seen(ChassisType.Notebook) }, Power = new PowerFacts(Seen(false), Seen(true)) };

        var profile = HardwareProfileBuilder.Build(facts, new HardwareExtras { WindowsBuild = 26200 });

        Assert.Equal(DeviceForm.Portable, profile.Form);
        Assert.True(profile.IsPortable);
        Assert.Equal(CoolingClass.Limited, profile.Cooling);
        Assert.True(profile.Power.HasBattery);
        Assert.False(profile.Power.OnMains);
    }

    [Fact]
    public void A_tower_is_open_cooling_even_when_a_battery_is_reported()
    {
        var facts = Synthetic();
        facts = facts with { Machine = facts.Machine with { ChassisType = Seen(ChassisType.Tower) }, Power = new PowerFacts(Seen(true), Seen(true)) };

        var profile = HardwareProfileBuilder.Build(facts, new HardwareExtras { WindowsBuild = 26200 });

        Assert.Equal(DeviceForm.Desktop, profile.Form);
        Assert.Equal(CoolingClass.Open, profile.Cooling);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_battery_alone_never_decides_the_form(bool battery)
    {
        var facts = Synthetic() with { Power = new PowerFacts(Seen(true), Seen(battery)) };

        var profile = HardwareProfileBuilder.Build(facts, new HardwareExtras { WindowsBuild = 26200 });

        Assert.Equal(DeviceForm.Unknown, profile.Form);
        Assert.Equal(battery, profile.Power.HasBattery);
    }

    [Fact]
    public void The_platform_reading_of_the_battery_wins_over_the_scan_so_a_ups_is_not_a_battery()
    {
        var facts = Synthetic() with { Power = new PowerFacts(Seen(true), Seen(true)) };

        var profile = HardwareProfileBuilder.Build(facts, new HardwareExtras { WindowsBuild = 26200, HasBattery = false });

        Assert.False(profile.Power.HasBattery);
    }

    [Fact]
    public void Cores_and_threads_add_up_over_the_populated_sockets_only()
    {
        var facts = Synthetic() with
        {
            Processors = Seen<IReadOnlyList<ProcessorFacts>>([Socket("cpu-1", 16, 32, manufacturer: "Advanced Micro Devices, Inc.", brand: "AMD Test"), Socket("cpu-2", 16, 32), Socket("cpu-3", 8, 16, populated: false)]),
        };

        var cpu = HardwareProfileBuilder.Build(facts, new HardwareExtras { WindowsBuild = 26200, CpuVendorId = "AuthenticAMD", CpuName = "AMD Ryzen Test", HybridCpu = false }).Cpu;

        Assert.NotNull(cpu);
        Assert.Equal(CpuMaker.Amd, cpu.Maker);
        Assert.Equal("AMD Ryzen Test", cpu.Name);
        Assert.Equal(32, cpu.Cores);
        Assert.Equal(64, cpu.Threads);
        Assert.False(cpu.Hybrid);
    }

    [Fact]
    public void A_socket_that_does_not_say_its_threads_leaves_the_total_unknown_rather_than_short()
    {
        var silent = Socket("cpu-2", 4, 8) with { ThreadCount = Fact.Missing<int>(FactGap.NotPresent, "no thread count", "/test") };
        var facts = Synthetic() with { Processors = Seen<IReadOnlyList<ProcessorFacts>>([Socket("cpu-1", 4, 8), silent]) };

        var cpu = HardwareProfileBuilder.Build(facts, new HardwareExtras { WindowsBuild = 26200 }).Cpu;

        Assert.NotNull(cpu);
        Assert.Equal(8, cpu.Cores);
        Assert.Null(cpu.Threads);
    }

    [Theory]
    [InlineData(BusKind.Nvme, MediaKind.Ssd, StorageClass.Nvme)]
    [InlineData(BusKind.Nvme, MediaKind.Unspecified, StorageClass.Nvme)]
    [InlineData(BusKind.Sata, MediaKind.Hdd, StorageClass.Hdd)]
    [InlineData(BusKind.Sata, MediaKind.Ssd, StorageClass.Ssd)]
    [InlineData(BusKind.Sata, MediaKind.Unspecified, StorageClass.Unknown)]
    [InlineData(BusKind.Usb, MediaKind.Ssd, StorageClass.Removable)]
    [InlineData(BusKind.Sd, MediaKind.Unspecified, StorageClass.Removable)]
    [InlineData(BusKind.StorageSpaces, MediaKind.Unspecified, StorageClass.Other)]
    public void A_drive_is_classified_by_its_bus_and_media_and_an_unspecified_one_stays_unknown(BusKind bus, MediaKind media, StorageClass expected) =>
        Assert.Equal(expected, HardwareProfileBuilder.ClassOf(Seen(bus), Seen(media)));

    [Fact]
    public void A_drive_whose_bus_and_media_are_missing_is_unknown()
    {
        Assert.Equal(StorageClass.Unknown, HardwareProfileBuilder.ClassOf(
            Fact.Missing<BusKind>(FactGap.NotSupported, "no bus", "/test"), Fact.Missing<MediaKind>(FactGap.NotSupported, "no media", "/test")));
    }

    [Fact]
    public void Only_physical_ethernet_and_wifi_adapters_are_hardware()
    {
        var adapters = new[]
        {
            Adapter("Test Gigabit Ethernet", AdapterKind.Ethernet, vendor: "Realtek"),
            Adapter("Test Wi-Fi 6", AdapterKind.WiFi, up: false),
            Adapter("Hyper-V Virtual Ethernet", AdapterKind.Virtual, physical: false),
            Adapter("Test VPN", AdapterKind.Vpn, physical: false),
            Adapter("Bluetooth PAN", AdapterKind.Other),
        };

        var profile = HardwareProfileBuilder.Build(Synthetic(), new HardwareExtras { WindowsBuild = 26200, Adapters = adapters });

        Assert.Equal(["Test Gigabit Ethernet", "Test Wi-Fi 6"], profile.Network.Select(n => n.Name));
        Assert.True(profile.HasEthernet);
        Assert.True(profile.HasWifi);
        Assert.False(profile.Network.Single(n => n.Kind == AdapterKind.WiFi).Up);
        Assert.Equal("Realtek", profile.Network.Single(n => n.Kind == AdapterKind.Ethernet).Maker);
    }

    [Fact]
    public void Memory_is_reported_to_the_nearest_gigabyte_because_windows_reports_what_it_can_use()
    {
        var profile = HardwareProfileBuilder.Build(Synthetic(), new HardwareExtras { WindowsBuild = 26200, MemoryBytes = 16_969_424_896 });

        Assert.Equal(16, profile.MemoryGb);
    }

    [Theory]
    [InlineData(19045, false)]
    [InlineData(22000, true)]
    [InlineData(26200, true)]
    public void Windows_11_starts_at_build_22000(int build, bool windows11) =>
        Assert.Equal(windows11, new WindowsPlatform(build, null, null, null).IsWindows11);
}
