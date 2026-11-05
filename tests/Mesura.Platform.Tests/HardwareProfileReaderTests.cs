using Mesura.Core.Hardware;
using Mesura.Platform.Windows.Windows;

namespace Mesura.Platform.Tests;

/// <summary>The hardware profile on the machine running the tests. Read-only; it asserts invariants only, never this machine's hardware, so it passes on any supported PC.</summary>
public sealed class HardwareProfileReaderTests
{
    [Fact]
    public void The_profile_of_this_pc_is_read_without_changing_anything_and_says_only_what_windows_said()
    {
        var reader = new SystemStateReader();

        var profile = HardwareProfileReader.Read(reader, "test");

        Assert.Equal(reader.Build, profile.Windows.Build);
        Assert.True(profile.Windows.Build >= 19045);
        Assert.Equal(reader.HasBattery, profile.Power.HasBattery);
        Assert.Equal(reader.ModernStandby, profile.Power.ModernStandby);
        Assert.Equal(reader.PhysicalMemoryBytes, profile.MemoryBytes);
        Assert.NotNull(profile.Cpu);
        Assert.False(string.IsNullOrWhiteSpace(profile.Cpu.Name));
        Assert.All(profile.Gpus, g => Assert.False(string.IsNullOrWhiteSpace(g.Name)));
        Assert.All(profile.Gpus.Where(g => g.VendorId == 0x10DE), g => Assert.Equal(GpuMaker.Nvidia, g.Maker));
        Assert.True(profile.Disks.Count(d => d.HoldsWindows) <= 1);
        Assert.DoesNotContain(profile.Network, n => n.Kind is not (Mesura.Core.Windows.AdapterKind.Ethernet or Mesura.Core.Windows.AdapterKind.WiFi));
        Assert.Equal(profile.Form == DeviceForm.Portable, profile.IsPortable);
    }
}
