using Mesura.Core.Windows;

namespace Mesura.Core.Tests.Windows;

/// <summary>
/// The generic network adapter tuning (14 operations that write a physical adapter's driver settings by standard keyword) was held back through 0.6; the owner released it on 2026-11-07 as
/// individually selectable advanced settings (never in a profile). The five per-maker adapter presets were released on 2026-11-04.
/// </summary>
public sealed class ReleaseWithholdingTests
{
    private static readonly string[] MakerPresets =
        ["network.preset-realtek-ethernet", "network.preset-intel-ethernet", "network.preset-killer-ethernet", "network.preset-intel-wifi", "network.preset-realtek-wifi"];

    [Fact]
    public void The_products_catalog_lists_the_generic_adapter_operations_and_their_collection()
    {
        Assert.NotNull(WindowsCatalog.Default.PresetById("network-adapter"));
        Assert.Equal(9, WindowsCatalog.Default.PresetById("network-adapter")!.OperationIds.Count);
        Assert.NotNull(WindowsCatalog.Default.ById("network.adapter-eee-off"));
        Assert.NotNull(WindowsCatalog.Default.ById("network.adapter-interrupt-moderation-off"));
        // 13 generic (14 until 0.9 retired the IPv6 unbinding, D057) + the 5 per-maker presets.
        Assert.Equal(18, WindowsCatalog.Default.Operations.Count(o => o.AdapterSettings.Count > 0));
        Assert.Null(WindowsCatalog.Default.ById("network.adapter-ipv6-binding-off"));
    }

    [Fact]
    public void The_five_per_maker_presets_are_in_the_products_catalog_each_for_its_own_maker_and_kind()
    {
        Assert.Equal(MakerPresets, WindowsCatalog.Default.PresetById("network-adapter-makers")!.OperationIds);
        Assert.All(MakerPresets.Select(id => WindowsCatalog.Default.ById(id)!), o =>
        {
            Assert.All(o.AdapterSettings, s => Assert.NotEmpty(s.Vendors)); // never written to another maker's card
            Assert.Equal(ValueBasis.MakerDocumented, o.Basis);
        });
    }

    [Fact]
    public void Nothing_is_held_back_any_more_so_the_full_and_the_products_catalogs_agree()
    {
        Assert.Equal(WindowsCatalog.Default.Operations.Count, WindowsCatalog.Full.Operations.Count);
        Assert.Equal(WindowsCatalog.Default.Presets.Count, WindowsCatalog.Full.Presets.Count);
    }

    [Fact]
    public void The_generic_adapter_operations_are_in_no_profile_so_they_never_reach_a_card_without_being_chosen()
    {
        var generic = WindowsCatalog.Default.Operations
            .Where(o => o.AdapterSettings.Count > 0 && !MakerPresets.Contains(o.Id)).Select(o => o.Id).ToHashSet();
        Assert.Equal(13, generic.Count); // 14 until 0.9 retired the IPv6 unbinding (D057)
        foreach (var profile in new[] { "recommended-gaming", "maximum-performance" })
        {
            Assert.All(WindowsCatalog.Default.PresetById(profile)!.OperationIds, id => Assert.DoesNotContain(id, generic));
        }
    }
}
