using Mesura.Core.Firmware;

namespace Mesura.Core.Tests.Firmware;

/// <summary>
/// Resizable BAR / SmartAccess Memory as the owner's G4 asked: the layers kept apart, what is read told from what is inferred, "supported but off" explained with this board maker's steps, "enabled" said only when
/// the window really is as large as the card's memory, and the BIOS never touched.
/// </summary>
public sealed class ResizableBarAdvisorTests
{
    private static FirmwareFacts Facts(FirmwareGpu? gpu, string cpu = "AMD Ryzen 7 7700 8-Core Processor", string bootMode = "UEFI", string board = "Gigabyte Technology Co., Ltd.") =>
        new(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero), 26200, "Desktop", board, "B850 EAGLE ICE", board, "B850 EAGLE ICE", "American Megatrends", "F4", "05/24/2026", bootMode, true,
            new FirmwareExtras(), cpu, [], 4, gpu is null ? [] : [gpu]);

    private static FirmwareGpu Rtx3080(ulong? window, ulong? start = null) => new("NVIDIA GeForce RTX 3080", "NVIDIA", false, 10UL << 30, window, start);

    private static FirmwareGpu Radeon(ulong? window, ulong? start = null) => new("AMD Radeon RX 9070 XT", "AMD", false, 16UL << 30, window, start);

    private static string State(BarAssessment a, string layer) => a.Layers.Single(l => l.Name == layer).State;

    [Fact]
    public void A_window_as_large_as_the_cards_memory_is_enabled_and_the_chain_is_shown_as_inferred_where_it_is()
    {
        var a = ResizableBarAdvisor.Assess(Facts(Rtx3080(16UL << 30, 0x3800_0000_0000)));

        Assert.Equal(BarVerdict.Enabled, a.Verdict);
        Assert.Equal("On (inferred)", State(a, "Above 4G Decoding")); // the window sits above 4 GiB
        Assert.Equal("On (inferred)", State(a, "Re-Size BAR in the BIOS"));
        Assert.Equal("Evidently supported", State(a, "Motherboard BIOS"));
        Assert.False(a.MesuraCanChangeIt);
    }

    [Fact]
    public void A_small_window_on_supported_hardware_is_needs_bios_with_this_board_makers_steps_and_never_enabled()
    {
        var a = ResizableBarAdvisor.Assess(Facts(Radeon(256UL << 20, 0xD000_0000)));

        Assert.Equal(BarVerdict.NeedsBios, a.Verdict);
        Assert.Equal("Probably off", State(a, "Re-Size BAR in the BIOS"));
        Assert.Equal("Unknown", State(a, "Above 4G Decoding")); // 256 MB below 4 GiB proves nothing
        Assert.Contains(a.Instruction, s => s.En.Contains("IO Ports", StringComparison.Ordinal)); // Gigabyte's own menu path
        Assert.Contains("never changes the BIOS", a.Reason, StringComparison.Ordinal);
        Assert.False(a.MesuraCanChangeIt);
    }

    [Fact]
    public void A_small_window_above_4_gib_says_above_4g_is_working_and_only_the_resize_option_is_missing()
    {
        var a = ResizableBarAdvisor.Assess(Facts(Radeon(256UL << 20, 0x7000_0000_0000)));

        Assert.Equal("On (inferred)", State(a, "Above 4G Decoding"));
        Assert.Equal("Probably off", State(a, "Re-Size BAR in the BIOS"));
    }

    [Fact]
    public void A_large_window_with_the_amd_drivers_own_switch_off_is_the_one_thing_mesura_can_change()
    {
        var a = ResizableBarAdvisor.Assess(Facts(Radeon(16UL << 30, 0x7000_0000_0000)), new DriverBarState(true, false));

        Assert.Equal(BarVerdict.DriverSwitchOff, a.Verdict);
        Assert.True(a.MesuraCanChangeIt);
        Assert.Equal("Off", State(a, "Driver"));
    }

    [Fact]
    public void A_driver_switch_that_is_off_while_the_window_is_small_is_still_a_bios_matter_first()
    {
        var a = ResizableBarAdvisor.Assess(Facts(Radeon(256UL << 20)), new DriverBarState(true, false));

        Assert.Equal(BarVerdict.NeedsBios, a.Verdict);
        Assert.False(a.MesuraCanChangeIt);
    }

    [Fact]
    public void A_card_the_vendor_does_not_support_is_not_supported_and_is_not_pushed_towards_patched_firmware()
    {
        var a = ResizableBarAdvisor.Assess(Facts(new FirmwareGpu("NVIDIA GeForce RTX 2080", "NVIDIA", false, 8UL << 30, 256UL << 20)));

        Assert.Equal(BarVerdict.NotSupported, a.Verdict);
        Assert.Contains("brick", a.Reason, StringComparison.Ordinal);
        Assert.Empty(a.Instruction);
    }

    [Fact]
    public void An_old_processor_with_a_small_window_is_not_supported_but_a_large_window_beats_the_table()
    {
        Assert.Equal(BarVerdict.NotSupported, ResizableBarAdvisor.Assess(Facts(Rtx3080(256UL << 20), cpu: "AMD Ryzen 5 1600 Six-Core Processor")).Verdict);
        Assert.Equal(BarVerdict.Enabled, ResizableBarAdvisor.Assess(Facts(Rtx3080(16UL << 30), cpu: "AMD Ryzen 5 1600 Six-Core Processor")).Verdict); // measured on the PC itself
    }

    [Fact]
    public void Legacy_boot_comes_first_with_the_warning_about_installed_windows()
    {
        var a = ResizableBarAdvisor.Assess(Facts(Rtx3080(256UL << 20), bootMode: "Legacy BIOS"));

        Assert.Equal(BarVerdict.NeedsUefi, a.Verdict);
        Assert.Contains("unable to start", a.Reason, StringComparison.Ordinal);
        Assert.Contains("legacy", a.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unreadable_window_is_unknown_never_a_guess_and_a_pc_without_a_discrete_card_is_not_applicable()
    {
        Assert.Equal(BarVerdict.Unknown, ResizableBarAdvisor.Assess(Facts(Rtx3080(null))).Verdict);
        Assert.Equal(BarVerdict.NotApplicable, ResizableBarAdvisor.Assess(Facts(null)).Verdict);
        Assert.Equal(BarVerdict.NotApplicable, ResizableBarAdvisor.Assess(Facts(new FirmwareGpu("Intel Iris Xe", "Intel", true, 0, null))).Verdict);
    }

    [Theory]
    [InlineData("AMD Ryzen 9 5900X 12-Core Processor", BarSupport.Supported)]
    [InlineData("AMD Ryzen 7 7700 8-Core Processor", BarSupport.Supported)]
    [InlineData("AMD Ryzen 9 9950X3D 16-Core Processor", BarSupport.Supported)]
    [InlineData("AMD Ryzen 5 5600G with Radeon Graphics", BarSupport.Likely)]
    [InlineData("AMD Ryzen 5 3600 6-Core Processor", BarSupport.Likely)]
    [InlineData("AMD Ryzen 5 3400G with Radeon Vega Graphics", BarSupport.Unlikely)]
    [InlineData("AMD Ryzen 7 2700X Eight-Core Processor", BarSupport.Unlikely)]
    [InlineData("11th Gen Intel(R) Core(TM) i7-1165G7 @ 2.80GHz", BarSupport.Supported)]
    [InlineData("12th Gen Intel(R) Core(TM) i5-12400", BarSupport.Supported)]
    [InlineData("Intel(R) Core(TM) i7-9700K CPU @ 3.60GHz", BarSupport.Likely)]
    [InlineData("Intel(R) Core(TM) i7-7700K CPU @ 4.20GHz", BarSupport.Unlikely)]
    [InlineData("Intel(R) Core(TM) Ultra 7 155H", BarSupport.Supported)]
    [InlineData("Some Future Processor", BarSupport.Unknown)]
    [InlineData(null, BarSupport.Unknown)]
    public void The_processor_is_judged_from_the_generation_in_its_name_and_an_unrecognised_name_is_unknown(string? name, BarSupport expected) =>
        Assert.Equal(expected, ResizableBarAdvisor.CpuSupport(name).Support);

    [Theory]
    [InlineData("NVIDIA GeForce RTX 5090", BarSupport.Supported)]
    [InlineData("NVIDIA GeForce RTX 4070", BarSupport.Supported)]
    [InlineData("NVIDIA GeForce RTX 3080", BarSupport.Supported)]
    [InlineData("NVIDIA GeForce RTX 2080 Ti", BarSupport.NotSupported)]
    [InlineData("NVIDIA GeForce GTX 1660 SUPER", BarSupport.NotSupported)]
    [InlineData("NVIDIA RTX A4000", BarSupport.Unknown)]
    [InlineData("AMD Radeon RX 9070 XT", BarSupport.Supported)]
    [InlineData("AMD Radeon RX 7900 XTX", BarSupport.Supported)]
    [InlineData("AMD Radeon RX 6600", BarSupport.Supported)]
    [InlineData("AMD Radeon RX 5700 XT", BarSupport.Unknown)]
    [InlineData("AMD Radeon RX 580", BarSupport.Unknown)]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", BarSupport.Supported)]
    public void The_card_is_judged_from_its_series_and_an_unrecognised_name_is_unknown(string name, BarSupport expected) =>
        Assert.Equal(expected, ResizableBarAdvisor.GpuSupport(new FirmwareGpu(name, null, false, 8UL << 30, null)).Support);
}
