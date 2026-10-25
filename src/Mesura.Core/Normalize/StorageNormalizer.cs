using System.Globalization;
using Mesura.Core.Facts;
using Mesura.Core.Raw;

namespace Mesura.Core.Normalize;

/// <summary>
/// Physical disks, their partitions and the volumes on them, joined only as <see cref="Rules.DiskPartitions"/>,
/// <see cref="Rules.PartitionVolume"/> and <see cref="Rules.VolumeDisk"/> allow (architecture §4.4 "Milestone 1.5").
/// A relationship that can't be established is a missing fact with its reason, never a guess.
/// </summary>
internal static class StorageNormalizer
{
    private const string Data = "/sources/storage/data";

    public static (Fact<IReadOnlyList<DiskFacts>> Disks, Fact<IReadOnlyList<VolumeFacts>> Volumes) Normalize(SourceEntry<StorageData> source)
    {
        if (!Evidence.TryData(source, "storage", out var storage, out var unavailable))
        {
            return (unavailable.Fact<IReadOnlyList<DiskFacts>>(), unavailable.Fact<IReadOnlyList<VolumeFacts>>());
        }

        // A volume path listed twice can't join anything: both are left out of the joins, as ambiguous.
        var repeatedPaths = storage.Volumes.GroupBy(volume => volume.Path, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var volumeIds = storage.Volumes
            .Select((volume, index) => (volume.Path, Id: VolumeId(index)))
            .Where(pair => !repeatedPaths.Contains(pair.Path))
            .ToDictionary(pair => pair.Path, pair => pair.Id, StringComparer.Ordinal);

        // Each physical disk's match to an OS disk, or why there is none.
        var matches = storage.PhysicalDisks.Select((disk, index) => Match(storage, disk, index)).ToList();

        var disks = storage.PhysicalDisks.Select((disk, index) =>
        {
            var at = $"{Data}/physicalDisks/{index}";
            return new DiskFacts(
                Id: DiskId(index),
                Model: Evidence.Observed(disk.Model, Basis.Authoritative, $"{at}/model"),
                MediaType: Evidence.Observed(disk.MediaType, Basis.Authoritative, $"{at}/mediaType"),
                BusType: Evidence.Observed(disk.BusType, Basis.Authoritative, $"{at}/busType"),
                Health: Evidence.Observed(disk.Health, Basis.Authoritative, $"{at}/health"),
                SizeBytes: Evidence.Observed(disk.SizeBytes, Basis.Authoritative, $"{at}/sizeBytes"),
                Location: Evidence.Observed(disk.Location, Basis.Authoritative, $"{at}/location"),
                Partitions: Evidence.Map(matches[index], number => Partitions(storage, number, volumeIds, repeatedPaths)));
        });

        var volumes = storage.Volumes.Select((volume, index) => Volume(storage, volume, index, matches, repeatedPaths.Contains(volume.Path)));

        return (
            Fact.Observed<IReadOnlyList<DiskFacts>>([.. disks], Basis.Authoritative, $"{Data}/physicalDisks"),
            Fact.Observed<IReadOnlyList<VolumeFacts>>([.. volumes], Basis.Authoritative, $"{Data}/volumes"));
    }

    /// <summary>
    /// <see cref="Rules.DiskPartitions"/>: the OS disk number this physical disk is, as a derived fact, or why it
    /// can't be told.
    /// </summary>
    private static Fact<int> Match(StorageData storage, PhysicalDisk disk, int index)
    {
        var at = $"{Data}/physicalDisks/{index}";
        var sameId = storage.PhysicalDisks.Count(other => other.DeviceId == disk.DeviceId);
        var candidates = storage.Disks
            .Select((osDisk, osIndex) => (osDisk, osIndex))
            .Where(pair => pair.osDisk.Number.ToString(CultureInfo.InvariantCulture) == disk.DeviceId)
            .ToList();

        if (sameId > 1 || candidates.Count > 1)
        {
            return Fact.Missing<int>(FactGap.Ambiguous, "More than one disk has this number, so the match is not claimed", $"{at}/deviceId", $"{Data}/disks");
        }

        if (candidates.Count == 0)
        {
            return Fact.Missing<int>(
                FactGap.Unmatched,
                "No OS disk has this disk's number; a disk in a storage pool isn't one, and the snapshot doesn't map pools",
                $"{at}/deviceId",
                $"{Data}/disks");
        }

        var (os, osIndex) = candidates[0];
        var osAt = $"{Data}/disks/{osIndex}";
        if (os.BusType.Value is BusKind.StorageSpaces or BusKind.FileBackedVirtual || !os.BusType.IsKnown)
        {
            return Fact.Missing<int>(
                FactGap.Unmatched,
                os.BusType.IsKnown
                    ? "The OS disk with this number is virtual (Storage Spaces or a file-backed disk), for which equal numbers are not a documented match"
                    : "The OS disk's bus type is unknown, so it can't be told from a virtual disk",
                $"{at}/deviceId",
                $"{osAt}/number",
                $"{osAt}/busType");
        }

        if (disk.Location.IsKnown && os.Location.IsKnown)
        {
            return disk.Location.Value == os.Location.Value
                ? Fact.Derived(os.Number, Basis.Authoritative, Rules.DiskPartitions, $"{at}/deviceId", $"{osAt}/number", $"{osAt}/busType", $"{at}/location", $"{osAt}/location")
                : Fact.Missing<int>(FactGap.Conflicting, "The disk and the OS disk share a number but report different locations", $"{at}/location", $"{osAt}/location");
        }

        return Fact.Derived(os.Number, Basis.Authoritative, Rules.DiskPartitions, $"{at}/deviceId", $"{osAt}/number", $"{osAt}/busType");
    }

    private static IReadOnlyList<PartitionFacts> Partitions(StorageData storage, int diskNumber, IReadOnlyDictionary<string, string> volumeIds, HashSet<string> repeatedPaths) =>
        [.. storage.Partitions
            .Select((partition, index) => (partition, index))
            .Where(pair => pair.partition.DiskNumber == diskNumber)
            .Select(pair =>
            {
                var at = $"{Data}/partitions/{pair.index}";
                var volumes = pair.partition.AccessPaths.Where(volumeIds.ContainsKey).Distinct(StringComparer.Ordinal).ToList();
                return new PartitionFacts(
                    Number: pair.partition.PartitionNumber,
                    SizeBytes: Evidence.Observed(pair.partition.SizeBytes, Basis.Authoritative, $"{at}/sizeBytes"),
                    Volume: volumes.Count switch
                    {
                        _ when volumes.Count > 1 || pair.partition.AccessPaths.Any(repeatedPaths.Contains) =>
                            Fact.Missing<string>(FactGap.Ambiguous, "More than one volume is among the partition's access paths, or its volume path is listed twice", $"{at}/accessPaths", $"{Data}/volumes"),
                        1 => Fact.Derived(volumeIds[volumes[0]], Basis.Authoritative, Rules.PartitionVolume, $"{at}/accessPaths", $"{Data}/volumes"),

                        // No access path at all: the platform reports nothing mounted from it (an MSR partition).
                        _ when pair.partition.AccessPaths.Count == 0 => Fact.Missing<string>(FactGap.NotPresent, "The partition has no access path, so no volume", $"{at}/accessPaths"),

                        // Access paths, but none is a listed volume (an EFI system partition's volume isn't listed to a standard user).
                        _ => Fact.Missing<string>(FactGap.Unmatched, "None of the partition's access paths is a volume the snapshot lists", $"{at}/accessPaths", $"{Data}/volumes"),
                    });
            })];

    private static VolumeFacts Volume(StorageData storage, StorageVolume volume, int index, IReadOnlyList<Fact<int>> matches, bool pathRepeated)
    {
        var at = $"{Data}/volumes/{index}";
        var holders = storage.Partitions
            .Select((partition, partitionIndex) => (partition, partitionIndex))
            .Where(pair => pair.partition.AccessPaths.Contains(volume.Path, StringComparer.Ordinal))
            .ToList();

        Fact<IReadOnlyList<string>> mountPoints;
        Fact<string> disk;
        if (holders.Count != 1 || pathRepeated)
        {
            var gap = holders.Count == 0 && !pathRepeated ? FactGap.Unmatched : FactGap.Ambiguous;
            var why = pathRepeated ? "Another volume in the snapshot has the same path, so it can't be told apart"
                : holders.Count == 0 ? "No partition lists this volume among its access paths"
                : "More than one partition lists this volume (a spanned volume)";
            mountPoints = Fact.Missing<IReadOnlyList<string>>(gap, why, $"{at}/path", $"{Data}/partitions");
            disk = Fact.Missing<string>(gap, why, $"{at}/path", $"{Data}/partitions");
        }
        else
        {
            var (partition, partitionIndex) = holders[0];
            var partitionAt = $"{Data}/partitions/{partitionIndex}";
            var volumePaths = storage.Volumes.Select(other => other.Path).ToHashSet(StringComparer.Ordinal);

            // The access paths less every volume's opaque path: drive roots and folders, profile already rewritten.
            mountPoints = Fact.Derived<IReadOnlyList<string>>(
                [.. partition.AccessPaths.Where(path => !volumePaths.Contains(path))],
                Basis.Authoritative,
                Rules.VolumeMountPoints,
                $"{partitionAt}/accessPaths",
                $"{Data}/volumes");
            disk = DiskOf(storage, partition, partitionAt, matches);
        }

        return new VolumeFacts(
            Id: VolumeId(index),
            DriveLetter: Evidence.Observed(volume.DriveLetter, Basis.Authoritative, $"{at}/driveLetter"),
            FileSystem: Evidence.Observed(volume.FileSystem, Basis.Authoritative, $"{at}/fileSystem"),
            SizeBytes: Evidence.Observed(volume.SizeBytes, Basis.Authoritative, $"{at}/sizeBytes"),
            FreeBytes: Evidence.Observed(volume.FreeBytes, Basis.Authoritative, $"{at}/freeBytes"),
            MountPoints: mountPoints,
            Disk: disk);
    }

    /// <summary><see cref="Rules.VolumeDisk"/>: the physical disk matched to the partition's OS disk, or why none is.</summary>
    private static Fact<string> DiskOf(StorageData storage, DiskPartition partition, string partitionAt, IReadOnlyList<Fact<int>> matches)
    {
        var matched = matches
            .Select((match, diskIndex) => (match, diskIndex))
            .Where(pair => pair.match.IsKnown && pair.match.Value == partition.DiskNumber)
            .ToList();

        if (matched.Count == 1)
        {
            var (match, diskIndex) = matched[0];
            return Fact.Derived(DiskId(diskIndex), Basis.Authoritative, Rules.VolumeDisk, [$"{partitionAt}/diskNumber", .. match.From]);
        }

        // A physical disk that was tried for this OS disk and not matched says why (ambiguous, conflicting, virtual).
        var number = partition.DiskNumber.ToString(CultureInfo.InvariantCulture);
        var tried = storage.PhysicalDisks
            .Select((disk, diskIndex) => (disk, match: matches[diskIndex]))
            .Where(pair => pair.disk.DeviceId == number && !pair.match.IsKnown)
            .Select(pair => pair.match)
            .ToList();
        if (tried.Count > 0)
        {
            var gap = tried.Count > 1 ? FactGap.Ambiguous : tried[0].Missing!.Value;
            return Fact.Missing<string>(gap, $"The partition's OS disk isn't matched to one physical disk: {tried[0].Detail}", [$"{partitionAt}/diskNumber", .. tried[0].From]);
        }

        var osDisk = storage.Disks.FirstOrDefault(disk => disk.Number == partition.DiskNumber);
        var why = osDisk switch
        {
            null => "The snapshot has no OS disk with the partition's disk number",
            { BusType.Value: BusKind.StorageSpaces or BusKind.FileBackedVirtual } => "The partition is on a virtual disk (Storage Spaces or file-backed), which isn't attributed to physical disks",
            _ => "No physical disk has the number of the partition's OS disk (a storage-pool member isn't a disk)",
        };

        return Fact.Missing<string>(FactGap.Unmatched, why, $"{partitionAt}/diskNumber", $"{Data}/disks");
    }

    private static string DiskId(int index) => string.Create(CultureInfo.InvariantCulture, $"disk-{index + 1}");

    private static string VolumeId(int index) => string.Create(CultureInfo.InvariantCulture, $"volume-{index + 1}");
}
