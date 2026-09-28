using System.Text.Json.Serialization;
using FpsUtility.Core.Raw.Serialization;

namespace FpsUtility.Core.Raw;

/// <summary>
/// The <c>storage</c> section: physical disks, partitions and volumes (architecture §4.4). No serial
/// numbers or unique disk identifiers are collected (§13). The disk ↔ partition ↔ volume joins are
/// the normaliser's: partitions carry their access paths, volumes their path.
/// </summary>
public sealed record StorageData
{
    [JsonConstructor]
    public StorageData(IReadOnlyList<PhysicalDisk> physicalDisks, IReadOnlyList<DiskPartition> partitions, IReadOnlyList<StorageVolume> volumes)
    {
        PhysicalDisks = Canonical.Sort(physicalDisks, static (a, b) => Canonical.Ordinal(a.DeviceId, b.DeviceId));
        Partitions = Canonical.Sort(partitions, static (a, b) =>
        {
            var order = a.DiskNumber.CompareTo(b.DiskNumber);
            return order != 0 ? order : a.PartitionNumber.CompareTo(b.PartitionNumber);
        });
        Volumes = Canonical.Sort(volumes, static (a, b) => Canonical.Ordinal(a.Path, b.Path));
    }

    public IReadOnlyList<PhysicalDisk> PhysicalDisks { get; }

    public IReadOnlyList<DiskPartition> Partitions { get; }

    public IReadOnlyList<StorageVolume> Volumes { get; }
}

/// <param name="DeviceId">Opaque id the platform gives the disk.</param>
/// <param name="Model">The disk's display name (model), as reported.</param>
/// <param name="MediaType">Native: the platform's media-type code.</param>
/// <param name="BusType">Native: the platform's bus-type code.</param>
/// <param name="Health">The platform's own coarse health verdict. Native: its health code.</param>
/// <param name="OperationalStatus">DMTF CIM operational-status codes, in the order reported.</param>
/// <param name="SizeBytes">Total size in bytes.</param>
public sealed record PhysicalDisk(
    string DeviceId,
    Reading<string> Model,
    Reading<MediaKind> MediaType,
    Reading<BusKind> BusType,
    Reading<HealthState> Health,
    Reading<IReadOnlyList<int>> OperationalStatus,
    Reading<ulong> SizeBytes);

/// <param name="AccessPaths">The partition's access paths as reported (volume path, drive root, mount folders), user profile rewritten.</param>
public sealed record DiskPartition(int DiskNumber, int PartitionNumber, Reading<ulong> SizeBytes, IReadOnlyList<string> AccessPaths);

/// <param name="Path">Opaque volume path; partitions list it among their access paths.</param>
/// <param name="FreeBytes">Free space. Run-dependent (§4.4).</param>
public sealed record StorageVolume(
    string Path,
    Reading<string> DriveLetter,
    Reading<string> FileSystem,
    Reading<ulong> SizeBytes,
    Reading<ulong> FreeBytes);

[JsonConverter(typeof(CamelCaseEnumConverter<MediaKind>))]
public enum MediaKind
{
    /// <summary>The platform reports the media type as unspecified.</summary>
    Unspecified,
    Hdd,
    Ssd,

    /// <summary>Storage-class memory.</summary>
    Scm,
}

[JsonConverter(typeof(CamelCaseEnumConverter<BusKind>))]
public enum BusKind
{
    /// <summary>The platform reports the bus type as unknown.</summary>
    Unknown,
    Scsi,
    Atapi,
    Ata,
    Ieee1394,
    Ssa,
    FibreChannel,
    Usb,
    Raid,
    Iscsi,
    Sas,
    Sata,
    Sd,
    Mmc,
    FileBackedVirtual,
    StorageSpaces,
    Nvme,
}

[JsonConverter(typeof(CamelCaseEnumConverter<HealthState>))]
public enum HealthState
{
    Healthy,
    Warning,
    Unhealthy,

    /// <summary>The platform reports the health as unknown.</summary>
    Unknown,
}
