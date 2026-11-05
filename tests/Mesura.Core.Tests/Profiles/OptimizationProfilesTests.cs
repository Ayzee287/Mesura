using Mesura.Core.Hardware;
using Mesura.Core.Profiles;
using Mesura.Core.Windows;

namespace Mesura.Core.Tests.Profiles;

/// <summary>The built-in profile definitions (profiles.json): their consistency, their agreement with the catalog, and the rules of docs/profiles-0.6.md.</summary>
public sealed class OptimizationProfilesTests
{
    private static readonly OptimizationProfiles Profiles = OptimizationProfiles.Default;
    private static readonly WindowsCatalog Catalog = WindowsCatalog.Default;

    private static OptimizationProfile Balanced => Profiles.ById("recommended-gaming")!;

    private static OptimizationProfile Maximum => Profiles.ById("maximum-performance")!;

    private static string Json(string entries, string why = "\"x.a\": \"Because.\"", string groups = "{ \"id\": \"g\", \"name\": \"G\" }") =>
        "{ \"kind\": \"Mesura.OptimizationProfiles\", \"schemaVersion\": 1, \"groups\": [" + groups + "], "
        + "\"profiles\": [ { \"id\": \"p\", \"entries\": [" + entries + "] } ], \"why\": { " + why + " } }";

    [Fact]
    public void The_two_levels_exist_under_the_ids_saved_profiles_and_the_command_line_use()
    {
        Assert.NotNull(Balanced);
        Assert.NotNull(Maximum);
        Assert.Equal(["recommended-gaming", "maximum-performance"], Profiles.Profiles.Select(p => p.Id));
    }

    [Fact]
    public void Every_entry_names_an_operation_the_catalog_has_that_mesura_can_apply()
    {
        foreach (var entry in Profiles.Profiles.SelectMany(p => p.Entries))
        {
            var op = Catalog.ById(entry.OperationId);
            Assert.True(op is not null, $"{entry.OperationId} is not in the catalog");
            Assert.True(op.Applicable, $"{entry.OperationId} is a guide or an observation");
            Assert.False(string.IsNullOrWhiteSpace(entry.Why), entry.OperationId);
            Assert.NotNull(Profiles.Group(entry.GroupId));
        }
    }

    [Fact]
    public void The_catalog_presets_of_the_same_id_list_exactly_the_default_and_optional_entries_in_order()
    {
        foreach (var profile in Profiles.Profiles)
        {
            var preset = Catalog.PresetById(profile.Id)!;
            Assert.Equal(profile.DefaultIds, preset.OperationIds);
            Assert.Equal(profile.OptionalIds.Order(StringComparer.Ordinal), preset.OptionalIds.Order(StringComparer.Ordinal));
            Assert.Equal(PresetTier.Primary, preset.Tier);
        }
    }

    [Fact]
    public void Telemetry_is_off_by_default_in_every_profile_and_is_its_own_group_apart_from_privacy_and_promotions()
    {
        foreach (var profile in Profiles.Profiles)
        {
            var telemetry = profile.InGroup("telemetry");
            Assert.Contains(telemetry, e => e.OperationId == "privacy.diagnostic-data-required" && e.Role == EntryRole.Default);
            Assert.Contains(telemetry, e => e.OperationId == "telemetry.ceip-policy-off" && e.Role == EntryRole.Default);
            Assert.Contains(telemetry, e => e.OperationId == "telemetry.application-telemetry-off" && e.Role == EntryRole.Default);

            // Nothing that is advertising, a tip or a promotion sits in the telemetry group, and no telemetry control sits in privacy.
            Assert.All(telemetry, e => Assert.Contains(Catalog.ById(e.OperationId)!.Impact, new[] { OperationImpact.Privacy }));
            Assert.All(profile.InGroup("privacy"), e => Assert.DoesNotContain("telemetry", e.OperationId, StringComparison.Ordinal));
            Assert.NotEmpty(profile.InGroup("privacy"));
        }
    }

    [Fact]
    public void The_owners_final_decisions_of_2026_11_02_are_in_the_definition()
    {
        foreach (var profile in Profiles.Profiles)
        {
            // The inventory collector is off by default in both profiles, whatever it may mean for Windows Update's compatibility checks (the owner's decision).
            var inventory = profile.Entries.Single(e => e.OperationId == "telemetry.inventory-collector-off");
            Assert.Equal((EntryRole.Default, "telemetry"), (inventory.Role, inventory.GroupId));
            Assert.True(inventory.SideEffects);

            // Privacy and promotions is its own group, on by default, apart from telemetry.
            Assert.All(profile.InGroup("privacy"), e => Assert.Equal(EntryRole.Default, e.Role));
            Assert.Empty(profile.InGroup("privacy").Select(e => e.OperationId).Intersect(profile.InGroup("telemetry").Select(e => e.OperationId)));
        }

        // Power throttling is off in Maximum performance on a laptop too, with its cost flagged.
        var throttling = Maximum.Entries.Single(e => e.OperationId == "power.power-throttling-off");
        Assert.Equal(EntryRole.Default, throttling.Role);
        Assert.True(throttling.SideEffects);
        Assert.DoesNotContain(DeviceRequirement.NoBattery, Catalog.ById("power.power-throttling-off")!.Requirements);

        // The names of the profiles and of the plans are unchanged.
        Assert.Equal("Mesura Reference", Catalog.PresetById("recommended-gaming")!.Name);
        Assert.Equal("Maximum performance", Catalog.PresetById("maximum-performance")!.Name);
        Assert.Equal("Balanced Mesura", Catalog.ById("power.mesura-balanced-plan")!.PowerPlan!.Name);
        Assert.Equal("Maximum Performance Mesura", Catalog.ById("power.mesura-maximum-plan")!.PowerPlan!.Name);
    }

    [Fact]
    public void Maximum_performance_holds_everything_balanced_does_except_its_own_plan_and_adds_to_it()
    {
        var balanced = Balanced.Entries.Where(e => e.OperationId != "power.mesura-balanced-plan").ToDictionary(e => e.OperationId);
        var maximum = Maximum.Entries.ToDictionary(e => e.OperationId);

        foreach (var (id, entry) in balanced)
        {
            Assert.True(maximum.ContainsKey(id), $"{id} is in Balanced but not in Maximum");
            Assert.Equal(entry.Role, maximum[id].Role);
            Assert.Equal(entry.GroupId, maximum[id].GroupId);
        }

        Assert.DoesNotContain("power.mesura-balanced-plan", maximum.Keys);
        Assert.Contains("power.mesura-maximum-plan", maximum.Keys);
        Assert.True(Maximum.DefaultIds.Count > Balanced.DefaultIds.Count + 20);
    }

    [Fact]
    public void Maximum_performance_is_not_limited_to_the_moderate_entries_and_holds_the_aggressive_ones_ticked_with_their_cost_flagged()
    {
        var byId = Maximum.Entries.ToDictionary(e => e.OperationId);
        foreach (var id in new[]
        {
            "cpu.core-parking-off", "power.cpu-epp-performance-ac", "power.cpu-ramp-up-fast-ac", "cpu.hybrid-prefer-performance-cores", "power.power-throttling-off",
            "memory.compression-off", "memory.keep-kernel-in-ram", "system.ntfs-8dot3-off", "system.ntfs-last-access-off", "power.nvme-stay-active-ac",
            "power.sata-link-power-off-ac", "power.pcie-aspm-off", "power.usb-keep-awake", "power.disk-never-off", "gpu.amd-anti-lag-on", "input.pointer-precision-off",
        })
        {
            Assert.Equal(EntryRole.Default, byId[id].Role);
            Assert.True(byId[id].SideEffects, $"{id} has a cost that must be shown");
        }

        // The entries held back are held back for the evidence of their stored value, never for being aggressive.
        foreach (var optional in Maximum.Entries.Where(e => e.Role == EntryRole.Optional && !e.OperationId.StartsWith("telemetry.", StringComparison.Ordinal)))
        {
            Assert.Equal(ValueBasis.Community, Catalog.ById(optional.OperationId)!.Basis);
        }
    }

    [Fact]
    public void A_default_entry_never_rests_on_a_value_known_from_community_sources_unless_windows_confirms_it_in_effect()
    {
        foreach (var entry in Profiles.Profiles.SelectMany(p => p.Entries).Where(e => e.Role == EntryRole.Default))
        {
            var op = Catalog.ById(entry.OperationId)!;
            Assert.True(op.Basis != ValueBasis.Community || op.Effect is not null, $"{entry.OperationId}: a community value needs an effect check or must be optional");
        }
    }

    [Fact]
    public void The_default_entries_of_each_profile_never_set_one_thing_two_ways_even_with_every_group_on()
    {
        foreach (var profile in Profiles.Profiles)
        {
            var ops = profile.DefaultIds.Select(id => Catalog.ById(id)!).ToList();
            Assert.Empty(WindowsConfigurator.Conflicts(ops));
        }
    }

    [Fact]
    public void Entries_for_particular_hardware_say_what_they_are_for_and_the_profile_is_still_valid_without_that_hardware()
    {
        var nvme = Maximum.Entries.Single(e => e.OperationId == "power.nvme-stay-active-ac");
        Assert.Equal("nvme", nvme.When!.Disk);
        Assert.Equal("sata", Maximum.Entries.Single(e => e.OperationId == "power.sata-link-power-off-ac").When!.Disk);
        Assert.Equal("any", Maximum.Entries.Single(e => e.OperationId == "gpu.hardware-scheduling-on").When!.Gpu);
        Assert.Equal("amd", Maximum.Entries.Single(e => e.OperationId == "gpu.amd-anti-lag-on").When!.Gpu);
    }

    [Fact]
    public void Every_group_has_a_name_and_every_used_group_is_defined_once()
    {
        Assert.Equal(Profiles.Groups.Count, Profiles.Groups.Select(g => g.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(Profiles.Groups, g => Assert.False(string.IsNullOrWhiteSpace(g.Name)));
        foreach (var group in Profiles.Profiles.SelectMany(p => p.Entries.Select(e => e.GroupId)).Distinct())
        {
            Assert.NotNull(Profiles.Group(group));
        }
    }

    [Theory]
    [InlineData("{ \"op\": \"x.a\", \"group\": \"nope\" }", "unknown group")]
    [InlineData("{ \"op\": \"x.a\", \"group\": \"g\", \"role\": \"always\" }", "unknown role")]
    [InlineData("{ \"op\": \"x.b\", \"group\": \"g\" }", "no reason")]
    [InlineData("{ \"op\": \"x.a\", \"group\": \"g\" }, { \"op\": \"x.a\", \"group\": \"g\" }", "more than once")]
    [InlineData("{ \"op\": \"x.a\", \"group\": \"g\", \"when\": { \"disk\": \"floppy\" } }", "unknown disk")]
    [InlineData("{ \"op\": \"x.a\", \"group\": \"g\", \"when\": { \"colour\": \"red\" } }", "unknown condition")]
    [InlineData("{ \"op\": \"x.a\", \"group\": \"g\", \"when\": { \"form\": [\"toaster\"] } }", "unknown form")]
    [InlineData("{ \"group\": \"g\" }", "no 'op'")]
    public void An_inconsistent_definition_is_refused_with_its_reason(string entry, string expected)
    {
        var error = Assert.Throws<FormatException>(() => OptimizationProfiles.Parse(Json(entry)));
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_of_another_kind_or_a_newer_schema_is_refused_and_a_consistent_one_is_read()
    {
        Assert.Throws<FormatException>(() => OptimizationProfiles.Parse("{ \"kind\": \"Other\", \"schemaVersion\": 1 }"));
        Assert.Throws<FormatException>(() => OptimizationProfiles.Parse(Json("").Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99", StringComparison.Ordinal)));
        Assert.Throws<FormatException>(() => OptimizationProfiles.Parse("not json"));

        var read = OptimizationProfiles.Parse(Json("{ \"op\": \"x.a\", \"group\": \"g\", \"when\": { \"form\": [\"desktop\"], \"gpu\": \"nvidia\" }, \"sideEffects\": true }"));
        var entry = Assert.Single(read.ById("p")!.Entries);
        Assert.Equal("Because.", entry.Why);
        Assert.True(entry.SideEffects);
        Assert.Equal(["desktop"], entry.When!.Form);
    }

    [Fact]
    public void A_hardware_condition_answers_yes_no_or_unknown_and_never_guesses_from_an_unread_fact()
    {
        var desktop = new HardwareProfile(new WindowsPlatform(26200, "Professional", "25H2", true), DeviceForm.Desktop, new PowerPlatform(false, false, true),
            new CpuProfile("Test", CpuMaker.Amd, 8, 16, false), 32UL << 30,
            [new GpuProfile("NVIDIA Test", GpuMaker.Nvidia, 0x10DE, 1, "1", false)],
            [new DiskProfile("Test NVMe", StorageClass.Nvme, 1, true, Mesura.Core.Raw.BusKind.Nvme)],
            [new NetworkProfile("Test Ethernet", AdapterKind.Ethernet, "Realtek", true)]);

        Assert.True(new HardwareCondition(Disk: "nvme").Holds(desktop, out _));
        Assert.False(new HardwareCondition(Disk: "sata").Holds(desktop, out var failed));
        Assert.Equal("disk.sata", failed);
        Assert.True(new HardwareCondition(Gpu: "nvidia", Cpu: "amd", Network: "ethernet", Form: ["desktop", "compact"]).Holds(desktop, out _));
        Assert.False(new HardwareCondition(Cpu: "hybrid").Holds(desktop, out _));
        Assert.False(new HardwareCondition(Network: "wifi").Holds(desktop, out _));
        Assert.False(new HardwareCondition(Gpu: "amd").Holds(desktop, out _));
        Assert.True(new HardwareCondition(Gpu: "discrete").Holds(desktop, out _));

        // Facts that weren't read answer "unknown": no drive read is not "no NVMe drive", an unread chassis is not a desktop.
        var unread = new HardwareProfile(new WindowsPlatform(26200, null, null, null), DeviceForm.Unknown, new PowerPlatform(null, null, null), null, null, [], [], []);
        Assert.Null(new HardwareCondition(Disk: "nvme").Holds(unread, out var why));
        Assert.Equal("disk.nvme", why);
        Assert.Null(new HardwareCondition(Form: ["desktop"]).Holds(unread, out _));
        Assert.Null(new HardwareCondition(Gpu: "nvidia").Holds(unread, out _));
        Assert.Null(new HardwareCondition(Cpu: "hybrid").Holds(unread, out _));
        Assert.Null(new HardwareCondition(Network: "wifi").Holds(unread, out _));
        Assert.True(new HardwareCondition().Holds(unread, out _));
    }
}
