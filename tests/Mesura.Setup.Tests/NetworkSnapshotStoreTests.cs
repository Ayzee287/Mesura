using Mesura.Setup.Windows;

namespace Mesura.Setup.Tests;

/// <summary>The network snapshot files (0.6): written whole, listed newest first, read back, and an id can never leave the folder.</summary>
public sealed class NetworkSnapshotStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mesura-network-snapshots-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private SetupStore Store => new(_dir, legacyRoot: null);

    [Fact]
    public void A_snapshot_is_written_listed_newest_first_and_read_back()
    {
        var store = Store;
        Assert.Empty(store.ListNetworkSnapshots());
        Assert.Null(store.ReadNetworkSnapshot("20261104T100000Z-snapshot"));
        store.WriteNetworkSnapshot("20261104T100000Z-snapshot", "{\"a\":1}");
        store.WriteNetworkSnapshot("20261104T110000Z-before-restore", "{\"a\":2}");
        Assert.Equal(["20261104T110000Z-before-restore", "20261104T100000Z-snapshot"], store.ListNetworkSnapshots());
        Assert.Equal("{\"a\":2}", store.ReadNetworkSnapshot("20261104T110000Z-before-restore"));
        Assert.DoesNotContain(Directory.EnumerateFiles(store.NetworkFolder), f => f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_id_cannot_name_a_file_outside_the_folder()
    {
        var store = Store;
        var path = store.WriteNetworkSnapshot(@"..\..\escape", "x");
        Assert.Equal(Path.GetFullPath(store.NetworkFolder), Path.GetDirectoryName(Path.GetFullPath(path)));
    }
}
