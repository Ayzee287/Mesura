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

    [Fact]
    public void The_processors_l3_caches_are_read_from_windows_and_cover_its_threads_or_are_left_unread()
    {
        var cpu = HardwareProfileReader.Read(new SystemStateReader(), "test").Cpu!;

        // Any x64 PC Mesura supports reports its L3 (0.8); when it does, the caches are L3, sized, and shared by the processor's threads.
        Assert.NotNull(cpu.L3Caches);
        Assert.All(cpu.L3Caches, c => Assert.True(c.Level == 3 && c.SizeBytes > 0 && c.LogicalProcessors > 0));
        if (cpu.Threads is { } threads)
        {
            Assert.Equal(threads, cpu.L3Caches.Sum(c => c.LogicalProcessors));
        }

        // The fact is always answered with its basis; an Intel processor is never a Ryzen X3D.
        Assert.False(string.IsNullOrWhiteSpace(cpu.DualCcdX3dFact.Basis));
        if (cpu.Maker == CpuMaker.Intel)
        {
            Assert.False(cpu.DualCcdX3d);
        }
    }
}
