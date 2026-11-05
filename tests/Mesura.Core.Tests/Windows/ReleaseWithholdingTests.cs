using Mesura.Core.Windows;

namespace Mesura.Core.Tests.Windows;

/// <summary>
/// The generic network adapter tuning (14 operations that write a physical adapter's driver settings by standard keyword; their writes have never run on a real adapter) stays held back without being
/// deleted: the product's catalog does not list it, the full catalog and the code keep it for the next update. The five per-maker adapter presets were held back too until the owner's decision of
/// 2026-11-04 to put them in both profiles; they are in the product's catalog now.
/// </summary>
public sealed class ReleaseWithholdingTests
{
    private static readonly string[] Presets =
        ["network.preset-realtek-ethernet", "network.preset-intel-ethernet", "network.preset-killer-ethernet", "network.preset-intel-wifi", "network.preset-realtek-wifi"];

    [Fact]
    public void The_products_catalog_lists_no_generic_adapter_operation_and_not_their_collection()
    {
        Assert.All(WindowsCatalog.Default.Operations.Where(o => o.AdapterSettings.Count > 0), o => Assert.Contains(o.Id, Presets));
        Assert.Null(WindowsCatalog.Default.PresetById("network-adapter"));
        Assert.Null(WindowsCatalog.Default.ById("network.adapter-eee-off"));
        Assert.Null(WindowsCatalog.Default.ById("network.adapter-interrupt-moderation-off"));
        Assert.NotNull(WindowsCatalog.Default.ById("network.wifi-power-saving-max-performance")); // a power-plan setting, not an adapter write: stays
    }

    [Fact]
    public void The_five_per_maker_presets_are_in_the_products_catalog_each_for_its_own_maker_and_kind()
    {
        Assert.Equal(Presets.Order(), WindowsCatalog.Default.Operations.Where(o => o.AdapterSettings.Count > 0).Select(o => o.Id).Order());
        Assert.Equal(Presets, WindowsCatalog.Default.PresetById("network-adapter-makers")!.OperationIds);
        Assert.All(Presets.Select(id => WindowsCatalog.Default.ById(id)!), o =>
        {
            Assert.All(o.AdapterSettings, s => Assert.NotEmpty(s.Vendors)); // never written to another maker's card
            Assert.Equal(ValueBasis.MakerDocumented, o.Basis);
        });
    }

    [Fact]
    public void The_full_catalog_keeps_the_held_back_part_for_the_next_update()
    {
        Assert.Equal(19, WindowsCatalog.Full.Operations.Count(o => o.AdapterSettings.Count > 0));   // 14 generic adapter operations + the 5 per-maker presets
        Assert.Equal(9, WindowsCatalog.Full.PresetById("network-adapter")!.OperationIds.Count);
        Assert.Equal(WindowsCatalog.Default.Operations.Count + 14, WindowsCatalog.Full.Operations.Count);
        Assert.Equal(5, WindowsCatalog.Full.PresetById("network-adapter-makers")!.OperationIds.Count);
    }
}
