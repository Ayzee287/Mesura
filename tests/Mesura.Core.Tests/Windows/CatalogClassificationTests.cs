using Mesura.Core.Windows;

namespace Mesura.Core.Tests.Windows;

/// <summary>
/// The redesign's catalog data (2026-10): every entry has an impact and a summary for the lists, the two outcomes
/// Optimize offers are the only primary presets, Maximum performance is Recommended plus documented desktop power
/// settings, and its extras are opt-in, never part of the preset.
/// </summary>
public sealed class CatalogClassificationTests
{
    private static readonly WindowsCatalog Catalog = WindowsCatalog.Default;

    [Fact]
    public void Every_built_in_entry_has_a_one_line_summary()
    {
        Assert.All(Catalog.Operations, op =>
        {
            Assert.False(string.IsNullOrWhiteSpace(op.Summary), $"{op.Id} has no summary");
            Assert.True(op.Summary.Length <= 90, $"{op.Id}'s summary is longer than one line");
        });
    }

    [Fact]
    public void Every_group_of_the_audit_has_entries_and_no_other_group_exists()
    {
        // The eight groups of docs/catalog-audit-0.4.md. A group nobody uses would be an empty rail entry; a ninth would be a decision.
        var used = Catalog.Operations.Select(o => o.Impact).Distinct().OrderBy(i => i).ToList();
        Assert.Equal(Enum.GetValues<OperationImpact>().OrderBy(i => i), used);
        Assert.Equal(8, used.Count);
    }

    [Fact]
    public void Optimize_offers_exactly_recommended_and_maximum_performance()
    {
        Assert.Equal(["recommended-gaming", "maximum-performance"], Catalog.Presets.Where(p => p.Tier == PresetTier.Primary).Select(p => p.Id));
        Assert.Null(Catalog.PresetById("ghost-spectre-superlite"));
        Assert.All(new[] { "gaming", "less-data-sharing", "network-defaults", "desktop-performance", "reference-performance", "reference-privacy", "reference-convenience" }, id => Assert.Equal(PresetTier.Collection, Catalog.PresetById(id)!.Tier));
    }

    [Fact]
    public void Maximum_performance_is_recommended_plus_every_applicable_performance_setting_documented_or_confirmed_in_effect()
    {
        var recommended = Catalog.PresetById("recommended-gaming")!;
        var maximum = Catalog.PresetById("maximum-performance")!;

        Assert.All(recommended.OperationIds.Where(id => id != "power.mesura-balanced-plan"), id => Assert.Contains(id, maximum.OperationIds)); // the plan is replaced by the Maximum one
        var added = Catalog.Resolve(maximum).Where(o => !recommended.OperationIds.Contains(o.Id)).ToList();
        Assert.NotEmpty(added);
        Assert.All(added, o =>
        {
            // What Maximum adds is documented by Microsoft (not merely verified) or confirmed in effect by Windows after it is written, a power,
            // performance, gaming, memory or storage setting or a background reduction, never a service, task, feature removal or an undocumented knob.
            Assert.True(o.Basis == ValueBasis.Documented || o.Effect is not null, o.Id);
            Assert.True(o.Impact is OperationImpact.Power or OperationImpact.Privacy or OperationImpact.Performance or OperationImpact.Gaming or OperationImpact.Convenience
                or OperationImpact.Maintenance or OperationImpact.Experimental, o.Id);
            Assert.True(o.Risk is RiskClass.SystemConfiguration or RiskClass.UserPreference, o.Id);
            Assert.True(o.Services.Count == 0 && o.Tasks.Count == 0 && o.Features.Count == 0, o.Id);
        });

        // Its power side is its own plan (0.5), which carries the USB, PCI Express and drive settings itself; Windows' High performance is not used.
        Assert.Contains("power.mesura-maximum-plan", maximum.OperationIds);
        Assert.DoesNotContain("power.high-performance", maximum.OperationIds);
        Assert.Contains("power.pcie-aspm-off", maximum.OperationIds); // the plan writes the same value; the profile planner shows it as covered rather than applying it twice
        Assert.Empty(WindowsConfigurator.Conflicts(Catalog.Resolve(maximum)));
    }

    [Fact]
    public void Maximum_performance_extras_are_opt_in_documented_and_not_in_the_preset()
    {
        var maximum = Catalog.PresetById("maximum-performance")!;

        Assert.NotEmpty(maximum.OptionalIds);
        Assert.All(maximum.OptionalIds, id =>
        {
            var op = Catalog.ById(id);
            Assert.NotNull(op);
            Assert.True(op.Applicable);
            Assert.NotEqual(RiskClass.Undocumented, op.Risk);
            Assert.DoesNotContain(id, maximum.OperationIds);
        });
        Assert.DoesNotContain(Catalog.Resolve(maximum), o => maximum.OptionalIds.Contains(o.Id));
    }

    [Fact]
    public void Maximum_performance_entries_apply_on_laptops_and_the_one_that_costs_battery_says_so()
    {
        // 2026-11-02 (0.6, owner's decisions): Maximum performance is not limited on a laptop. Windows' own High performance plan switch changes the battery side
        // too, so it stays desktop-only; an entry that writes only the AC value of the active plan leaves the battery alone and applies; and the power-throttling
        // switch, which acts on battery as well, is applied on a laptop on purpose, with its cost stated.
        var plan = Catalog.ById("power.high-performance")!;
        Assert.Contains(DeviceRequirement.NoBattery, plan.Requirements);
        Assert.Contains(DeviceRequirement.NoModernStandby, plan.Requirements);
        var throttling = Catalog.ById("power.power-throttling-off")!;
        Assert.DoesNotContain(DeviceRequirement.NoBattery, throttling.Requirements);
        Assert.Contains(DeviceRequirement.ProEdition, throttling.Requirements);
        Assert.Equal(OperationLevel.Moderate, throttling.Level);
        Assert.Contains("battery", throttling.Limitations, StringComparison.OrdinalIgnoreCase);

        foreach (var id in new[] { "power.pcie-aspm-off", "power.nvme-stay-active-ac", "power.cpu-epp-performance-ac", "power.cpu-ramp-up-fast-ac", "cpu.core-parking-off" })
        {
            var op = Catalog.ById(id)!;
            Assert.DoesNotContain(DeviceRequirement.NoBattery, op.Requirements);
            Assert.NotEmpty(op.PowerSettings); // a PowerSetting has an AC value only: there is no battery side to write
            Assert.True(op.Registry.Count == 0 && op.PowerPlan is null, id);
        }
    }

    private static string CatalogWith(string operation, string preset) => $$"""
        { "kind": "Mesura.WindowsCatalog", "schemaVersion": 1,
          "operations": [ {{operation}} ],
          "presets": [ {{preset}} ] }
        """;

    private const string Op = """
        { "id": "a.b", "name": "A", "description": "D", "risk": "userPreference",
          "registry": [ { "hive": "HKCU", "path": "Software\\X", "name": "V", "type": "dword", "value": "1" } ] }
        """;

    private const string Undocumented = """
        { "id": "u.v", "name": "U", "description": "D", "risk": "undocumented",
          "registry": [ { "hive": "HKCU", "path": "Software\\X", "name": "W", "type": "dword", "value": "1" } ] }
        """;

    [Fact]
    public void Missing_fields_default_to_maintenance_impact_an_empty_summary_and_a_collection()
    {
        var catalog = WindowsCatalog.Parse(CatalogWith(Op, """{ "id": "p", "name": "P", "operations": [ "a.b" ] }"""));

        Assert.Equal(OperationImpact.Maintenance, catalog.ById("a.b")!.Impact);
        Assert.Equal("", catalog.ById("a.b")!.Summary);
        Assert.Equal(PresetTier.Collection, catalog.PresetById("p")!.Tier);
        Assert.Empty(catalog.PresetById("p")!.OptionalIds);
    }

    [Theory]
    [InlineData("""{ "id": "p", "name": "P", "tier": "hero", "operations": [ "a.b" ] }""", "unknown tier 'hero'")]
    [InlineData("""{ "id": "p", "name": "P", "operations": [ "a.b" ], "optional": [ "a.b" ] }""", "both as an operation and as optional")]
    [InlineData("""{ "id": "p", "name": "P", "operations": [ "a.b" ], "optional": [ "missing" ] }""", "not in the catalog")]
    public void Inconsistent_presets_are_refused(string preset, string expected)
    {
        var e = Assert.Throws<FormatException>(() => WindowsCatalog.Parse(CatalogWith(Op, preset)));
        Assert.Contains(expected, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_undocumented_entry_is_refused_as_an_optional_extra_too()
    {
        var e = Assert.Throws<FormatException>(() => WindowsCatalog.Parse(CatalogWith(Op + "," + Undocumented,
            """{ "id": "p", "name": "P", "operations": [ "a.b" ], "optional": [ "u.v" ] }""")));
        Assert.Contains("undocumented", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_impact_is_refused()
    {
        var e = Assert.Throws<FormatException>(() => WindowsCatalog.Parse(CatalogWith(Op.Replace("\"risk\"", "\"impact\": \"speed\", \"risk\"", StringComparison.Ordinal),
            """{ "id": "p", "name": "P", "operations": [ "a.b" ] }""")));
        Assert.Contains("unknown impact 'speed'", e.Message, StringComparison.Ordinal);
    }
}
