using System.Text.Json.Serialization;
using FpsUtility.Core.Raw.Serialization;

namespace FpsUtility.Core.Raw;

/// <summary>
/// The <c>storage</c> section: physical disks, the operating system's disks, partitions and volumes
/// (architecture §4.4). No serial numbers, unique IDs, disk GUIDs or signatures are collected (§13).
/// The joins are the normaliser's: a partition names its OS disk by number, a volume is listed among
/// its partition's access paths, and an OS disk and a physical disk share a number (for a disk that
/// isn't virtual) and, where the platform reports both, a location.
/// </summary>
/// <remarks>
/// Order: physical disks by location, then device id; OS disks by location, then number; partitions by
/// disk number, then partition number; volumes by path. Locations are expected to hold across restarts;
/// disk numbers and device ids may not (§4.4 run-dependent values).
/// </remarks>
public sealed record StorageData
{
    [JsonConstructor]
    public StorageData(IReadOnlyList<PhysicalDisk> physicalDisks, IReadOnlyList<OsDisk> disks, IReadOnlyList<DiskPartition> partitions, IReadOnlyList<StorageVolume> volumes)
    {
        PhysicalDisks = Canonical.Sort(physicalDisks, static (a, b) =>
        {
            var order = Canonical.Ordinal(a.Location.Value, b.Location.Value);
            return order != 0 ? order : Canonical.Ordinal(a.DeviceId, b.DeviceId);
        });
        Disks = Canonical.Sort(disks, static (a, b) =>
        {
            var order = Canonical.Ordinal(a.Location.Value, b.Location.Value);
            return order != 0 ? order : a.Number.CompareTo(b.Number);
        });
        Partitions = Canonical.Sort(partitions, static (a, b) =>
        {
            var order = a.DiskNumber.CompareTo(b.DiskNumber);
            return order != 0 ? order : a.PartitionNumber.CompareTo(b.PartitionNumber);
        });
        Volumes = Canonical.Sort(volumes, static (a, b) => Canonical.Ordinal(a.Path, b.Path));
    }

    public IReadOnlyList<PhysicalDisk> PhysicalDisks { get; }

    public IReadOnlyList<OsDisk> Disks { get; }

    public IReadOnlyList<DiskPartition> Partitions { get; }

    public IReadOnlyList<StorageVolume> Volumes { get; }
}

/// <summary>A storage device as the platform's storage stack reports it.</summary>
/// <param name="DeviceId">Opaque id the platform gives the disk. Windows: <c>MSFT_PhysicalDisk.DeviceId</c>, which for a disk outside a storage pool equals its OS disk number, so it is run-dependent (§4.4).</param>
/// <param name="Location">
/// Where the device is attached, as the platform describes it (Windows: <c>MSFT_PhysicalDisk.PhysicalLocation</c>, a
/// free-form string such as <c>PCI Slot 8 : Bus 1 : Device 0 : Function 0 : Adapter 0</c>). It names a place, not a
/// unit: it changes if the disk moves and says nothing about which device sits there.
/// </param>
/// <param name="Model">The hardware model string (Windows: <c>MSFT_PhysicalDisk.Model</c>, which must match the device's inquiry data). Never the user-editable friendly name.</param>
/// <param name="MediaType">Native: the platform's media-type code.</param>
/// <param name="BusType">Native: the platform's bus-type code.</param>
/// <param name="Health">The platform's own coarse health verdict. Native: its health code.</param>
/// <param name="OperationalStatus">DMTF CIM operational-status codes, in the order reported (positional: the order is part of the value).</param>
/// <param name="SizeBytes">Total size in bytes.</param>
public sealed record PhysicalDisk(
    string DeviceId,
    Reading<string> Location,
    Reading<string> Model,
    Reading<MediaKind> MediaType,
    Reading<BusKind> BusType,
    Reading<HealthState> Health,
    Reading<IReadOnlyList<int>> OperationalStatus,
    Reading<ulong> SizeBytes);

/// <summary>
/// A disk as the operating system numbers it: the device partitions are created on (Windows: <c>MSFT_Disk</c>).
/// It is usually one physical disk. A Storage Spaces or file-backed virtual disk is not, which is why its bus
/// type is kept.
/// </summary>
/// <param name="Number">The OS's disk number; partitions name their disk by it. Run-dependent (§4.4).</param>
/// <param name="BusType">Native: the platform's bus-type code.</param>
/// <param name="Location">Where the disk is attached, as the platform describes it (Windows: <c>MSFT_Disk.Location</c>, documented as the PnP location path; NULL for Hyper-V and VHD disks).</param>
public sealed record OsDisk(int Number, Reading<BusKind> BusType, Reading<string> Location);

/// <summary>One partition.</summary>
public sealed record DiskPartition
{
    [JsonConstructor]
    public DiskPartition(int diskNumber, int partitionNumber, Reading<ulong> sizeBytes, IReadOnlyList<string> accessPaths)
    {
        DiskNumber = diskNumber;
        PartitionNumber = partitionNumber;
        SizeBytes = sizeBytes;
        AccessPaths = Canonical.Sort(accessPaths, static (a, b) => Canonical.Ordinal(a, b));
    }

    /// <summary>The number of the OS disk (<see cref="OsDisk.Number"/>) the partition is on. Run-dependent (§4.4).</summary>
    public int DiskNumber { get; }

    public int PartitionNumber { get; }

    public Reading<ulong> SizeBytes { get; }

    /// <summary>
    /// The partition's access paths (volume path, drive root, mount folders), user profile rewritten. The
    /// platform documents no order, so they are sorted ordinally.
    /// </summary>
    public IReadOnlyList<string> AccessPaths { get; }
}

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
