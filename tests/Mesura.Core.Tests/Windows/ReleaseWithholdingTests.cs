using Mesura.Core.Windows;

namespace Mesura.Core.Tests.Windows;

/// <summary>
/// 0.5.5 holds back the network adapter tuning (its writes have never run on a real adapter) without deleting it: the product's catalog does not list it, the full catalog and the code keep it for the next update.
/// </summary>
public sealed class ReleaseWithholdingTests
{
    [Fact]
    public void The_products_catalog_lists_no_operation_that_writes_a_network_adapter_and_not_the_collection()
    {
        Assert.DoesNotContain(WindowsCatalog.Default.Operations, o => o.AdapterSettings.Count > 0);
        Assert.Null(WindowsCatalog.Default.PresetById("network-adapter"));
        Assert.Null(WindowsCatalog.Default.ById("network.adapter-eee-off"));
        Assert.NotNull(WindowsCatalog.Default.ById("network.wifi-power-saving-max-performance")); // a power-plan setting, not an adapter write: stays
    }

    [Fact]
    public void The_full_catalog_keeps_all_of_it_for_the_next_update()
    {
        Assert.Equal(14, WindowsCatalog.Full.Operations.Count(o => o.AdapterSettings.Count > 0));
        Assert.Equal(9, WindowsCatalog.Full.PresetById("network-adapter")!.OperationIds.Count);
        Assert.Equal(WindowsCatalog.Default.Operations.Count + 14, WindowsCatalog.Full.Operations.Count);
    }
}
