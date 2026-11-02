using Mesura.Core.Nvidia;
using Mesura.Core.Windows;

namespace Mesura.Core.Tests.Windows;

public sealed class SystemSummaryTests
{
    private static SystemSummary Summary(IReadOnlyList<GpuAdapter> adapters, IReadOnlyList<NvidiaGpu>? nvidia = null) =>
        new("Professional", "25H2", 26200, false, false, [.. adapters.Select(a => a.Name)], [.. (nvidia ?? []).Select(n => n.Name)], true, nvidia ?? [], adapters);

    [Fact]
    public void An_amd_card_and_the_graphics_inside_a_ryzen_are_both_found_by_the_pci_vendor_and_named_with_their_driver()
    {
        var summary = Summary([
            new GpuAdapter("AMD Radeon RX 7800 XT", 0x1002, 0x747E, "32.0.21025.1024", false),
            new GpuAdapter("AMD Radeon(TM) Graphics", 0x1002, 0x164E, "32.0.21025.1024", true),
            new GpuAdapter("Microsoft Basic Render Driver", 0x1414, 0x8C, "10.0", null)]);

        Assert.Equal(["AMD Radeon RX 7800 XT", "AMD Radeon(TM) Graphics"], summary.Amd.Select(a => a.Name));
        Assert.False(summary.HasNvidia);
        Assert.Null(summary.Nvidia);
    }

    [Fact]
    public void Two_nvidia_cards_are_both_named_and_the_steps_use_the_first()
    {
        var a = new NvidiaGpu("NVIDIA GeForce RTX 4070", 0x2786, "32.0.15.6094");
        var b = new NvidiaGpu("NVIDIA GeForce GTX 1060 6GB", 0x1C03, "32.0.15.6094");

        var summary = Summary([], [a, b]);

        Assert.Equal("NVIDIA GeForce RTX 4070 + NVIDIA GeForce GTX 1060 6GB", summary.NvidiaNames);
        Assert.Equal(a, summary.Nvidia);
        Assert.Empty(summary.Amd);
    }
}
