using FpsUtility.Core.Privacy;
using FpsUtility.Core.Raw;
using FpsUtility.Core.Raw.Serialization;
using FpsUtility.Platform.Windows;

namespace FpsUtility.Platform.Tests;

/// <summary>
/// One real collection on the machine running the tests (architecture §11, "collectors never throw, snapshot
/// round-trips"). It asserts invariants only, never this machine's hardware, so it passes on any supported PC.
/// </summary>
public sealed class CollectionTests
{
    private static readonly Lazy<RawSnapshot> Snapshot = new(() =>
        SnapshotCollector.Collect(OsInfoReader.Read(), new AppInfo("FpsUtility", "0.0.0-test", Reading.Missing<string>(MissingReason.NotPresent, "test build"))));

    [Fact]
    public void Every_source_is_ok_or_unavailable_with_a_reason_and_nothing_throws()
    {
        var sources = Snapshot.Value.Sources;
        (SourceStatus Status, MissingReason? Reason)[] entries =
        [
            (sources.Display.Status, sources.Display.Reason),
            (sources.Edid.Status, sources.Edid.Reason),
            (sources.Adapters.Status, sources.Adapters.Reason),
            (sources.Smbios.Status, sources.Smbios.Reason),
            (sources.Pcie.Status, sources.Pcie.Reason),
            (sources.Storage.Status, sources.Storage.Reason),
            (sources.Steam.Status, sources.Steam.Reason),
            (sources.Power.Status, sources.Power.Reason),
        ];

        Assert.All(entries, entry => Assert.True(entry.Status == SourceStatus.Ok || entry.Reason is not null));
    }

    [Fact]
    public void A_real_snapshot_round_trips_to_identical_bytes()
    {
        var json = SnapshotJson.Serialize(Snapshot.Value);

        Assert.Equal(json, SnapshotJson.Serialize(SnapshotJson.Deserialize(json)));
    }

    [Fact]
    public void A_real_snapshot_holds_no_edid_identifier_the_scrub_knows_how_to_find()
    {
        // Scrubbing is idempotent: what collection stored has nothing left for the scrubber to remove.
        var monitors = Snapshot.Value.Sources.Edid.Data?.Monitors ?? [];

        Assert.All(
            monitors.Where(monitor => monitor.Edid.IsKnown),
            monitor => Assert.Equal(0, EdidScrubber.Scrub(monitor.Edid.Value!.Bytes).FieldsScrubbed));
    }

    [Fact]
    public void The_storage_section_reads_the_location_key_and_no_friendly_name()
    {
        var storage = Snapshot.Value.Sources.Storage;
        if (storage.Status != SourceStatus.Ok)
        {
            return; // nothing to check when Storage WMI is unavailable on this machine
        }

        var json = System.Text.Encoding.UTF8.GetString(SnapshotJson.Serialize(Snapshot.Value));
        Assert.Contains("\"location\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("friendlyName", json, StringComparison.OrdinalIgnoreCase);
        Assert.All(storage.Data!.Partitions, partition => Assert.Equal(partition.AccessPaths.Order(StringComparer.Ordinal), partition.AccessPaths));
    }
}
