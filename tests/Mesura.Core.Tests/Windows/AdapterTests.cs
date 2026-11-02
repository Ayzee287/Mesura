using Mesura.Core.Windows;

namespace Mesura.Core.Tests.Windows;

/// <summary>
/// Network adapter operations (0.5): what counts as an adapter Mesura may tune, which value a setting would write on which adapter, and how the engine applies,
/// verifies and undoes it per adapter. The Windows side (the WMI reader, the cmdlets) is not covered here: these tests pin the decisions around it.
/// </summary>
public sealed class AdapterTests
{
    private const string WifiGuid = "{11111111-1111-1111-1111-111111111111}";
    private const string EthGuid = "{22222222-2222-2222-2222-222222222222}";
    private const string VpnGuid = "{33333333-3333-3333-3333-333333333333}";

    private static WindowsOperation Op(string id) => EngineFixtures.Op(id);

    private static AdapterPropertyReading Enum(string keyword, string current, string[] registry, string[] display) =>
        new(keyword, keyword, current, display[Array.IndexOf(registry, current)], registry, display);

    private static AdapterReading IntelWifi(bool up = true) => new(WifiGuid, "Wi-Fi", "Intel(R) Wi-Fi 6 AX201 160MHz", "Intel", 0x8086, "23.80.0.7", "2026-07-29", AdapterKind.WiFi, true, up,
        new Dictionary<string, AdapterPropertyReading>(StringComparer.OrdinalIgnoreCase)
        {
            ["MIMOPowerSaveMode"] = Enum("MIMOPowerSaveMode", "0", ["0", "1", "2", "3"], ["Automatic SMPS", "Static SMPS", "Dynamic SMPS", "No SMPS"]),
            ["RoamAggressiveness"] = Enum("RoamAggressiveness", "2", ["0", "1", "2", "3", "4"], ["1. Lowest", "2. Medium-Low", "3. Medium", "4. Medium-High", "5. Highest"]),
            ["*InterruptModeration"] = Enum("*InterruptModeration", "1", ["0", "1"], ["Disabled", "Enabled"]),
        },
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["ms_lldp"] = true, ["ms_tcpip6"] = true, ["ms_server"] = true },
        true);

    private static AdapterReading RealtekEthernet() => new(EthGuid, "Ethernet", "Realtek PCIe 2.5GbE Family Controller", "Realtek", 0x10EC, "10.70", "2025-03-01", AdapterKind.Ethernet, true, true,
        new Dictionary<string, AdapterPropertyReading>(StringComparer.OrdinalIgnoreCase)
        {
            ["*InterruptModeration"] = Enum("*InterruptModeration", "1", ["0", "1"], ["Disabled", "Enabled"]),
            ["*ReceiveBuffers"] = new("*ReceiveBuffers", "Receive Buffers", "512", "512", [], [], 32, 2048, 8),
            ["*EEE"] = Enum("*EEE", "1", ["0", "1"], ["Disabled", "Enabled"]),
            ["ULPMode"] = Enum("ULPMode", "1", ["0", "1"], ["Disabled", "Enabled"]),
        },
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["ms_lldp"] = true },
        null);

    private static AdapterReading Tunnel() => new(VpnGuid, "WireGuard", "WireGuard Tunnel", "WireGuard", null, "0.5", "2025-01-01", AdapterKind.Vpn, false, true,
        new Dictionary<string, AdapterPropertyReading>(StringComparer.OrdinalIgnoreCase) { ["*InterruptModeration"] = Enum("*InterruptModeration", "1", ["0", "1"], ["Disabled", "Enabled"]) },
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["ms_lldp"] = true },
        true);

    private static FakeWindows Pc(params AdapterReading[] adapters) => new() { Adapters = [.. adapters] };

    private static AdapterReading Find(FakeWindows pc, string guid) => pc.Adapters!.Single(a => a.InterfaceGuid == guid);

    // ---- what an adapter is --------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Intel(R) Ethernet Controller I225-V", 6, 14, true, false, AdapterKind.Ethernet)]
    [InlineData("Realtek USB GbE Family Controller", 6, 14, true, false, AdapterKind.Ethernet)]
    [InlineData("Intel(R) Wi-Fi 6 AX201 160MHz", 71, 9, true, false, AdapterKind.WiFi)]
    [InlineData("Hyper-V Virtual Ethernet Adapter", 6, 14, false, true, AdapterKind.Virtual)]
    [InlineData("VMware Virtual Ethernet Adapter for VMnet8", 6, 14, true, false, AdapterKind.Virtual)]
    [InlineData("Microsoft Wi-Fi Direct Virtual Adapter", 71, 9, false, true, AdapterKind.Virtual)]
    [InlineData("TAP-Windows Adapter V9", 6, 14, false, false, AdapterKind.Vpn)]
    [InlineData("WireGuard Tunnel", 53, null, false, true, AdapterKind.Vpn)]
    [InlineData("Bluetooth Device (Personal Area Network)", 6, 14, true, false, AdapterKind.Other)]
    [InlineData("Remote NDIS based Internet Sharing Device", 6, 14, true, false, AdapterKind.Other)]
    public void An_adapter_is_classified_by_what_windows_says_it_is(string description, int interfaceType, int? medium, bool hardware, bool virtualFlag, AdapterKind expected) =>
        Assert.Equal(expected, AdapterClassifier.Classify(description, interfaceType, medium, hardware, virtualFlag));

    [Fact]
    public void The_maker_comes_from_the_driver_provider_and_else_from_the_pci_vendor_id()
    {
        Assert.Equal("Intel", AdapterClassifier.Vendor("Microsoft", 0x8086));
        Assert.Equal("Realtek", AdapterClassifier.Vendor("Realtek", 0x10EC));
        Assert.Equal("Realtek", AdapterClassifier.Vendor("", 0x10EC));
        Assert.Equal("Intel Corporation", AdapterClassifier.Vendor("Intel Corporation", 0x8086));
        Assert.Equal("Microsoft", AdapterClassifier.Vendor("Microsoft", null));
        Assert.Equal("", AdapterClassifier.Vendor(null, null));
    }

    [Fact]
    public void The_pci_vendor_is_read_from_the_pnp_device_id_and_a_usb_device_has_none()
    {
        Assert.Equal(0x8086, AdapterClassifier.PciVendor(@"PCI\VEN_8086&DEV_A0F0&SUBSYS_00748086&REV_20\3&11583659&0&A3"));
        Assert.Equal(0x10EC, AdapterClassifier.PciVendor(@"PCI\VEN_10EC&DEV_8125&SUBSYS_00000000&REV_05\4&2"));
        Assert.Null(AdapterClassifier.PciVendor(@"USB\VID_0BDA&PID_8153\000001"));
        Assert.Null(AdapterClassifier.PciVendor(null));
        Assert.Null(AdapterClassifier.PciVendor(@"PCI\VEN_"));
    }

    // ---- which value a setting writes where ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_registry_value_is_wanted_only_where_the_driver_lists_it()
    {
        var setting = Op("network.adapter-interrupt-moderation-off").AdapterSettings.Single();

        Assert.Equal("0", setting.Wanted(IntelWifi()));

        var withoutZero = IntelWifi() with
        {
            Properties = new Dictionary<string, AdapterPropertyReading>(StringComparer.OrdinalIgnoreCase)
            {
                ["*InterruptModeration"] = Enum("*InterruptModeration", "1", ["1", "2"], ["Enabled", "Adaptive"]),
            },
        };
        Assert.Null(setting.Wanted(withoutZero)); // the driver doesn't list 0: nothing is written

        Assert.Null(setting.Wanted(IntelWifi() with { Properties = new Dictionary<string, AdapterPropertyReading>() })); // the driver has no such keyword
    }

    [Fact]
    public void A_display_value_is_looked_up_in_the_drivers_own_list_so_a_vendor_number_never_has_to_be_guessed()
    {
        Assert.Equal("3", Op("network.adapter-wifi-mimo-power-save-off").AdapterSettings.Single().Wanted(IntelWifi()));
        Assert.Equal("0", Op("network.adapter-wifi-roaming-lowest").AdapterSettings.Single().Wanted(IntelWifi()));

        // The same driver listing the entries in another order maps to its own numbers.
        var reordered = IntelWifi() with
        {
            Properties = new Dictionary<string, AdapterPropertyReading>(StringComparer.OrdinalIgnoreCase)
            {
                ["MIMOPowerSaveMode"] = Enum("MIMOPowerSaveMode", "1", ["1", "5", "7"], ["Static SMPS", "No SMPS", "Dynamic SMPS"]),
            },
        };
        Assert.Equal("5", Op("network.adapter-wifi-mimo-power-save-off").AdapterSettings.Single().Wanted(reordered));

        var noSuchEntry = IntelWifi() with
        {
            Properties = new Dictionary<string, AdapterPropertyReading>(StringComparer.OrdinalIgnoreCase)
            {
                ["MIMOPowerSaveMode"] = Enum("MIMOPowerSaveMode", "0", ["0", "1"], ["Automatic SMPS", "Static SMPS"]),
            },
        };
        Assert.Null(Op("network.adapter-wifi-mimo-power-save-off").AdapterSettings.Single().Wanted(noSuchEntry));
    }

    [Fact]
    public void The_maximum_is_the_largest_value_the_driver_allows_and_needs_a_range()
    {
        var setting = Op("network.adapter-receive-buffers-max").AdapterSettings.Single();

        Assert.Equal("2048", setting.Wanted(RealtekEthernet()));

        var noRange = RealtekEthernet() with
        {
            Properties = new Dictionary<string, AdapterPropertyReading>(StringComparer.OrdinalIgnoreCase)
            {
                ["*ReceiveBuffers"] = new("*ReceiveBuffers", "Receive Buffers", "512", "512", [], []),
            },
        };
        Assert.Null(setting.Wanted(noRange));
    }

    [Fact]
    public void A_binding_and_the_power_switch_are_wanted_only_where_the_adapter_has_them()
    {
        var lldp = Op("network.adapter-lldp-off").AdapterSettings.Single();
        var lltd = Op("network.adapter-lltd-off").AdapterSettings; // the mapper and the responder
        var power = Op("network.adapter-power-management-off").AdapterSettings.Single();

        Assert.Equal("False", lldp.Wanted(IntelWifi()));
        Assert.All(lltd, s => Assert.Null(s.Wanted(IntelWifi()))); // no such binding on this adapter
        Assert.Equal("False", power.Wanted(IntelWifi()));
        Assert.Null(power.Wanted(RealtekEthernet())); // the driver doesn't say
    }

    [Fact]
    public void A_setting_is_meant_only_for_physical_adapters_of_its_kinds_and_makers()
    {
        var wifiOnly = Op("network.adapter-wifi-mimo-power-save-off").AdapterSettings.Single();
        var ethernetOnly = Op("network.adapter-eee-off").AdapterSettings.Single();
        var any = Op("network.adapter-interrupt-moderation-off").AdapterSettings.Single();
        var vendor = Op("network.adapter-vendor-power-saving-off").AdapterSettings.First();

        Assert.True(wifiOnly.Matches(IntelWifi()));
        Assert.False(wifiOnly.Matches(RealtekEthernet()));
        Assert.False(ethernetOnly.Matches(IntelWifi()));
        Assert.True(ethernetOnly.Matches(RealtekEthernet()));
        Assert.False(any.Matches(Tunnel())); // a tunnel is never tuned, whatever it declares
        Assert.False(any.Matches(RealtekEthernet() with { Physical = false }));
        Assert.Contains("Intel", string.Join(',', vendor.Vendors.Concat(Op("network.adapter-vendor-power-saving-off").AdapterSettings.SelectMany(s => s.Vendors))));
        Assert.All(Op("network.adapter-vendor-power-saving-off").AdapterSettings, s => Assert.NotEmpty(s.Vendors));
    }

    [Fact]
    public void A_vendor_keyword_is_never_written_to_another_makers_card()
    {
        var pc = Pc(RealtekEthernet());
        var intelOnly = Op("network.adapter-vendor-power-saving-off").AdapterSettings.Where(s => s.Vendors.All(v => v.Equals("Intel", StringComparison.OrdinalIgnoreCase))).ToList();

        Assert.NotEmpty(intelOnly);
        Assert.All(intelOnly, s => Assert.False(s.Matches(pc.Adapters![0])));
    }

    // ---- the engine --------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_preview_lists_each_adapter_that_can_take_the_setting_and_skips_the_rest()
    {
        var pc = Pc(IntelWifi(), RealtekEthernet(), Tunnel());

        var evaluation = WindowsEvaluator.Evaluate(Op("network.adapter-interrupt-moderation-off"), pc);

        Assert.Equal(OperationState.NotApplied, evaluation.State);
        Assert.Equal(2, evaluation.Items.Count);
        Assert.All(evaluation.Items, i => Assert.True(i.Current == "1" && i.Desired == "0" && !i.Matches));
        Assert.DoesNotContain(evaluation.Items, i => i.Target.Contains("WireGuard", StringComparison.Ordinal));
    }

    [Fact]
    public void An_operation_no_adapter_can_take_is_not_available_and_unreadable_adapters_are_unknown()
    {
        Assert.Equal(OperationState.Inapplicable, WindowsEvaluator.Evaluate(Op("network.adapter-eee-off"), Pc(IntelWifi())).State);
        Assert.Equal(OperationState.Inapplicable, WindowsEvaluator.Evaluate(Op("network.adapter-lldp-off"), Pc()).State);
        Assert.Equal(OperationState.Unknown, WindowsEvaluator.Evaluate(Op("network.adapter-lldp-off"), new FakeWindows { Adapters = null }).State);
    }

    [Fact]
    public void Applying_writes_each_matching_adapter_reads_it_back_and_the_state_becomes_applied()
    {
        var pc = Pc(IntelWifi(), RealtekEthernet(), Tunnel());
        var op = Op("network.adapter-interrupt-moderation-off");

        var outcome = new WindowsConfigurator(pc, pc).Apply([op], TestContext.Current.CancellationToken).Single();

        Assert.Equal(WindowsOpStatus.Applied, outcome.Status);
        Assert.Equal("0", Find(pc, WifiGuid).Properties["*InterruptModeration"].RegistryValue);
        Assert.Equal("0", Find(pc, EthGuid).Properties["*InterruptModeration"].RegistryValue);
        Assert.Equal("1", Find(pc, VpnGuid).Properties["*InterruptModeration"].RegistryValue);
        Assert.DoesNotContain(pc.Writes, w => w.Contains(VpnGuid, StringComparison.Ordinal));
        Assert.All(outcome.Changes, c => Assert.Equal(("1", "0"), (c.Before, c.After)));
        Assert.Equal(OperationState.Applied, WindowsEvaluator.Evaluate(op, pc).State);
        Assert.Equal(WindowsOpStatus.AlreadyApplied, new WindowsConfigurator(pc, pc).Apply([op], TestContext.Current.CancellationToken).Single().Status);
    }

    [Fact]
    public void A_binding_and_the_power_switch_are_applied_and_undone_with_their_before_image()
    {
        var pc = Pc(IntelWifi());
        var configurator = new WindowsConfigurator(pc, pc, WindowsCatalog.Full);
        var ops = new[] { Op("network.adapter-lldp-off"), Op("network.adapter-power-management-off") };

        var applied = configurator.Apply(ops, TestContext.Current.CancellationToken);

        Assert.All(applied, o => Assert.Equal(WindowsOpStatus.Applied, o.Status));
        Assert.False(Find(pc, WifiGuid).Bindings["ms_lldp"]);
        Assert.False(Find(pc, WifiGuid).AllowTurnOff);
        Assert.True(Find(pc, WifiGuid).Bindings["ms_tcpip6"]); // nothing else was touched

        var reverted = configurator.Revert(applied);

        Assert.All(reverted, o => Assert.Equal(WindowsOpStatus.Reverted, o.Status));
        Assert.True(Find(pc, WifiGuid).Bindings["ms_lldp"]);
        Assert.True(Find(pc, WifiGuid).AllowTurnOff);
    }

    [Fact]
    public void Undo_puts_the_drivers_own_previous_value_back_per_adapter()
    {
        var wifi = IntelWifi();
        var pc = Pc(wifi);
        var configurator = new WindowsConfigurator(pc, pc, WindowsCatalog.Full);
        var op = Op("network.adapter-wifi-roaming-lowest");

        var applied = configurator.Apply([op], TestContext.Current.CancellationToken);
        Assert.Equal("0", Find(pc, WifiGuid).Properties["RoamAggressiveness"].RegistryValue);

        var reverted = configurator.Revert(applied).Single();

        Assert.Equal(WindowsOpStatus.Reverted, reverted.Status);
        Assert.Equal("2", Find(pc, WifiGuid).Properties["RoamAggressiveness"].RegistryValue);
    }

    [Fact]
    public void A_driver_that_does_not_keep_the_value_fails_the_operation_and_nothing_is_left_changed()
    {
        var pc = Pc(IntelWifi(), RealtekEthernet());
        pc.DriverKeeps = false;

        var outcome = new WindowsConfigurator(pc, pc).Apply([Op("network.adapter-interrupt-moderation-off")], TestContext.Current.CancellationToken).Single();

        Assert.Equal(WindowsOpStatus.Failed, outcome.Status);
        Assert.Equal("1", Find(pc, WifiGuid).Properties["*InterruptModeration"].RegistryValue);
        Assert.Equal("1", Find(pc, EthGuid).Properties["*InterruptModeration"].RegistryValue);
        Assert.Contains("doesn't report", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_adapter_that_does_not_come_back_up_fails_the_operation()
    {
        var pc = Pc(IntelWifi());
        pc.AdapterStaysDown = true;

        var outcome = new WindowsConfigurator(pc, pc).Apply([Op("network.adapter-wifi-mimo-power-save-off")], TestContext.Current.CancellationToken).Single();

        Assert.Equal(WindowsOpStatus.Failed, outcome.Status);
        Assert.Contains("up", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_write_on_the_second_adapter_puts_the_first_one_back()
    {
        var pc = Pc(IntelWifi(), RealtekEthernet());
        pc.FailAdapterKey = "*InterruptModeration";
        var first = pc.Adapters![0];

        var outcome = new WindowsConfigurator(pc, pc).Apply([Op("network.adapter-interrupt-moderation-off")], TestContext.Current.CancellationToken).Single();

        Assert.Equal(WindowsOpStatus.Failed, outcome.Status);
        Assert.Equal(first.Properties["*InterruptModeration"].RegistryValue, Find(pc, WifiGuid).Properties["*InterruptModeration"].RegistryValue);
        Assert.Equal("1", Find(pc, EthGuid).Properties["*InterruptModeration"].RegistryValue);
    }

    [Fact]
    public void Adapters_that_cannot_be_read_stop_the_change_before_anything_is_written()
    {
        var pc = new FakeWindows { Adapters = null };

        var outcome = new WindowsConfigurator(pc, pc).Apply([Op("network.adapter-lldp-off")], TestContext.Current.CancellationToken).Single();

        Assert.NotEqual(WindowsOpStatus.Applied, outcome.Status);
        Assert.Empty(pc.Writes);
    }

    [Fact]
    public void A_record_of_an_adapter_change_is_only_undone_for_an_operation_that_declares_it()
    {
        var pc = Pc(IntelWifi());
        var applied = new WindowsConfigurator(pc, pc, WindowsCatalog.Full).Apply([Op("network.adapter-lldp-off")], TestContext.Current.CancellationToken).Single();
        var change = Assert.Single(applied.Changes);

        Assert.True(Op("network.adapter-lldp-off").Declares(change));
        Assert.False(Op("network.adapter-lltd-off").Declares(change)); // another binding
        Assert.False(Op("network.adapter-interrupt-moderation-off").Declares(change)); // another kind of change
    }

    [Fact]
    public void An_operation_says_how_its_state_is_confirmed_and_the_weakest_source_decides()
    {
        Assert.Equal(Confirmation.SystemReported, Op("network.adapter-lldp-off").Confirmation); // the driver's own report, read back after the change
        Assert.Equal(Confirmation.SystemReported, Op("power.mesura-maximum-plan").Confirmation);
        Assert.Equal(Confirmation.InEffect, Op("gpu.hardware-scheduling-on").Confirmation); // stored value plus an independent effect check
        Assert.Equal(Confirmation.Stored, WindowsCatalog.Default.Operations.First(o => o.Applicable && o.Registry.Count > 0 && o.Effect is null).Confirmation);
        Assert.All(WindowsCatalog.Default.Operations.Where(o => o.Applicable && o.Registry.Count > 0 && o.Effect is null), o => Assert.Equal(Confirmation.Stored, o.Confirmation));
    }

    [Fact]
    public void Every_adapter_entry_in_the_catalog_is_for_ethernet_or_wifi_only_and_uses_a_whitelisted_binding()
    {
        var settings = WindowsCatalog.Full.Operations.SelectMany(o => o.AdapterSettings).ToList();

        Assert.NotEmpty(settings);
        Assert.All(settings, s => Assert.All(s.Kinds, k => Assert.True(k is AdapterKind.Ethernet or AdapterKind.WiFi)));
        Assert.All(settings.Where(s => s.Kind == AdapterSettingKind.Binding), s => Assert.Contains(s.Key, new[] { "ms_lldp", "ms_lltdio", "ms_rspndr", "ms_implat", "ms_tcpip6", "ms_server", "ms_msclient", "ms_pacer" }));
    }
}
