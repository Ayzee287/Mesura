using FpsUtility.Core.Facts;
using FpsUtility.Core.Normalize;
using FpsUtility.Core.Raw;

namespace FpsUtility.Core.Tests.Normalize;

/// <summary>Disks, partitions and volumes: the joins <see cref="Rules.DiskPartitions"/> allows, and the reasons when they fail.</summary>
public sealed class StorageNormalizationTests
{
    private const string Nvme = "PCI Slot 2 : Bus 1 : Device 0 : Function 0 : Adapter 0";

    [Fact]
    public void A_disk_is_joined_to_its_partitions_by_number_and_location_and_its_volume_to_it()
    {
        var facts = Normalizer.Normalize(SnapshotFixture.Create());
        var nvme = facts.Disks.Value!.Single(disk => disk.Model.Value == "TEST NVME 1TB");
        var c = facts.Volumes.Value!.Single(volume => volume.DriveLetter.Value == "C");

        Assert.Equal(Rules.DiskPartitions, nvme.Partitions.Rule);
        Assert.Contains($"/sources/storage/data/physicalDisks/1/location", nvme.Partitions.From); // the location corroborated the number
        Assert.Equal([1, 2], nvme.Partitions.Value!.Select(partition => partition.Number));
        Assert.Equal(nvme.Id, c.Disk.Value);
        Assert.Equal([@"C:\"], c.MountPoints.Value); // the opaque volume path is a join key, never a mount point
    }

    [Fact]
    public void Without_a_location_the_documented_number_join_still_holds()
    {
        var hdd = Normalizer.Normalize(SnapshotFixture.Create()).Disks.Value!.Single(disk => disk.Model.Value == "TEST HDD 2TB");

        Assert.True(hdd.Partitions.IsKnown);
        Assert.DoesNotContain(hdd.Partitions.From, pointer => pointer.EndsWith("/location", StringComparison.Ordinal));
    }

    [Fact]
    public void Model_is_the_hardware_model_as_reported()
    {
        var disk = Normalizer.Normalize(SnapshotFixture.Create()).Disks.Value![1];

        Assert.Equal("TEST NVME 1TB", disk.Model.Value);
        Assert.Equal("/sources/storage/data/physicalDisks/1/model", disk.Model.From.Single());
        Assert.Equal(Basis.Authoritative, disk.Model.Basis);
    }

    [Fact]
    public void Locations_that_disagree_make_the_relationship_conflicting_not_guessed()
    {
        var facts = Normalizer.Normalize(WithDisks((physical, os) => (physical, [new OsDisk(0, Reading.Known(BusKind.Nvme, 17), Reading.Known("PCI Slot 9 : Bus 4")), os[0]])));
        var nvme = facts.Disks.Value!.Single(disk => disk.Model.Value == "TEST NVME 1TB");
        var c = facts.Volumes.Value!.Single(volume => volume.DriveLetter.Value == "C");

        Assert.Equal(FactGap.Conflicting, nvme.Partitions.Missing);
        Assert.Equal(FactGap.Conflicting, c.Disk.Missing); // the volume carries its disk's reason
    }

    [Theory]
    [InlineData(BusKind.StorageSpaces, 16)]
    [InlineData(BusKind.FileBackedVirtual, 15)]
    public void A_virtual_os_disk_is_never_matched_to_a_physical_disk_by_a_coincidental_number(BusKind bus, int code)
    {
        var facts = Normalizer.Normalize(WithDisks((physical, os) => (physical, [new OsDisk(0, Reading.Known(bus, code), Reading.Missing<string>(MissingReason.NotPresent, "none")), os[0]])));
        var nvme = facts.Disks.Value!.Single(disk => disk.Model.Value == "TEST NVME 1TB");
        var c = facts.Volumes.Value!.Single(volume => volume.DriveLetter.Value == "C");

        Assert.Equal(FactGap.Unmatched, nvme.Partitions.Missing);
        Assert.Equal(FactGap.Unmatched, c.Disk.Missing);
        Assert.Contains("virtual", c.Disk.Detail, StringComparison.Ordinal);
        Assert.Equal([@"C:\"], c.MountPoints.Value); // what is known about the volume stays known
    }

    [Fact]
    public void A_physical_disk_no_os_disk_numbers_is_unmatched_not_partitionless()
    {
        var facts = Normalizer.Normalize(WithDisks((physical, os) => (physical, [os[0]])));
        var nvme = facts.Disks.Value!.Single(disk => disk.Model.Value == "TEST NVME 1TB");

        Assert.Equal(FactGap.Unmatched, nvme.Partitions.Missing);
    }

    [Fact]
    public void A_matched_disk_with_no_partitions_knows_it_has_none()
    {
        var facts = Normalizer.Normalize(RawBuilder.With(sources =>
        {
            var storage = sources.Storage.Data!;
            return sources with { Storage = RawBuilder.Ok(new StorageData(storage.PhysicalDisks, storage.Disks, [.. storage.Partitions.Where(partition => partition.DiskNumber != 1)], storage.Volumes)) };
        }));
        var hdd = facts.Disks.Value!.Single(disk => disk.Model.Value == "TEST HDD 2TB");

        Assert.True(hdd.Partitions.IsKnown);
        Assert.Empty(hdd.Partitions.Value!); // "none" is a known fact, not a gap
    }

    [Fact]
    public void A_partition_without_access_paths_holds_no_volume_and_one_with_an_unlisted_path_is_unmatched()
    {
        var facts = Normalizer.Normalize(RawBuilder.With(sources =>
        {
            var storage = sources.Storage.Data!;
            DiskPartition[] partitions =
            [
                new(0, 1, Reading.Known(100UL), []),
                new(0, 2, Reading.Known(999_000_000_000UL), [@"C:\", @"\\?\Volume{00000000-0000-0000-0000-000000000002}\"]),
                new(0, 3, Reading.Known(200UL), [@"\\?\Volume{00000000-0000-0000-0000-00000000000f}\"]),
            ];
            return sources with { Storage = RawBuilder.Ok(new StorageData(storage.PhysicalDisks, storage.Disks, partitions, storage.Volumes)) };
        }));
        var partitionsOfNvme = facts.Disks.Value!.Single(disk => disk.Model.Value == "TEST NVME 1TB").Partitions.Value!;

        Assert.Equal(FactGap.NotPresent, partitionsOfNvme[0].Volume.Missing);
        Assert.True(partitionsOfNvme[1].Volume.IsKnown);
        Assert.Equal(FactGap.Unmatched, partitionsOfNvme[2].Volume.Missing);
    }

    [Fact]
    public void An_unavailable_storage_source_leaves_disks_and_volumes_missing_not_empty()
    {
        var facts = Normalizer.Normalize(RawBuilder.With(sources => sources with { Storage = RawBuilder.Unavailable<StorageData>(MissingReason.AccessDenied) }));

        Assert.Equal(FactGap.AccessDenied, facts.Disks.Missing);
        Assert.Equal(FactGap.AccessDenied, facts.Volumes.Missing);
        Assert.Null(facts.Disks.Value);
    }

    [Fact]
    public void A_withheld_location_stays_withheld()
    {
        var facts = Normalizer.Normalize(WithDisks((physical, os) =>
            ([physical[0] with { Location = Reading.Missing<string>(MissingReason.Withheld, "a file path") }, physical[1]], os)));

        Assert.Equal(FactGap.Withheld, facts.Disks.Value!.Single(disk => disk.Model.Value == "TEST NVME 1TB").Location.Missing);
    }

    [Fact]
    public void A_volume_path_listed_twice_joins_nothing_and_crashes_nothing()
    {
        var facts = Normalizer.Normalize(RawBuilder.With(sources =>
        {
            var storage = sources.Storage.Data!;
            return sources with { Storage = RawBuilder.Ok(new StorageData(storage.PhysicalDisks, storage.Disks, storage.Partitions, [.. storage.Volumes, storage.Volumes[0]])) };
        }));
        var repeated = facts.Volumes.Value!.Where(volume => volume.DriveLetter.Value == "C").ToList();

        Assert.Equal(2, repeated.Count);
        Assert.All(repeated, volume => Assert.Equal(FactGap.Ambiguous, volume.Disk.Missing));
        Assert.Contains(facts.Disks.Value!.SelectMany(disk => disk.Partitions.Value ?? []), partition => partition.Volume.Missing == FactGap.Ambiguous);
    }

    private static RawSnapshot WithDisks(Func<IReadOnlyList<PhysicalDisk>, IReadOnlyList<OsDisk>, (IReadOnlyList<PhysicalDisk>, IReadOnlyList<OsDisk>)> change) =>
        RawBuilder.With(sources =>
        {
            var storage = sources.Storage.Data!;

            // Raw order: physical disks and OS disks by location, so the NVMe's OS disk is second (no location sorts first).
            var nvmePhysical = storage.PhysicalDisks.Single(disk => disk.Location.Value == Nvme);
            var hddPhysical = storage.PhysicalDisks.Single(disk => disk.Location.Value != Nvme);
            var hddOs = storage.Disks.Single(disk => disk.Number == 1);
            var (physical, os) = change([nvmePhysical, hddPhysical], [hddOs]);
            return sources with { Storage = RawBuilder.Ok(new StorageData(physical, os, storage.Partitions, storage.Volumes)) };
        });
}
