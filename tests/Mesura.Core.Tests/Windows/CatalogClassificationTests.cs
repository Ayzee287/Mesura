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
    public void Power_entries_are_power_and_privacy_entries_are_privacy()
    {
        Assert.All(Catalog.Operations.Where(o => o.Category == "Power"), o => Assert.Equal(OperationImpact.Power, o.Impact));
        Assert.All(Catalog.Operations.Where(o => o.Category == "Privacy"), o => Assert.Equal(OperationImpact.Privacy, o.Impact));
    }

    [Fact]
    public void Optimize_offers_exactly_recommended_and_maximum_performance()
    {
        Assert.Equal(["recommended-gaming", "maximum-performance"], Catalog.Presets.Where(p => p.Tier == PresetTier.Primary).Select(p => p.Id));
        Assert.Equal(PresetTier.Specialized, Catalog.PresetById("ghost-spectre-superlite")!.Tier);
        Assert.All(new[] { "gaming", "quiet", "clean-taskbar" }, id => Assert.Equal(PresetTier.Collection, Catalog.PresetById(id)!.Tier));
    }

    [Fact]
    public void Maximum_performance_is_recommended_plus_documented_background_reduction_and_desktop_power_settings()
    {
        var recommended = Catalog.PresetById("recommended-gaming")!;
        var maximum = Catalog.PresetById("maximum-performance")!;

        Assert.All(recommended.OperationIds, id => Assert.Contains(id, maximum.OperationIds));
        var added = Catalog.Resolve(maximum).Where(o => !recommended.OperationIds.Contains(o.Id)).ToList();
        Assert.NotEmpty(added);
        Assert.All(added, o =>
        {
            // What Maximum adds is documented by Microsoft (not merely verified), a power setting or a privacy/background
            // reduction (0.2.0 adds documented Edge, telemetry and sync policies and a few AC processor settings), never a service,
            // task, feature removal or an undocumented knob.
            Assert.Equal(ValueBasis.Documented, o.Basis);
            Assert.True(o.Impact is OperationImpact.Power or OperationImpact.Privacy or OperationImpact.Network or OperationImpact.Interface or OperationImpact.Performance or OperationImpact.System, o.Id);
            Assert.True(o.Risk is RiskClass.SystemConfiguration or RiskClass.UserPreference, o.Id);
            Assert.True(o.Services.Count == 0 && o.Tasks.Count == 0 && o.Features.Count == 0, o.Id);
        });
        Assert.Contains(added, o => o.Impact == OperationImpact.Power);

        // The plan switch comes before the settings it would otherwise overwrite in the plan being left.
        var ids = maximum.OperationIds.ToList();
        Assert.True(ids.IndexOf("power.high-performance") < ids.IndexOf("power.pcie-aspm-off"));
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
    public void Desktop_power_entries_in_maximum_performance_are_refused_on_laptops()
    {
        var plan = Catalog.ById("power.high-performance")!;
        var pcie = Catalog.ById("power.pcie-aspm-off")!;

        Assert.Contains(DeviceRequirement.NoBattery, plan.Requirements);
        Assert.Contains(DeviceRequirement.NoModernStandby, plan.Requirements);
        Assert.Contains(DeviceRequirement.NoBattery, pcie.Requirements);
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
    public void Missing_fields_default_to_system_impact_an_empty_summary_and_a_collection()
    {
        var catalog = WindowsCatalog.Parse(CatalogWith(Op, """{ "id": "p", "name": "P", "operations": [ "a.b" ] }"""));

        Assert.Equal(OperationImpact.System, catalog.ById("a.b")!.Impact);
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
