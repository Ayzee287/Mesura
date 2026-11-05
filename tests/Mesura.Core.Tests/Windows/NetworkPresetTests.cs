using Mesura.Core.Windows;

namespace Mesura.Core.Tests.Windows;

/// <summary>
/// The per-maker network adapter presets (0.6): which card each one reaches, that it never reaches another maker's card, what it writes and what it leaves alone. The adapters are stand-ins built from the
/// keyword lists and value lists of real driver INFs (Windows 11 build 26100) and of the Intel AX201 on the development laptop; no real adapter was written.
/// </summary>
public sealed class NetworkPresetTests
{
    private const string RealtekGuid = "{A1000000-0000-0000-0000-000000000001}";
    private const string IntelGuid = "{A2000000-0000-0000-0000-000000000002}";
    private const string KillerGuid = "{A3000000-0000-0000-0000-000000000003}";
    private const string IntelWifiGuid = "{B1000000-0000-0000-0000-000000000001}";
    private const string KillerWifiGuid = "{B2000000-0000-0000-0000-000000000002}";
    private const string RealtekWifiGuid = "{B3000000-0000-0000-0000-000000000003}";

    private static readonly string[] OffOn = ["Disabled", "Enabled"];

    private static WindowsOperation Op(string id) => WindowsCatalog.Full.ById(id) ?? throw new InvalidOperationException(id);

    private static AdapterPropertyReading Enum(string keyword, string current, string[] registry, string[] display) =>
        new(keyword, keyword, current, display[Array.IndexOf(registry, current)], registry, display);

    private static Dictionary<string, AdapterPropertyReading> Props(params AdapterPropertyReading[] items) => items.ToDictionary(p => p.Keyword, StringComparer.OrdinalIgnoreCase);

    private static AdapterReading Ethernet(string guid, string name, string description, string vendor, int pci, Dictionary<string, AdapterPropertyReading> props, bool? power = true) =>
        new(guid, name, description, vendor, pci, "1.0", "2025-01-01", AdapterKind.Ethernet, true, true, props, new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase), power);

    private static AdapterReading Wifi(string guid, string description, string vendor, int? pci, Dictionary<string, AdapterPropertyReading> props, bool? power = null) =>
        new(guid, "Wi-Fi", description, vendor, pci, "24.70.0.3", "2026-07-29", AdapterKind.WiFi, true, true, props, new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase), power);

    // Realtek 2.5GbE as rtcx21x64.inf declares it: EnableGreenEthernet, *EEE, *InterruptModeration (all 0/1), *ReceiveBuffers 32..512.
    private static AdapterReading RealtekEth() => Ethernet(RealtekGuid, "Ethernet", "Realtek Gaming 2.5GbE Family Controller", "Realtek", 0x10EC, Props(
        Enum("EnableGreenEthernet", "1", ["0", "1"], OffOn), Enum("*EEE", "1", ["0", "1"], OffOn), Enum("*InterruptModeration", "1", ["0", "1"], OffOn),
        Enum("PowerSavingMode", "1", ["0", "1"], OffOn),
        new("*ReceiveBuffers", "Receive Buffers", "256", "256", [], [], 32, 512, 1)));

    // Intel I225 as nete1e3e.inf / net1ic64.inf declare it: *InterruptModeration, ReduceSpeedOnPowerDown, *ReceiveBuffers 80..2048 default 256.
    private static AdapterReading IntelEth() => Ethernet(IntelGuid, "Ethernet 2", "Intel(R) Ethernet Controller (3) I225-V", "Intel", 0x8086, Props(
        Enum("*InterruptModeration", "1", ["0", "1"], OffOn), Enum("*EEE", "1", ["0", "1"], OffOn), Enum("ReduceSpeedOnPowerDown", "1", ["0", "1"], OffOn),
        Enum("ULPMode", "1", ["0", "1"], OffOn), Enum("EnableGreenEthernet", "1", ["0", "1"], OffOn),
        new("*ReceiveBuffers", "Receive Buffers", "256", "256", [], [], 80, 2048, 1)));

    // Killer E2xxx as e2xw10x64.inf declares it: *InterruptModeration, *EEE, *ReceiveBuffers 16..2048 default 512; the PCI maker is Qualcomm, the name says Killer.
    private static AdapterReading KillerEth() => Ethernet(KillerGuid, "Ethernet 3", "Killer E2600 Gigabit Ethernet Controller", "Qualcomm", 0x1969, Props(
        Enum("*InterruptModeration", "1", ["0", "1"], OffOn), Enum("*EEE", "1", ["0", "1"], OffOn),
        new("*ReceiveBuffers", "Receive Buffers", "512", "512", [], [], 16, 2048, 1)));

    // Intel Wi-Fi 6 AX201, values and texts read from the laptop with Get-NetAdapterAdvancedProperty.
    private static Dictionary<string, AdapterPropertyReading> IntelWifiProps() => Props(
        Enum("MIMOPowerSaveMode", "0", ["0", "1", "2", "3"], ["Auto SMPS", "Static SMPS", "Dynamic SMPS", "No SMPS"]),
        Enum("RoamAggressiveness", "2", ["0", "1", "2", "3", "4"], ["1. Lowest", "2. Medium-Low", "3. Medium", "4. Medium-High", "5. Highest"]),
        Enum("RoamingPreferredBandType", "0", ["0", "1", "2"], ["1. No Preference", "2. Prefer 2.4GHz band", "3. Prefer 5GHz band"]));

    private static AdapterReading IntelWifi() => Wifi(IntelWifiGuid, "Intel(R) Wi-Fi 6 AX201 160MHz", "Intel", 0x8086, IntelWifiProps());

    // Killer Wi-Fi runs Intel's driver; the maker field can say Intel or Rivet Networks, the name says Killer.
    private static AdapterReading KillerWifi() => Wifi(KillerWifiGuid, "Killer(R) Wi-Fi 6 AX1650i 160MHz Wireless Network Adapter (201NGW)", "Rivet Networks", 0x8086, IntelWifiProps());

    private static AdapterReading RealtekWifi() => Wifi(RealtekWifiGuid, "Realtek 8822CE Wireless LAN 802.11ac PCI-E NIC", "Realtek", 0x10EC,
        Props(Enum("bLeisurePs", "2", ["2", "0", "1"], ["AUTO", "DISABLE", "ENABLE"])));

    private static FakeWindows Pc(params AdapterReading[] adapters) => new() { Adapters = [.. adapters] };

    private static AdapterReading Find(FakeWindows pc, string guid) => pc.Adapters!.Single(a => a.InterfaceGuid == guid);

    private static WindowsOpOutcome Apply(FakeWindows pc, string id) => new WindowsConfigurator(pc, pc, WindowsCatalog.Full).Apply([Op(id)], TestContext.Current.CancellationToken).Single();

    private static string Value(FakeWindows pc, string guid, string keyword) => Find(pc, guid).Properties[keyword].RegistryValue;

    private static FakeWindows EveryAdapter() => Pc(RealtekEth(), IntelEth(), KillerEth(), IntelWifi(), KillerWifi(), RealtekWifi());

    [Fact]
    public void The_realtek_ethernet_preset_reaches_only_the_realtek_card_and_writes_what_it_says()
    {
        var pc = EveryAdapter();

        var outcome = Apply(pc, "network.preset-realtek-ethernet");

        Assert.Equal(WindowsOpStatus.Applied, outcome.Status);
        Assert.Equal("0", Value(pc, RealtekGuid, "*InterruptModeration"));
        Assert.Equal("0", Value(pc, RealtekGuid, "*EEE"));
        Assert.Equal("0", Value(pc, RealtekGuid, "EnableGreenEthernet"));
        Assert.Equal("1", Value(pc, RealtekGuid, "PowerSavingMode"));   // not in the preset: it was never seen as a driver parameter, only as a registry value name
        Assert.Equal("512", Value(pc, RealtekGuid, "*ReceiveBuffers"));
        Assert.False(Find(pc, RealtekGuid).AllowTurnOff);
        Assert.All(pc.Writes, w => Assert.Contains(RealtekGuid, w, StringComparison.Ordinal));   // no other adapter was touched
    }

    [Fact]
    public void The_intel_ethernet_preset_reaches_only_the_intel_ethernet_card_and_not_the_wifi_card()
    {
        var pc = EveryAdapter();

        Assert.Equal(WindowsOpStatus.Applied, Apply(pc, "network.preset-intel-ethernet").Status);

        Assert.Equal("0", Value(pc, IntelGuid, "*InterruptModeration"));
        Assert.Equal("0", Value(pc, IntelGuid, "*EEE"));
        Assert.Equal("0", Value(pc, IntelGuid, "ReduceSpeedOnPowerDown"));
        Assert.Equal("0", Value(pc, IntelGuid, "ULPMode"));                  // declared by net1ic64.inf as an enumeration of 0 and 1
        Assert.Equal("1", Value(pc, IntelGuid, "EnableGreenEthernet"));      // not in any Intel INF read: the preset does not write it
        Assert.Equal("2048", Value(pc, IntelGuid, "*ReceiveBuffers"));
        Assert.All(pc.Writes, w => Assert.Contains(IntelGuid, w, StringComparison.Ordinal));
        Assert.Equal("0", Value(pc, IntelWifiGuid, "MIMOPowerSaveMode"));   // the Intel Wi-Fi card is another preset's
    }

    [Theory]
    [InlineData("NetworkAddress", true)]
    [InlineData("*networkaddress", true)]
    [InlineData("SerialNumber", true)]
    [InlineData("*InterruptModeration", false)]
    [InlineData("MIMOPowerSaveMode", false)]
    public void An_address_or_a_serial_number_among_the_driver_properties_is_never_read(string keyword, bool isPrivate)
    {
        Assert.Equal(isPrivate, AdapterPrivacy.IsPrivate(keyword));
        // and no preset sets one
        Assert.DoesNotContain(WindowsCatalog.Default.PresetById("network-adapter-makers")!.OperationIds.Select(id => WindowsCatalog.Default.ById(id)!), o => o.AdapterSettings.Any(s => AdapterPrivacy.IsPrivate(s.Key)));
    }

    [Fact]
    public void Virtual_vpn_and_tunnel_adapters_that_carry_a_makers_name_are_never_written()
    {
        // A VPN or Wi-Fi Direct adapter often names the maker of the card it rides on; the preset reaches physical adapters only.
        var vpn = RealtekEth() with { InterfaceGuid = "{C1000000-0000-0000-0000-000000000001}", Name = "VPN", Description = "Realtek PCIe GbE Family Controller - WireGuard Tunnel", Physical = false };
        var direct = IntelWifi() with { InterfaceGuid = "{C2000000-0000-0000-0000-000000000002}", Name = "Local Area Connection* 3", Description = "Microsoft Wi-Fi Direct Virtual Adapter (Intel)", Physical = false };
        var pc = Pc(vpn, direct);

        foreach (var id in new[] { "network.preset-realtek-ethernet", "network.preset-intel-wifi" })
        {
            Assert.NotEqual(WindowsOpStatus.Applied, Apply(pc, id).Status);
        }

        Assert.Empty(pc.Writes);
    }

    [Fact]
    public void A_card_is_addressed_by_its_interface_guid_and_two_cards_of_one_maker_are_both_set_and_both_put_back()
    {
        var second = RealtekEth() with { InterfaceGuid = "{A1000000-0000-0000-0000-000000000009}", Name = "Ethernet 9" };
        var pc = Pc(RealtekEth(), second);
        var configurator = new WindowsConfigurator(pc, pc, WindowsCatalog.Full);

        var applied = configurator.Apply([Op("network.preset-realtek-ethernet")], TestContext.Current.CancellationToken);

        Assert.Equal("0", Value(pc, RealtekGuid, "*InterruptModeration"));
        Assert.Equal("0", Value(pc, second.InterfaceGuid, "*InterruptModeration"));
        Assert.All(pc.Writes, w => Assert.True(w.Contains(RealtekGuid, StringComparison.Ordinal) || w.Contains(second.InterfaceGuid, StringComparison.Ordinal), w));   // by GUID, never by the name
        Assert.DoesNotContain(pc.Writes, w => w.Contains("Ethernet 9", StringComparison.Ordinal));

        configurator.Revert(applied);

        Assert.Equal("1", Value(pc, RealtekGuid, "*InterruptModeration"));
        Assert.Equal("1", Value(pc, second.InterfaceGuid, "*InterruptModeration"));
    }

    [Fact]
    public void A_driver_that_does_not_keep_the_values_fails_the_preset_and_leaves_the_card_as_it_was()
    {
        var pc = Pc(RealtekEth());
        pc.DriverKeeps = false;   // the driver answers the write and reports the old value again

        var outcome = Apply(pc, "network.preset-realtek-ethernet");

        Assert.Equal(WindowsOpStatus.Failed, outcome.Status);
        Assert.Empty(outcome.Changes);   // nothing is left recorded as changed
        Assert.Equal("1", Value(pc, RealtekGuid, "*InterruptModeration"));
        Assert.Equal("1", Value(pc, RealtekGuid, "*EEE"));
    }

    [Fact]
    public void A_card_that_does_not_come_back_up_fails_the_preset_and_what_was_changed_is_put_back()
    {
        var pc = Pc(IntelEth());
        pc.AdapterStaysDown = true;

        var outcome = Apply(pc, "network.preset-intel-ethernet");

        Assert.Equal(WindowsOpStatus.Failed, outcome.Status);
        Assert.Contains("didn't come back up", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void The_killer_ethernet_preset_finds_the_card_by_its_name_although_the_chip_maker_is_another()
    {
        var pc = EveryAdapter();

        Assert.Equal(WindowsOpStatus.Applied, Apply(pc, "network.preset-killer-ethernet").Status);

        Assert.Equal("0", Value(pc, KillerGuid, "*InterruptModeration"));
        Assert.Equal("0", Value(pc, KillerGuid, "*EEE"));
        Assert.Equal("2048", Value(pc, KillerGuid, "*ReceiveBuffers"));
        Assert.All(pc.Writes, w => Assert.Contains(KillerGuid, w, StringComparison.Ordinal));
    }

    [Fact]
    public void The_wifi_preset_reaches_intel_and_killer_wifi_cards_and_nothing_else()
    {
        var pc = EveryAdapter();

        Assert.Equal(WindowsOpStatus.Applied, Apply(pc, "network.preset-intel-wifi").Status);

        foreach (var guid in new[] { IntelWifiGuid, KillerWifiGuid })
        {
            Assert.Equal("3", Value(pc, guid, "MIMOPowerSaveMode"));          // No SMPS
            Assert.Equal("0", Value(pc, guid, "RoamAggressiveness"));         // 1. Lowest
            Assert.Equal("2", Value(pc, guid, "RoamingPreferredBandType"));   // 3. Prefer 5GHz band
        }

        Assert.DoesNotContain(pc.Writes, w => w.Contains(RealtekGuid, StringComparison.Ordinal) || w.Contains(IntelGuid, StringComparison.Ordinal) || w.Contains(KillerGuid, StringComparison.Ordinal) || w.Contains(RealtekWifiGuid, StringComparison.Ordinal));
    }

    [Fact]
    public void The_realtek_wifi_preset_turns_leisure_power_save_off_on_the_realtek_wifi_card_only()
    {
        var pc = EveryAdapter();

        Assert.Equal(WindowsOpStatus.Applied, Apply(pc, "network.preset-realtek-wifi").Status);

        Assert.Equal("0", Value(pc, RealtekWifiGuid, "bLeisurePs"));
        Assert.All(pc.Writes, w => Assert.Contains(RealtekWifiGuid, w, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("network.preset-realtek-ethernet")]
    [InlineData("network.preset-intel-ethernet")]
    [InlineData("network.preset-killer-ethernet")]
    [InlineData("network.preset-intel-wifi")]
    [InlineData("network.preset-realtek-wifi")]
    public void A_preset_writes_nothing_on_a_pc_without_its_makers_card(string id)
    {
        var elsewhere = id.Contains("realtek", StringComparison.Ordinal) ? Pc(IntelEth(), IntelWifi()) : id.Contains("killer", StringComparison.Ordinal) ? Pc(RealtekEth(), RealtekWifi()) : Pc(RealtekEth(), RealtekWifi(), KillerEth());

        var outcome = Apply(elsewhere, id);

        Assert.Empty(elsewhere.Writes);
        Assert.NotEqual(WindowsOpStatus.Applied, outcome.Status);
    }

    [Fact]
    public void A_preset_is_undone_to_the_drivers_own_previous_values()
    {
        var pc = Pc(IntelEth());
        var configurator = new WindowsConfigurator(pc, pc, WindowsCatalog.Full);

        var applied = configurator.Apply([Op("network.preset-intel-ethernet")], TestContext.Current.CancellationToken);
        Assert.Equal("0", Value(pc, IntelGuid, "*InterruptModeration"));

        var reverted = configurator.Revert(applied).Single();

        Assert.Equal(WindowsOpStatus.Reverted, reverted.Status);
        Assert.Equal("1", Value(pc, IntelGuid, "*InterruptModeration"));
        Assert.Equal("1", Value(pc, IntelGuid, "*EEE"));
        Assert.Equal("1", Value(pc, IntelGuid, "ReduceSpeedOnPowerDown"));
        Assert.Equal("256", Value(pc, IntelGuid, "*ReceiveBuffers"));
        Assert.True(Find(pc, IntelGuid).AllowTurnOff);
    }

    [Fact]
    public void Every_setting_of_a_preset_names_its_makers_and_none_touches_what_no_source_supports()
    {
        string[] unsupported = ["*FlowControl", "*LsoV1IPv4", "*LsoV2IPv4", "*LsoV2IPv6", "*TCPChecksumOffloadIPv4", "*UDPChecksumOffloadIPv4", "*TCPChecksumOffloadIPv6", "*UDPChecksumOffloadIPv6", "*IPChecksumOffloadIPv4", "*RscIPv4", "*RscIPv6", "*RSS", "*NumRssQueues", "*JumboPacket", "*SpeedDuplex", "ThroughputBoosterEnabled", "uAPSDSupport"];
        var preset = WindowsCatalog.Full.PresetById("network-adapter-makers");
        Assert.NotNull(preset);
        Assert.Equal(5, preset.OperationIds.Count);
        foreach (var id in preset.OperationIds)
        {
            var op = Op(id);
            Assert.NotEmpty(op.AdapterSettings);
            Assert.All(op.AdapterSettings, s => Assert.NotEmpty(s.Vendors));
            Assert.DoesNotContain(op.AdapterSettings, s => s.Kind == AdapterSettingKind.Property && unsupported.Contains(s.Key, StringComparer.OrdinalIgnoreCase));
            Assert.False(string.IsNullOrWhiteSpace(op.Source));
            Assert.Equal(OperationBenefit.Unverified, op.Evidence);
        }
    }
}
