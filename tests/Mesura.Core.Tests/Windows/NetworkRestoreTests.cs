using Mesura.Core.Windows;

namespace Mesura.Core.Tests.Windows;

/// <summary>
/// Network snapshot and restore (0.6): what a snapshot holds, how a restore plan is decided from fresh readings, and how a restore writes and verifies. The Windows side (WMI reader, cmdlets) is not
/// covered: these tests pin the decisions, with the stand-in adapter of the configurator tests. Nothing here touched a real adapter.
/// </summary>
public sealed class NetworkRestoreTests
{
    private const string EthGuid = "{22222222-2222-2222-2222-222222222222}";
    private const string WifiGuid = "{11111111-1111-1111-1111-111111111111}";
    private const string VpnGuid = "{33333333-3333-3333-3333-333333333333}";
    private static readonly DateTimeOffset Taken = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);

    private static AdapterPropertyReading Enum(string keyword, string current, string[] registry, string[] display) =>
        new(keyword, keyword, current, display[Array.IndexOf(registry, current)], registry, display);

    private static AdapterReading Ethernet(string itr = "1", string buffers = "512", bool lldp = true, bool? allow = true, string driver = "10.70", bool up = true) =>
        new(EthGuid, "Ethernet", "Realtek PCIe 2.5GbE Family Controller", "Realtek", 0x10EC, driver, "2025-03-01", AdapterKind.Ethernet, true, up,
            new Dictionary<string, AdapterPropertyReading>(StringComparer.OrdinalIgnoreCase)
            {
                ["*InterruptModeration"] = Enum("*InterruptModeration", itr, ["0", "1"], ["Disabled", "Enabled"]),
                ["*ReceiveBuffers"] = new("*ReceiveBuffers", "Receive Buffers", buffers, buffers, [], [], 32, 2048, 8),
            },
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["ms_lldp"] = lldp },
            allow);

    private static AdapterReading Wifi() => new(WifiGuid, "Wi-Fi", "Intel(R) Wi-Fi 6 AX201 160MHz", "Intel", 0x8086, "23.80.0.7", "2026-07-29", AdapterKind.WiFi, true, true,
        new Dictionary<string, AdapterPropertyReading>(StringComparer.OrdinalIgnoreCase) { ["RoamAggressiveness"] = Enum("RoamAggressiveness", "2", ["0", "1", "2", "3", "4"], ["1", "2", "3", "4", "5"]) },
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
        null);

    private static AdapterReading Tunnel() => new(VpnGuid, "WireGuard", "WireGuard Tunnel", "WireGuard", null, "0.5", "2025-01-01", AdapterKind.Vpn, false, true,
        new Dictionary<string, AdapterPropertyReading>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase), null);

    private static NetworkSnapshot Snap(params AdapterReading[] adapters) => NetworkSnapshot.From(adapters, Taken, "test");

    private static FakeWindows Pc(params AdapterReading[] adapters) => new() { Adapters = [.. adapters] };

    private static NetworkRestoreResult Restore(NetworkSnapshot snapshot, FakeWindows pc)
    {
        var plan = NetworkRestorer.Plan(snapshot, pc.ReadAdapters()!);
        return NetworkRestorer.Apply(plan, pc.ReadAdapters, pc);
    }

    // ---- the snapshot ---------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_snapshot_holds_physical_ethernet_and_wifi_only()
    {
        var snapshot = Snap(Ethernet(), Wifi(), Tunnel());
        Assert.Equal(2, snapshot.Adapters.Count);
        Assert.DoesNotContain(snapshot.Adapters, a => a.InterfaceGuid == VpnGuid);
        var eth = snapshot.Adapters.Single(a => a.InterfaceGuid == EthGuid);
        Assert.Equal(2, eth.Properties.Count);
        Assert.Equal("1", eth.Properties.Single(p => p.Keyword == "*InterruptModeration").RegistryValue);
        Assert.True(eth.Bindings["ms_lldp"]);
        Assert.True(eth.AllowTurnOff);
    }

    [Fact]
    public void A_snapshot_survives_json_and_keeps_case_insensitive_bindings()
    {
        var snapshot = Snap(Ethernet(), Wifi());
        var back = NetworkSnapshot.FromJson(snapshot.ToJson());
        Assert.NotNull(back);
        Assert.Equal(snapshot.TakenAt, back.TakenAt);
        Assert.Equal(snapshot.Adapters.Count, back.Adapters.Count);
        Assert.Equal(snapshot.Adapters[0].Properties, back.Adapters[0].Properties);
        Assert.True(back.Adapters.Single(a => a.InterfaceGuid == EthGuid).Bindings["MS_LLDP"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"version\": 99, \"adapters\": []}")]
    [InlineData("[1,2,3]")]
    public void Something_that_is_not_a_snapshot_is_refused(string text) => Assert.Null(NetworkSnapshot.FromJson(text));

    // ---- the plan -------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Unchanged_adapters_have_nothing_to_restore()
    {
        var plan = NetworkRestorer.Plan(Snap(Ethernet(), Wifi()), [Ethernet(), Wifi()]);
        Assert.Equal(0, plan.Differences);
        Assert.Equal(0, plan.Writes);
        Assert.All(plan.Adapters, a => Assert.True(a.Present));
        Assert.Equal(4, plan.Adapters.Single(a => a.InterfaceGuid == EthGuid).Unchanged);   // two properties, one binding, the power switch
    }

    [Fact]
    public void A_property_a_binding_and_the_power_switch_that_moved_are_planned_back()
    {
        var snapshot = Snap(Ethernet());
        var plan = NetworkRestorer.Plan(snapshot, [Ethernet(itr: "0", buffers: "2048", lldp: false, allow: false)]);
        var items = plan.Adapters.Single().Items;
        Assert.Equal(4, items.Count);
        Assert.All(items, i => Assert.True(i.Writable));
        Assert.Equal(("0", "1"), (items.Single(i => i.Key == "*InterruptModeration").Current, items.Single(i => i.Key == "*InterruptModeration").Wanted));
        Assert.Equal(("2048", "512"), (items.Single(i => i.Key == "*ReceiveBuffers").Current, items.Single(i => i.Key == "*ReceiveBuffers").Wanted));
        Assert.Equal(("False", "True"), (items.Single(i => i.Kind == AdapterSettingKind.Binding).Current, items.Single(i => i.Kind == AdapterSettingKind.Binding).Wanted));
        Assert.Equal(("False", "True"), (items.Single(i => i.Kind == AdapterSettingKind.PowerManagement).Current, items.Single(i => i.Kind == AdapterSettingKind.PowerManagement).Wanted));
    }

    [Fact]
    public void A_value_the_driver_no_longer_lists_is_shown_and_not_written()
    {
        var snapshot = Snap(Ethernet(buffers: "4096"));                 // the old driver allowed 4096
        var plan = NetworkRestorer.Plan(snapshot, [Ethernet(buffers: "512")]);
        var item = Assert.Single(plan.Adapters.Single().Items);
        Assert.False(item.Writable);
        Assert.Contains("does not list the value '4096'", item.SkipReason);
        Assert.Equal(0, plan.Writes);
        Assert.Equal(1, plan.Skipped);
    }

    [Fact]
    public void A_setting_the_driver_no_longer_declares_is_skipped_with_its_reason()
    {
        var before = Ethernet();
        var now = before with { Properties = before.Properties.Where(p => p.Key != "*InterruptModeration").ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase), AllowTurnOff = null };
        var items = NetworkRestorer.Plan(Snap(before), [now]).Adapters.Single().Items;
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.False(i.Writable));
        Assert.Contains(items, i => i.Key == "*InterruptModeration" && i.SkipReason!.Contains("no longer declares", StringComparison.Ordinal));
        Assert.Contains(items, i => i.Kind == AdapterSettingKind.PowerManagement && i.SkipReason!.Contains("power-management", StringComparison.Ordinal));
    }

    [Fact]
    public void A_missing_adapter_a_new_adapter_and_a_changed_kind_are_told_apart()
    {
        var snapshot = Snap(Ethernet(), Wifi());
        var plan = NetworkRestorer.Plan(snapshot, [Wifi() with { Kind = AdapterKind.Virtual }, Ethernet() with { InterfaceGuid = "{99999999-9999-9999-9999-999999999999}", Description = "New card" }]);
        var eth = plan.Adapters.Single(a => a.InterfaceGuid == EthGuid);
        Assert.False(eth.Present);
        Assert.Empty(eth.Items);
        var wifi = plan.Adapters.Single(a => a.InterfaceGuid == WifiGuid);
        Assert.True(wifi.Present);
        Assert.Empty(wifi.Items);
        Assert.Contains("leaves it alone", wifi.Notes.Single());
        Assert.Single(plan.NewAdapters);
        Assert.Contains("New card", plan.NewAdapters[0]);
    }

    [Fact]
    public void A_driver_that_changed_is_said_and_the_adapters_are_matched_by_guid_in_any_case()
    {
        var plan = NetworkRestorer.Plan(Snap(Ethernet()), [Ethernet(driver: "10.80") with { InterfaceGuid = EthGuid.ToLowerInvariant() }]);
        var adapter = plan.Adapters.Single();
        Assert.True(adapter.Present);
        Assert.Contains("driver changed", adapter.Notes.Single());
        Assert.Contains("10.70", adapter.Notes.Single());
    }

    // ---- the restore ----------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_restore_writes_what_moved_reads_it_back_and_leaves_the_rest_alone()
    {
        var snapshot = Snap(Ethernet(), Wifi());
        var pc = Pc(Ethernet(itr: "0", lldp: false), Wifi());
        var result = Restore(snapshot, pc);
        Assert.True(result.Complete);
        Assert.Equal(2, result.Restored);
        Assert.Equal([$"adapter {EthGuid} *InterruptModeration=1", $"adapter {EthGuid} ms_lldp=True"], pc.Writes);
        var eth = pc.Adapters!.Single(a => a.InterfaceGuid == EthGuid);
        Assert.Equal("1", eth.Properties["*InterruptModeration"].RegistryValue);
        Assert.True(eth.Bindings["ms_lldp"]);
        Assert.Empty(NetworkRestorer.Plan(snapshot, pc.ReadAdapters()!).Adapters.SelectMany(a => a.Items));   // a second plan has nothing to do
    }

    [Fact]
    public void Nothing_is_written_when_nothing_differs()
    {
        var pc = Pc(Ethernet(), Wifi());
        var result = Restore(Snap(Ethernet(), Wifi()), pc);
        Assert.Empty(result.Items);
        Assert.Empty(pc.Writes);
    }

    [Fact]
    public void A_driver_that_does_not_keep_the_value_is_reported_as_failed()
    {
        var pc = Pc(Ethernet(itr: "0"));
        pc.DriverKeeps = false;
        var result = Restore(Snap(Ethernet()), pc);
        var item = Assert.Single(result.Items);
        Assert.Equal(RestoreOutcome.Failed, item.Outcome);
        Assert.Contains("after the write", item.Message);
        Assert.False(result.Complete);
    }

    [Fact]
    public void One_refused_write_does_not_stop_the_others()
    {
        var pc = Pc(Ethernet(itr: "0", lldp: false));
        pc.FailAdapterKey = "*InterruptModeration";
        var result = Restore(Snap(Ethernet()), pc);
        Assert.Equal(RestoreOutcome.Failed, result.Items.Single(i => i.Item.Key == "*InterruptModeration").Outcome);
        Assert.Equal(RestoreOutcome.Restored, result.Items.Single(i => i.Item.Key == "ms_lldp").Outcome);
        Assert.Equal(1, result.Restored);
    }

    [Fact]
    public void An_adapter_that_stays_down_stops_its_own_remaining_items()
    {
        var pc = Pc(Ethernet(itr: "0", buffers: "2048", lldp: false));
        pc.AdapterStaysDown = true;
        var result = Restore(Snap(Ethernet()), pc);
        Assert.Equal(3, result.Items.Count);
        Assert.Equal(RestoreOutcome.Failed, result.Items[0].Outcome);
        Assert.Contains("did not come up", result.Items[0].Message);
        Assert.All(result.Items.Skip(1), i => Assert.Equal(RestoreOutcome.NotAttempted, i.Outcome));
        Assert.Single(pc.Writes);
    }

    [Fact]
    public void Skipped_items_are_never_written()
    {
        var pc = Pc(Ethernet(buffers: "512"));
        var result = Restore(Snap(Ethernet(buffers: "4096")), pc);
        Assert.Empty(result.Items);
        Assert.Empty(pc.Writes);
    }
}
