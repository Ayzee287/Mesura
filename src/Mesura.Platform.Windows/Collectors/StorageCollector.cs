using System.Globalization;
using System.Management;
using Mesura.Core.Privacy;
using Mesura.Core.Raw;

namespace Mesura.Platform.Windows.Collectors;

/// <summary>
/// <c>storage</c>: physical disks, OS disks, partitions and volumes from the Storage Management WMI
/// provider (<c>root\Microsoft\Windows\Storage</c>), readable without admin (architecture §4.4, §7).
/// Only the listed properties are queried (§13). Never queried: serial numbers, unique IDs,
/// <c>AdapterSerialNumber</c> (which holds the disk serial on NVMe), <c>FriendlyName</c> and
/// <c>Description</c> (user-editable), disk paths (they carry PnP instance ids), disk GUIDs and MBR
/// signatures, and volume labels. SMART reliability counters need admin and are not read.
/// </summary>
internal static class StorageCollector
{
    public const string Source = @"wmi.root\Microsoft\Windows\Storage (MSFT_PhysicalDisk, MSFT_Disk, MSFT_Partition, MSFT_Volume)";

    private const string Namespace = @"\\.\root\Microsoft\Windows\Storage";

    /// <summary>The documented bus-type code of a file-backed virtual disk (VHD, VHDX).</summary>
    private const ulong FileBackedVirtualBusType = 15;

    /// <summary>The WMI instance's property values by name; <c>null</c> when WMI returned none.</summary>
    internal delegate object? Properties(string name);

    internal const string PhysicalDiskQuery = "SELECT DeviceId, PhysicalLocation, Model, MediaType, BusType, HealthStatus, OperationalStatus, Size FROM MSFT_PhysicalDisk";

    internal const string DiskQuery = "SELECT Number, BusType, Location FROM MSFT_Disk";

    internal const string PartitionQuery = "SELECT DiskNumber, PartitionNumber, Size, AccessPaths FROM MSFT_Partition";

    internal const string VolumeQuery = "SELECT Path, DriveLetter, FileSystem, Size, SizeRemaining FROM MSFT_Volume";

    public static StorageData Collect(UserProfileRedactor profile, TimeSpan timeout)
    {
        var scope = new ManagementScope(Namespace);
        scope.Options.Timeout = timeout;
        scope.Connect();

        return new StorageData(
            Query(scope, timeout, PhysicalDiskQuery, disk => MapPhysicalDisk(disk, profile)),
            Query(scope, timeout, DiskQuery, disk => MapDisk(disk, profile)),
            Query(scope, timeout, PartitionQuery, partition => MapPartition(partition, profile)),
            Query(scope, timeout, VolumeQuery, MapVolume));
    }

    internal static PhysicalDisk MapPhysicalDisk(Properties disk, UserProfileRedactor profile) => new(
        DeviceId: disk("DeviceId") as string ?? throw new SourceUnavailableException(MissingReason.ParseFailed, "An MSFT_PhysicalDisk instance has no DeviceId"),
        Location: LocationReading(disk, "PhysicalLocation", profile),
        Model: TextReading(disk, "Model"),
        MediaType: MapMediaType(Number(disk, "MediaType")),
        BusType: MapBusType(Number(disk, "BusType")),
        Health: MapHealth(Number(disk, "HealthStatus")),
        OperationalStatus: disk("OperationalStatus") is ushort[] codes
            ? Reading.Known<IReadOnlyList<int>>([.. codes.Select(code => (int)code)])
            : Reading.Missing<IReadOnlyList<int>>(MissingReason.NotPresent, "WMI returned no OperationalStatus"),
        SizeBytes: UnsignedReading(disk, "Size"));

    internal static OsDisk MapDisk(Properties disk, UserProfileRedactor profile) => new(
        Number: checked((int)(Number(disk, "Number") ?? throw new SourceUnavailableException(MissingReason.ParseFailed, "An MSFT_Disk instance has no Number"))),
        BusType: MapBusType(Number(disk, "BusType")),
        Location: LocationReading(disk, "Location", profile));

    internal static DiskPartition MapPartition(Properties partition, UserProfileRedactor profile) => new(
        diskNumber: checked((int)(Number(partition, "DiskNumber") ?? throw new SourceUnavailableException(MissingReason.ParseFailed, "An MSFT_Partition instance has no DiskNumber"))),
        partitionNumber: checked((int)(Number(partition, "PartitionNumber") ?? throw new SourceUnavailableException(MissingReason.ParseFailed, "An MSFT_Partition instance has no PartitionNumber"))),
        sizeBytes: UnsignedReading(partition, "Size"),
        accessPaths: partition("AccessPaths") is string[] paths ? [.. paths.Select(profile.Redact)] : []);

    internal static StorageVolume MapVolume(Properties volume) => new(
        Path: volume("Path") as string ?? throw new SourceUnavailableException(MissingReason.ParseFailed, "An MSFT_Volume instance has no Path"),
        DriveLetter: volume("DriveLetter") is char letter && letter != '\0'
            ? Reading.Known(letter.ToString())
            : Reading.Missing<string>(MissingReason.NotPresent, "The volume has no drive letter"),
        FileSystem: TextReading(volume, "FileSystem"),
        SizeBytes: UnsignedReading(volume, "Size"),
        FreeBytes: UnsignedReading(volume, "SizeRemaining"));

    internal static Reading<MediaKind> MapMediaType(ulong? code) => code switch
    {
        null => Reading.Missing<MediaKind>(MissingReason.NotPresent, "WMI returned no MediaType"),
        0 => Reading.Known(MediaKind.Unspecified, 0),
        3 => Reading.Known(MediaKind.Hdd, 3),
        4 => Reading.Known(MediaKind.Ssd, 4),
        5 => Reading.Known(MediaKind.Scm, 5),
        _ => Unrecognised<MediaKind>("MediaType", code.Value),
    };

    internal static Reading<BusKind> MapBusType(ulong? code)
    {
        if (code is null)
        {
            return Reading.Missing<BusKind>(MissingReason.NotPresent, "WMI returned no BusType");
        }

        BusKind? kind = code.Value switch
        {
            0 => BusKind.Unknown,
            1 => BusKind.Scsi,
            2 => BusKind.Atapi,
            3 => BusKind.Ata,
            4 => BusKind.Ieee1394,
            5 => BusKind.Ssa,
            6 => BusKind.FibreChannel,
            7 => BusKind.Usb,
            8 => BusKind.Raid,
            9 => BusKind.Iscsi,
            10 => BusKind.Sas,
            11 => BusKind.Sata,
            12 => BusKind.Sd,
            13 => BusKind.Mmc,
            15 => BusKind.FileBackedVirtual,
            16 => BusKind.StorageSpaces,
            17 => BusKind.Nvme,
            _ => null, // 14 and 18+ are reserved in the MSFT_PhysicalDisk and MSFT_Disk documentation
        };

        return kind is { } value ? Reading.Known(value, (long)code.Value) : Unrecognised<BusKind>("BusType", code.Value);
    }

    internal static Reading<HealthState> MapHealth(ulong? code) => code switch
    {
        null => Reading.Missing<HealthState>(MissingReason.NotPresent, "WMI returned no HealthStatus"),
        0 => Reading.Known(HealthState.Healthy, 0),
        1 => Reading.Known(HealthState.Warning, 1),
        2 => Reading.Known(HealthState.Unhealthy, 2),
        5 => Reading.Known(HealthState.Unknown, 5),
        _ => Unrecognised<HealthState>("HealthStatus", code.Value),
    };

    private static Reading<T> Unrecognised<T>(string property, ulong code) =>
        Reading.Missing<T>(
            MissingReason.ParseFailed,
            string.Create(CultureInfo.InvariantCulture, $"{property} {code} is not a value the MSFT_PhysicalDisk and MSFT_Disk documentation defines"),
            (long)code);

    private static List<T> Query<T>(ManagementScope scope, TimeSpan timeout, string wql, Func<Properties, T> map)
    {
        var options = new System.Management.EnumerationOptions { Timeout = timeout, ReturnImmediately = true, Rewindable = false };
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql), options);
        using var results = searcher.Get();

        var items = new List<T>();
        foreach (var instance in results)
        {
            using (instance)
            {
                items.Add(map(name => instance[name]));
            }
        }

        return items;
    }

    /// <summary>
    /// A disk's location, kept only when it describes where hardware is attached. A file-backed virtual disk
    /// (a mounted VHD or VHDX) can report its image file's path instead, despite Learn's "NULL for VHD", and a
    /// path holds user-chosen folder and file names, so a virtual disk's location, and any value that is a file
    /// path, is withheld (§13). What is kept still has the profile path rewritten, like all collected text.
    /// </summary>
    private static Reading<string> LocationReading(Properties instance, string property, UserProfileRedactor profile)
    {
        if (instance(property) is not string value)
        {
            return Reading.Missing<string>(MissingReason.NotPresent, $"WMI returned no {property}");
        }

        var isVirtual = Number(instance, "BusType") == FileBackedVirtualBusType;
        var isFilePath = (value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '\\' or '/')
            || value.StartsWith(@"\\", StringComparison.Ordinal);

        return isVirtual || isFilePath
            ? Reading.Missing<string>(MissingReason.Withheld, $"{property} of a file-backed virtual disk, or one that is a file path, is not kept: it names an image file")
            : Reading.Known(profile.Redact(value));
    }

    private static Reading<string> TextReading(Properties instance, string property) =>
        instance(property) is string value
            ? Reading.Known(value)
            : Reading.Missing<string>(MissingReason.NotPresent, $"WMI returned no {property}");

    private static ulong? Number(Properties instance, string property) => instance(property) switch
    {
        byte value => value,
        ushort value => value,
        uint value => value,
        ulong value => value,
        _ => null,
    };

    private static Reading<ulong> UnsignedReading(Properties instance, string property) =>
        Number(instance, property) is { } value
            ? Reading.Known(value)
            : Reading.Missing<ulong>(MissingReason.NotPresent, $"WMI returned no {property}");
}
