using FpsUtility.Core.Facts;
using FpsUtility.Core.Normalize;
using FpsUtility.Core.Raw;

namespace FpsUtility.Core.Tests.Normalize;

/// <summary>Display outputs, GPUs and PCI devices: what is known, what is joined, and what stays missing and why.</summary>
public sealed class DisplayAndGpuNormalizationTests
{
    [Fact]
    public void A_display_knows_its_gpu_by_the_adapter_id_join()
    {
        var facts = Normalizer.Normalize(SnapshotFixture.Create());
        var display = facts.Displays.Value![0];
        var gpu = facts.Gpus.Value!.Single(candidate => candidate.Id == display.Gpu.Value);

        Assert.Equal("Test Discrete GPU", gpu.Name.Value);
        Assert.Equal(Rules.DisplayGpu, display.Gpu.Rule);
        Assert.Equal(["/sources/display/data/outputs/0/adapterId", "/sources/adapters/data/adapters/0/adapterId"], display.Gpu.From);
    }

    [Fact]
    public void An_unavailable_adapter_source_leaves_the_display_gpu_missing_for_that_reason_and_everything_else_known()
    {
        var facts = Normalizer.Normalize(RawBuilder.With(sources => sources with { Adapters = RawBuilder.Unavailable<AdapterData>(MissingReason.TimedOut) }));
        var display = facts.Displays.Value![0];

        Assert.Equal(FactGap.TimedOut, display.Gpu.Missing);
        Assert.Equal(["/sources/adapters"], display.Gpu.From);
        Assert.Equal(FactGap.TimedOut, facts.Gpus.Missing);
        Assert.True(display.Connection.IsKnown); // one collector's failure destroys nothing else
        Assert.True(facts.PciDevices.IsKnown);
    }

    [Fact]
    public void A_refresh_left_unset_is_not_present_and_a_zero_refresh_is_implausible_never_zero_hertz()
    {
        var facts = Normalizer.Normalize(WithOutput(output => Output(output, signal: new Rational(0, 1000), path: new Rational(0, 0))));
        var display = facts.Displays.Value![0];

        Assert.Equal(FactGap.Implausible, display.SignalRefresh.Missing);
        Assert.Equal(FactGap.NotPresent, display.PathRefresh.Missing);
    }

    [Fact]
    public void A_refresh_is_kept_as_an_exact_rational()
    {
        var facts = Normalizer.Normalize(WithOutput(output => Output(output, signal: new Rational(20602000, 343360), path: new Rational(60, 1))));

        Assert.Equal(new RefreshRate(20602000, 343360), facts.Displays.Value![0].SignalRefresh.Value);
    }

    [Fact]
    public void A_monitor_identity_is_read_from_its_edid_base_block_and_nothing_else_is()
    {
        var raw = RawBuilder.With(sources => sources with
        {
            Edid = RawBuilder.Ok(new EdidData([new MonitorEdid(sources.Display.Data!.Outputs[0].MonitorDevicePath.Value!, Reading.Known(new EdidPayload(RawBuilder.EdidBaseBlock(), [true], 0, Reading.Known(true))))])),
        });

        var display = Normalizer.Normalize(raw).Displays.Value![0];

        Assert.Equal(new MonitorIdentity("TST", 0x1234), display.Monitor.Value);
        Assert.Equal(Basis.Declared, display.Monitor.Basis);
        Assert.Equal(Rules.DisplayMonitor, display.Monitor.Rule);
        Assert.True(display.EdidOverride.Value);
        Assert.DoesNotContain(typeof(DisplayFacts).GetProperties(), property => property.Name.Contains("Max", StringComparison.Ordinal)); // no maximum refresh before step 4
    }

    [Theory]
    [InlineData(false)] // not a base block (the synthetic fixture's 10 bytes)
    [InlineData(true)] // a manufacturer ID with a letter outside A–Z
    public void An_edid_that_cannot_be_read_leaves_the_identity_missing_not_guessed(bool badLetter)
    {
        var bytes = RawBuilder.EdidBaseBlock();
        bytes[9] &= 0xE0; // third letter 0: not a letter
        var raw = RawBuilder.With(sources => badLetter
            ? sources with { Edid = RawBuilder.Ok(new EdidData([new MonitorEdid(sources.Display.Data!.Outputs[0].MonitorDevicePath.Value!, Reading.Known(new EdidPayload(bytes, [true], 0, Reading.Known(false))))])) }
            : sources);

        var monitor = Normalizer.Normalize(raw).Displays.Value![0].Monitor;

        Assert.Equal(badLetter ? FactGap.Implausible : FactGap.ParseFailed, monitor.Missing);
    }

    [Fact]
    public void Gpus_are_joined_to_their_pci_device_by_identity_even_from_a_combined_hardware_id()
    {
        var facts = Normalizer.Normalize(SnapshotFixture.Create());
        var integrated = facts.Gpus.Value!.Single(gpu => gpu.IsIntegrated.Value);

        Assert.Equal(Rules.PciIdentityFromHardwareId, integrated.PciIdentity.Rule);
        Assert.Equal(new PciFunctionId(0x8086, 0x4680, 0x0C), integrated.PciIdentity.Value);
        Assert.Equal(FactGap.NotSupported, integrated.PciSubsystem.Missing); // the combined subsystem is never split
        Assert.Equal("pci-2", integrated.PciDevice.Value);
        Assert.Equal(0UL, integrated.DedicatedMemoryBytes.Value); // a known 0 stays 0
        Assert.False(facts.Gpus.Value![0].IsIntegrated.Value); // a known false stays false
    }

    [Fact]
    public void Two_identical_cards_make_the_pci_relationship_ambiguous_not_guessed()
    {
        var raw = RawBuilder.With(sources =>
        {
            var card = sources.Pcie.Data!.Devices.Single(device => device.PciIds.Value?.VendorId == 0x10DE);
            var twin = card with { InstanceId = card.InstanceId + "-twin" };
            return sources with { Pcie = RawBuilder.Ok(new PcieData([.. sources.Pcie.Data!.Devices, twin])) };
        });

        var discrete = Normalizer.Normalize(raw).Gpus.Value!.Single(gpu => gpu.Name.Value == "Test Discrete GPU");

        Assert.Equal(FactGap.Ambiguous, discrete.PciDevice.Missing);
        Assert.Equal(3, discrete.PciDevice.From.Count); // the adapter's identity and both candidates
    }

    [Fact]
    public void A_pci_device_whose_identity_could_not_be_read_makes_a_single_match_ambiguous()
    {
        // One of two identical cards has unreadable ids: the readable one may not be the adapter's.
        var raw = RawBuilder.With(sources =>
        {
            var card = sources.Pcie.Data!.Devices.Single(device => device.PciIds.Value?.VendorId == 0x10DE);
            var unreadable = card with { InstanceId = card.InstanceId + "-2", PciIds = Reading.Missing<PciIds>(MissingReason.ParseFailed, "hardware id not decodable") };
            return sources with { Pcie = RawBuilder.Ok(new PcieData([.. sources.Pcie.Data!.Devices, unreadable])) };
        });

        var discrete = Normalizer.Normalize(raw).Gpus.Value!.Single(gpu => gpu.Name.Value == "Test Discrete GPU");

        Assert.Equal(FactGap.Ambiguous, discrete.PciDevice.Missing);
    }

    [Fact]
    public void Two_adapters_that_match_the_same_pci_device_both_lose_the_claim()
    {
        var raw = RawBuilder.With(sources =>
        {
            var adapter = sources.Adapters.Data!.Adapters.Single(candidate => candidate.Description.Value == "Test Discrete GPU");
            var copy = adapter with { AdapterId = "00000000000A0009", Description = Reading.Known("Test Discrete GPU (second view)") };
            return sources with { Adapters = RawBuilder.Ok(new AdapterData([.. sources.Adapters.Data!.Adapters, copy])) };
        });

        var claims = Normalizer.Normalize(raw).Gpus.Value!.Where(gpu => gpu.Name.Value!.StartsWith("Test Discrete GPU", StringComparison.Ordinal)).ToList();

        Assert.Equal(2, claims.Count);
        Assert.All(claims, gpu => Assert.Equal(FactGap.Ambiguous, gpu.PciDevice.Missing));
    }

    [Fact]
    public void An_adapter_id_listed_twice_makes_the_display_gpu_ambiguous_not_unmatched()
    {
        var raw = RawBuilder.With(sources =>
        {
            var adapter = sources.Adapters.Data!.Adapters.Single(candidate => candidate.Description.Value == "Test Discrete GPU");
            var twin = adapter with { Description = Reading.Known("Test Discrete GPU twin") };
            return sources with { Adapters = RawBuilder.Ok(new AdapterData([.. sources.Adapters.Data!.Adapters, twin])) };
        });

        Assert.Equal(FactGap.Ambiguous, Normalizer.Normalize(raw).Displays.Value![0].Gpu.Missing);
    }

    [Fact]
    public void An_edid_whose_base_block_checksum_was_invalid_as_stored_gives_no_identity()
    {
        var raw = RawBuilder.With(sources => sources with
        {
            Edid = RawBuilder.Ok(new EdidData([new MonitorEdid(sources.Display.Data!.Outputs[0].MonitorDevicePath.Value!, Reading.Known(new EdidPayload(RawBuilder.EdidBaseBlock(), [false], 0, Reading.Known(false))))])),
        });

        Assert.Equal(FactGap.ParseFailed, Normalizer.Normalize(raw).Displays.Value![0].Monitor.Missing);
    }

    [Fact]
    public void Offered_modes_leave_out_entries_that_are_not_modes_by_a_named_rule()
    {
        var raw = WithOutput(output => new DisplayOutput(
            output.AdapterId, output.SourceId, output.TargetId, output.SourceName, output.Connection, output.DesktopResolution,
            output.SignalRefresh, output.PathRefresh, output.MonitorName, output.MonitorDevicePath,
            Reading.Known<IReadOnlyList<DisplayMode>>([new(1920, 1080, 1, false), new(1920, 1080, 0, false), new(0, 1080, 60, false), new(1920, 1080, 144, false)])));

        var modes = Normalizer.Normalize(raw).Displays.Value![0].OfferedModes;

        Assert.Equal([new VideoMode(1920, 1080, 144, false)], modes.Value);
        Assert.Equal(Rules.DisplayModes, modes.Rule); // derived, and the raw list is one pointer away
    }

    [Fact]
    public void An_adapter_with_no_pci_device_of_its_identity_is_unmatched_not_absent()
    {
        var raw = RawBuilder.With(sources => sources with { Pcie = RawBuilder.Ok(new PcieData([])) });

        var gpu = Normalizer.Normalize(raw).Gpus.Value![0];

        Assert.Equal(FactGap.Unmatched, gpu.PciDevice.Missing);
    }

    [Fact]
    public void Link_facts_keep_known_values_and_the_reason_for_missing_ones()
    {
        var facts = Normalizer.Normalize(SnapshotFixture.Create());
        var card = facts.PciDevices.Value!.Single(device => device.PciIdentity.Value?.VendorId == 0x10DE);
        var integrated = facts.PciDevices.Value!.Single(device => device.PciIdentity.Value?.VendorId == 0x8086);

        Assert.Equal(PciDeviceClass.Display, card.Class.Value);
        Assert.Equal(4, card.Link.CurrentWidth.Value);
        Assert.Equal(16.0m, card.Link.MaxSpeedGtps.Value);
        Assert.Equal(16, card.UpstreamLink.MaxWidth.Value);
        Assert.Equal(FactGap.AccessDenied, integrated.Class.Missing);
        Assert.Equal(FactGap.NotPresent, integrated.Link.CurrentWidth.Missing);
        Assert.Equal(FactGap.ParseFailed, integrated.Link.CurrentSpeedGtps.Missing);
        Assert.Equal(FactGap.SourceFailed, integrated.UpstreamLink.MaxWidth.Missing); // the parent couldn't be read
    }

    [Fact]
    public void A_link_width_pci_express_does_not_define_is_implausible()
    {
        var raw = RawBuilder.With(sources =>
        {
            var card = sources.Pcie.Data!.Devices[0];
            var zero = card with { Link = card.Link with { CurrentWidth = Reading.Known(0) } };
            return sources with { Pcie = RawBuilder.Ok(new PcieData([zero, .. sources.Pcie.Data!.Devices.Skip(1)])) };
        });

        var device = Normalizer.Normalize(raw).PciDevices.Value!.Single(pci => pci.PciIdentity.Value?.VendorId == 0x10DE);

        Assert.Equal(FactGap.Implausible, device.Link.CurrentWidth.Missing);
    }

    private static RawSnapshot WithOutput(Func<DisplayOutput, DisplayOutput> change) =>
        RawBuilder.With(sources => sources with
        {
            Display = RawBuilder.Ok(new DisplayData([change(sources.Display.Data!.Outputs[0]), .. sources.Display.Data!.Outputs.Skip(1)])),
        });

    private static DisplayOutput Output(DisplayOutput output, Rational signal, Rational path) => new(
        output.AdapterId,
        output.SourceId,
        output.TargetId,
        output.SourceName,
        output.Connection,
        output.DesktopResolution,
        Reading.Known(signal),
        Reading.Known(path),
        output.MonitorName,
        output.MonitorDevicePath,
        output.OfferedModes);
}
