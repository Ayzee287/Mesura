using System.Text.Json.Serialization;
using FpsUtility.Core.Raw;
using FpsUtility.Core.Raw.Serialization;

namespace FpsUtility.Core.Facts;

/// <summary>A physical storage device (raw <c>storage.physicalDisks</c>), with its partitions where they can be established.</summary>
/// <param name="Id">Snapshot-scoped, in the raw snapshot's order of physical disks (location first).</param>
/// <param name="Model">The hardware model string, never the user-editable friendly name.</param>
/// <param name="MediaType">As the storage stack reports it; <c>unspecified</c> is the stack's own answer.</param>
/// <param name="Health">The storage stack's coarse verdict; <c>unknown</c> is the stack's own answer.</param>
/// <param name="Location">
/// Where the disk is attached, as the platform describes it. It names a place, not a unit: expected to hold across
/// restarts while the disk stays where it is (not verified), it changes when the disk moves, and another disk in the
/// same place gets the same value. Use it to match a disk across scans only together with its model and size.
/// </param>
/// <param name="Partitions">
/// The disk's partitions: a known empty list means the disk was matched and has none; missing means the
/// relationship couldn't be established (<see cref="Rules.DiskPartitions"/>).
/// </param>
public sealed record DiskFacts(
    string Id,
    Fact<string> Model,
    Fact<MediaKind> MediaType,
    Fact<BusKind> BusType,
    Fact<HealthState> Health,
    Fact<ulong> SizeBytes,
    Fact<string> Location,
    Fact<IReadOnlyList<PartitionFacts>> Partitions);

/// <param name="Number">The partition's number on its disk, as the platform numbers it.</param>
/// <param name="Volume">The <see cref="VolumeFacts.Id"/> of the partition's volume; missing (<c>notPresent</c>) when the partition holds none.</param>
public sealed record PartitionFacts(int Number, Fact<ulong> SizeBytes, Fact<string> Volume);

/// <summary>A volume (raw <c>storage.volumes</c>). Its opaque platform path is used only to join, never shown.</summary>
/// <param name="Id">Snapshot-scoped, in the raw snapshot's order of volumes.</param>
/// <param name="DriveLetter">The drive letter; missing (<c>notPresent</c>) when the volume has none.</param>
/// <param name="FreeBytes">Free space when the scan ran (run-dependent).</param>
/// <param name="MountPoints">Where the volume is mounted (drive roots and folders), user profile rewritten.</param>
/// <param name="Disk">The <see cref="DiskFacts.Id"/> of the physical disk holding the volume.</param>
public sealed record VolumeFacts(
    string Id,
    Fact<string> DriveLetter,
    Fact<string> FileSystem,
    Fact<ulong> SizeBytes,
    Fact<ulong> FreeBytes,
    Fact<IReadOnlyList<string>> MountPoints,
    Fact<string> Disk);
