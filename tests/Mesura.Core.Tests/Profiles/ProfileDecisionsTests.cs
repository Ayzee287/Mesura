using Mesura.Core.Hardware;
using Mesura.Core.Profiles;
using Mesura.Core.Tests.Windows;
using Mesura.Core.Windows;

namespace Mesura.Core.Tests.Profiles;

/// <summary>
/// The owner's final decisions on the composition of the 0.6.0 profiles (2026-11-04), pinned: original AMD Anti-Lag in Maximum performance only and only for a Radeon that offers it; hardware-accelerated
/// GPU scheduling offered unticked (no confirmed gain); the five per-maker network adapter presets in both profiles, each chosen by the adapter actually found. Nothing is applied to a real PC here: the
/// Windows and the adapters are stand-ins, the adapters built from real driver keyword lists.
/// </summary>
public sealed class ProfileDecisionsTests
{
    private const string RealtekGuid = "{A1000000-0000-0000-0000-000000000001}";
    private const string IntelWifiGuid = "{B1000000-0000-0000-0000-000000000001}";

    private static readonly WindowsCatalog Catalog = WindowsCatalog.Default;
    private static readonly OptimizationProfiles Profiles = OptimizationProfiles.Default;
    private static readonly string[] OffOn = ["Disabled", "Enabled"];

    private static readonly string[] NetworkPresets =
        ["network.preset-realtek-ethernet", "network.preset-intel-ethernet", "network.preset-killer-ethernet", "network.preset-intel-wifi", "network.preset-realtek-wifi"];

    private static OptimizationProfile Maximum => Profiles.ById("maximum-performance")!;

    private static OptimizationProfile Reference => Profiles.ById("recommended-gaming")!;

    // ---- stand-ins -------------------------------------------------------------------------------------------------------------------------------------------

    private static AdapterPropertyReading Enum(string keyword, string current, string[] registry, string[] display) =>
        new(keyword, keyword, current, display[Array.IndexOf(registry, current)], registry, display);

    private static Dictionary<string, AdapterPropertyReading> Props(params AdapterPropertyReading[] items) => items.ToDictionary(p => p.Keyword, StringComparer.OrdinalIgnoreCase);

    private static AdapterReading RealtekEthernet() => new(RealtekGuid, "Ethernet", "Realtek Gaming 2.5GbE Family Controller", "Realtek", 0x10EC, "1.0", "2025-01-01", AdapterKind.Ethernet, true, true,
        Props(Enum("EnableGreenEthernet", "1", ["0", "1"], OffOn), Enum("*EEE", "1", ["0", "1"], OffOn), Enum("*InterruptModeration", "1", ["0", "1"], OffOn),
            new("*ReceiveBuffers", "Receive Buffers", "256", "256", [], [], 32, 512, 1)),
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase), true);

    private static AdapterReading IntelWifi() => new(IntelWifiGuid, "Wi-Fi", "Intel(R) Wi-Fi 6 AX201 160MHz", "Intel", 0x8086, "24.70.0.3", "2026-07-29", AdapterKind.WiFi, true, true,
        Props(Enum("MIMOPowerSaveMode", "0", ["0", "1", "2", "3"], ["Auto SMPS", "Static SMPS", "Dynamic SMPS", "No SMPS"]),
            Enum("RoamAggressiveness", "2", ["0", "1", "2", "3", "4"], ["1. Lowest", "2. Medium-Low", "3. Medium", "4. Medium-High", "5. Highest"]),
            Enum("RoamingPreferredBandType", "0", ["0", "1", "2"], ["1. No Preference", "2. Prefer 2.4GHz band", "3. Prefer 5GHz band"])),
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase), null);

    private static AdapterReading IntelEthernetWithoutTheKeywords() => new("{A2000000-0000-0000-0000-000000000002}", "Ethernet 2", "Intel(R) Ethernet Controller I225-V", "Intel", 0x8086, "1.0", "2025-01-01",
        AdapterKind.Ethernet, true, true, Props(Enum("*SpeedDuplex", "0", ["0", "6"], ["Auto Negotiation", "1.0 Gbps Full Duplex"])),
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase), null);

    private static GpuFeatureReading Radeon(bool enabled, int? level = 0) => new(GpuAvailability.Available, "AMD Radeon RX 9070 XT", 0, true, enabled, level, null);

    private static HardwareProfile Hardware(GpuMaker gpu, params NetworkProfile[] network) => new(
        new WindowsPlatform(26100, "Professional", "24H2", true), DeviceForm.Desktop, new PowerPlatform(false, false, true),
        new CpuProfile("Test CPU", CpuMaker.Amd, 8, 16, false), 32UL << 30,
        [new GpuProfile("Test GPU", gpu, gpu == GpuMaker.Amd ? 0x1002 : 0x10DE, 1, "1.0", false)], [], network);

    private static ProfilePlan Plan(OptimizationProfile profile, FakeWindows pc, HardwareProfile hardware) =>
        ProfilePlanner.Plan(profile, Catalog, hardware, pc, id => Catalog.ById(id) is { } op ? WindowsEvaluator.Evaluate(op, pc) : null);

    private static FakeWindows Pc(GpuFeatureReading? gpu = null, params AdapterReading[] adapters) => new() { Gpu = gpu, Adapters = [.. adapters], PolicyEdition = true, PhysicalMemoryBytes = 32UL << 30 };

    // ---- the composition -------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_profile_ids_and_the_planner_are_unchanged_and_the_two_levels_are_composed_as_decided()
    {
        Assert.Equal(["recommended-gaming", "maximum-performance"], Profiles.Profiles.Select(p => p.Id));

        // AMD Anti-Lag: Maximum performance only, a default entry, for a Radeon.
        var antiLag = Maximum.Entries.Single(e => e.OperationId == "gpu.amd-anti-lag-on");
        Assert.Equal(EntryRole.Default, antiLag.Role);
        Assert.Equal("amd", antiLag.When!.Gpu);
        Assert.True(antiLag.SideEffects, "the driver turns Chill off while it is on: the entry shows its cost");
        Assert.DoesNotContain(Reference.Entries, e => e.OperationId == "gpu.amd-anti-lag-on");

        // Hardware-accelerated GPU scheduling: offered unticked, never applied with a profile, and not in Reference at all.
        var hags = Maximum.Entries.Single(e => e.OperationId == "gpu.hardware-scheduling-on");
        Assert.Equal(EntryRole.Optional, hags.Role);
        Assert.DoesNotContain(Reference.Entries, e => e.OperationId == "gpu.hardware-scheduling-on");
        Assert.DoesNotContain("gpu.hardware-scheduling-on", Maximum.DefaultIds);
        Assert.DoesNotContain("gpu.hardware-scheduling-on", Catalog.PresetById("maximum-performance")!.OperationIds);
        Assert.Contains("gpu.hardware-scheduling-on", Catalog.PresetById("maximum-performance")!.OptionalIds);

        // The five network presets: default entries in both profiles, each for the kind of adapter it is for.
        foreach (var profile in new[] { Reference, Maximum })
        {
            foreach (var id in NetworkPresets)
            {
                var entry = profile.Entries.Single(e => e.OperationId == id);
                Assert.Equal(EntryRole.Default, entry.Role);
                Assert.Equal("network", entry.GroupId);
                Assert.True(entry.SideEffects, $"{id}: the adapter restarts and power saving is given up: the entry shows its cost");
                Assert.Equal(id.EndsWith("wifi", StringComparison.Ordinal) ? "wifi" : "ethernet", entry.When!.Network);
            }
        }
    }

    [Fact]
    public void The_catalog_presets_of_both_levels_list_what_the_profiles_apply_and_offer()
    {
        foreach (var profile in new[] { Reference, Maximum })
        {
            var preset = Catalog.PresetById(profile.Id)!;
            Assert.Equal(profile.DefaultIds.Order(), preset.OperationIds.Order());
            Assert.Equal(profile.OptionalIds.Order(), preset.OptionalIds.Order());
        }
    }

    [Fact]
    public void A_default_entry_is_never_community_only_and_nothing_here_claims_a_speed_gain()
    {
        foreach (var id in NetworkPresets.Append("gpu.amd-anti-lag-on"))
        {
            var op = Catalog.ById(id)!;
            Assert.True(op.Basis is ValueBasis.Documented or ValueBasis.Verified or ValueBasis.MakerDocumented, $"{id} is {op.Basis}");
            Assert.Equal(OperationBenefit.Unverified, op.Evidence);   // no measurement exists, so none is claimed
            Assert.DoesNotContain("faster", op.Description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("boost your", op.Description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("No gain is claimed", Maximum.Entries.Single(e => e.OperationId == id).Why, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_saved_setup_profile_that_names_the_two_levels_or_the_new_operations_still_loads_and_resolves()
    {
        // The Profiles page keeps files that name presets and operations by id; the ids of the levels did not change and the new operations are ordinary ids.
        const string json = """
            { "kind": "Mesura.SetupProfile", "schemaVersion": 1, "name": "saved with 0.5.5", "description": "",
              "windowsPresets": [ "recommended-gaming", "maximum-performance" ],
              "windows": [ "gpu.hardware-scheduling-on", "gpu.amd-anti-lag-on", "network.preset-intel-wifi" ] }
            """;

        var profile = Mesura.Core.Setup.SetupProfile.Parse(json, Mesura.Core.Apps.AppCatalog.Default, Catalog);
        var resolved = profile.ResolveWindows(Catalog).Select(o => o.Id).ToList();

        Assert.Equal(["recommended-gaming", "maximum-performance"], profile.WindowsPresets);
        Assert.Contains("gpu.amd-anti-lag-on", resolved);
        Assert.Contains("network.preset-realtek-ethernet", resolved);          // from the Reference preset
        Assert.Contains("gpu.hardware-scheduling-on", resolved);               // named on its own: applied, because the file asks for it by id
        Assert.Equal(resolved.Count, resolved.Distinct().Count());
    }

    // ---- the network presets are chosen by the adapter found -------------------------------------------------------------------------------------------------

    [Fact]
    public void A_pc_with_a_realtek_ethernet_card_gets_only_the_realtek_preset_in_both_profiles_and_the_others_say_why_not()
    {
        var pc = Pc(null, RealtekEthernet());
        var hardware = Hardware(GpuMaker.Nvidia, new NetworkProfile("Ethernet", AdapterKind.Ethernet, "Realtek", true));

        foreach (var profile in new[] { Reference, Maximum })
        {
            var plan = Plan(profile, pc, hardware);

            Assert.Equal(EntryState.ToApply, plan.Entry("network.preset-realtek-ethernet")!.State);
            Assert.True(plan.Entry("network.preset-realtek-ethernet")!.Included);
            Assert.Equal(["network.preset-realtek-ethernet"], plan.OperationIds.Where(id => id.StartsWith("network.preset-", StringComparison.Ordinal)));

            var intel = plan.Entry("network.preset-intel-ethernet")!;
            Assert.Equal(EntryState.Unsupported, intel.State);
            Assert.Equal(PlanReason.Absent, intel.Reason);
            Assert.Contains("no physical ethernet network adapter of Intel", intel.Detail, StringComparison.Ordinal);
            Assert.Equal(EntryState.Unsupported, plan.Entry("network.preset-killer-ethernet")!.State);

            // The Wi-Fi presets are not for a PC with no Wi-Fi adapter.
            Assert.Equal("hardware.network.wifi", plan.Entry("network.preset-intel-wifi")!.Reason);
            Assert.Equal("hardware.network.wifi", plan.Entry("network.preset-realtek-wifi")!.Reason);
        }
    }

    [Fact]
    public void A_laptop_with_intel_wifi_gets_the_intel_wifi_preset_and_no_ethernet_or_realtek_one()
    {
        var pc = Pc(null, IntelWifi());
        var hardware = Hardware(GpuMaker.Nvidia, new NetworkProfile("Wi-Fi", AdapterKind.WiFi, "Intel", true));
        var plan = Plan(Maximum, pc, hardware);

        Assert.Equal(["network.preset-intel-wifi"], plan.OperationIds.Where(id => id.StartsWith("network.preset-", StringComparison.Ordinal)));
        Assert.Equal(PlanReason.Absent, plan.Entry("network.preset-realtek-wifi")!.Reason);
        Assert.Equal("hardware.network.ethernet", plan.Entry("network.preset-realtek-ethernet")!.Reason);
        Assert.Equal("hardware.network.ethernet", plan.Entry("network.preset-intel-ethernet")!.Reason);
    }

    [Fact]
    public void A_card_of_the_right_maker_whose_driver_declares_none_of_the_settings_is_left_alone_with_the_reason()
    {
        var pc = Pc(null, IntelEthernetWithoutTheKeywords());
        var plan = Plan(Reference, pc, Hardware(GpuMaker.Nvidia, new NetworkProfile("Ethernet 2", AdapterKind.Ethernet, "Intel", true)));

        var entry = plan.Entry("network.preset-intel-ethernet")!;
        Assert.Equal(EntryState.Unsupported, entry.State);
        Assert.Contains("offers none of these settings", entry.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(entry.OperationId, plan.OperationIds);
    }

    [Fact]
    public void An_unread_adapter_list_leaves_the_presets_alone_instead_of_writing_blind()
    {
        var pc = Pc(null);
        pc.Adapters = null;   // the adapters could not be read
        var plan = Plan(Maximum, pc, Hardware(GpuMaker.Nvidia, new NetworkProfile("Ethernet", AdapterKind.Ethernet, "Realtek", true)));

        Assert.Equal(EntryState.CantTell, plan.Entry("network.preset-realtek-ethernet")!.State);
        Assert.DoesNotContain(plan.OperationIds, id => id.StartsWith("network.preset-", StringComparison.Ordinal));
    }

    // ---- Anti-Lag is chosen by the GPU found ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void On_a_radeon_that_offers_it_Anti_Lag_is_planned_in_Maximum_and_not_in_Reference()
    {
        var pc = Pc(Radeon(false));
        var hardware = Hardware(GpuMaker.Amd);

        Assert.Contains("gpu.amd-anti-lag-on", Plan(Maximum, pc, hardware).OperationIds);
        Assert.DoesNotContain("gpu.amd-anti-lag-on", Plan(Reference, pc, hardware).OperationIds);
        Assert.Null(Plan(Reference, pc, hardware).Entry("gpu.amd-anti-lag-on"));
    }

    [Fact]
    public void Anti_Lag_is_left_alone_when_it_is_already_on_when_Next_is_selected_when_the_card_is_not_a_radeon_and_when_the_driver_cannot_be_asked()
    {
        var radeon = Hardware(GpuMaker.Amd);

        Assert.Equal(EntryState.Applied, Plan(Maximum, Pc(Radeon(true)), radeon).Entry("gpu.amd-anti-lag-on")!.State);

        var next = Plan(Maximum, Pc(Radeon(false, level: 1)), radeon).Entry("gpu.amd-anti-lag-on")!;
        Assert.Equal(EntryState.CantTell, next.State);
        Assert.Equal(PlanReason.Blocked, next.Reason);
        Assert.Contains("Anti-Lag Next", next.Detail, StringComparison.Ordinal);

        var nvidia = Plan(Maximum, Pc(GpuFeatureReading.None(GpuAvailability.NoAdapter)), Hardware(GpuMaker.Nvidia)).Entry("gpu.amd-anti-lag-on")!;
        Assert.Equal(EntryState.Unsupported, nvidia.State);
        Assert.Equal("hardware.gpu.amd", nvidia.Reason);

        Assert.Equal(EntryState.CantTell, Plan(Maximum, Pc(GpuFeatureReading.None(GpuAvailability.Unreadable)), radeon).Entry("gpu.amd-anti-lag-on")!.State);
        Assert.Equal(EntryState.Unsupported, Plan(Maximum, Pc(Radeon(false) with { Supported = false }), radeon).Entry("gpu.amd-anti-lag-on")!.State);
    }

    // ---- GPU scheduling is offered, not applied ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Hardware_accelerated_gpu_scheduling_is_offered_unticked_and_applied_only_when_the_user_ticks_it()
    {
        var pc = Pc(null);
        pc.Registry[@"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\HwSchMode"] = (RegistryKind.DWord, "1");
        var hardware = Hardware(GpuMaker.Nvidia);

        var untouched = Plan(Maximum, pc, hardware);
        Assert.DoesNotContain("gpu.hardware-scheduling-on", untouched.OperationIds);

        var ticked = ProfilePlanner.Plan(Maximum, Catalog, hardware, pc, id => Catalog.ById(id) is { } op ? WindowsEvaluator.Evaluate(op, pc) : null, null,
            new ProfileChoices(new HashSet<string>(), new Dictionary<string, bool> { ["gpu.hardware-scheduling-on"] = true }));
        var entry = ticked.Entry("gpu.hardware-scheduling-on")!;
        Assert.True(entry.Included == (entry.State == EntryState.ToApply), "ticking applies it exactly where it can apply");
    }

    // ---- the two together go through the real engine and undo ---------------------------------------------------------------------------------------------------

    [Fact]
    public void The_network_preset_and_Anti_Lag_of_a_plan_apply_through_the_engine_read_back_and_undo_to_the_exact_start()
    {
        var pc = Pc(Radeon(false), RealtekEthernet());
        var hardware = Hardware(GpuMaker.Amd, new NetworkProfile("Ethernet", AdapterKind.Ethernet, "Realtek", true));
        var ops = Plan(Maximum, pc, hardware).OperationIds.Where(id => id.StartsWith("network.preset-", StringComparison.Ordinal) || id == "gpu.amd-anti-lag-on").Select(id => Catalog.ById(id)!).ToList();
        Assert.Equal(2, ops.Count);
        var before = pc.Adapters![0];

        var configurator = new WindowsConfigurator(pc, pc);
        var applied = configurator.Apply(ops, TestContext.Current.CancellationToken);

        Assert.All(applied, o => Assert.Equal(WindowsOpStatus.Applied, o.Status));
        Assert.True(pc.Gpu!.Enabled);
        Assert.Equal("0", pc.Adapters![0].Properties["*InterruptModeration"].RegistryValue);
        Assert.All(pc.Writes.Where(w => w.StartsWith("adapter", StringComparison.Ordinal)), w => Assert.Contains(RealtekGuid, w, StringComparison.Ordinal));

        var reverted = configurator.Revert(applied);

        Assert.All(reverted, o => Assert.Equal(WindowsOpStatus.Reverted, o.Status));
        Assert.False(pc.Gpu!.Enabled);
        Assert.Equal(before.Properties.ToDictionary(p => p.Key, p => p.Value.RegistryValue), pc.Adapters![0].Properties.ToDictionary(p => p.Key, p => p.Value.RegistryValue));
        Assert.Equal(before.AllowTurnOff, pc.Adapters![0].AllowTurnOff);
    }
}
