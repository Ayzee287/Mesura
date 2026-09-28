using System.Globalization;
using System.Management;
using FpsUtility.Core.Privacy;
using FpsUtility.Core.Raw;

namespace FpsUtility.Platform.Windows.Collectors;

/// <summary>
/// <c>storage</c>: disks, partitions and volumes from the Storage Management WMI provider
/// (<c>root\Microsoft\Windows\Storage</c>), readable without admin (architecture §4.4, §7). Only the
/// listed properties are queried: no serial number, no unique ID, no volume label (§13). SMART
/// reliability counters need admin and are not read.
/// </summary>
internal static class StorageCollector
{
    public const string Source = @"wmi.root\Microsoft\Windows\Storage (MSFT_PhysicalDisk, MSFT_Partition, MSFT_Volume)";

    private const string Namespace = @"\\.\root\Microsoft\Windows\Storage";

    public static StorageData Collect(UserProfileRedactor profile, TimeSpan timeout)
    {
        var scope = new ManagementScope(Namespace);
        scope.Options.Timeout = timeout;
        scope.Connect();

        var physicalDisks = Query(scope, timeout, "SELECT DeviceId, FriendlyName, MediaType, BusType, HealthStatus, OperationalStatus, Size FROM MSFT_PhysicalDisk", disk => new PhysicalDisk(
            DeviceId: Text(disk, "DeviceId") ?? throw new SourceUnavailableException(MissingReason.ParseFailed, "An MSFT_PhysicalDisk instance has no DeviceId"),
            Model: TextReading(disk, "FriendlyName"),
            MediaType: MapMediaType(Number(disk, "MediaType")),
            BusType: MapBusType(Number(disk, "BusType")),
            Health: MapHealth(Number(disk, "HealthStatus")),
            OperationalStatus: disk["OperationalStatus"] is ushort[] codes
                ? Reading.Known<IReadOnlyList<int>>([.. codes.Select(code => (int)code)])
                : Reading.Missing<IReadOnlyList<int>>(MissingReason.NotPresent, "WMI returned no OperationalStatus"),
            SizeBytes: UnsignedReading(disk, "Size")));

        var partitions = Query(scope, timeout, "SELECT DiskNumber, PartitionNumber, Size, AccessPaths FROM MSFT_Partition", partition => new DiskPartition(
            DiskNumber: checked((int)(Number(partition, "DiskNumber") ?? throw new SourceUnavailableException(MissingReason.ParseFailed, "An MSFT_Partition instance has no DiskNumber"))),
            PartitionNumber: checked((int)(Number(partition, "PartitionNumber") ?? throw new SourceUnavailableException(MissingReason.ParseFailed, "An MSFT_Partition instance has no PartitionNumber"))),
            SizeBytes: UnsignedReading(partition, "Size"),
            AccessPaths: partition["AccessPaths"] is string[] paths ? [.. paths.Select(profile.Redact)] : []));

        var volumes = Query(scope, timeout, "SELECT Path, DriveLetter, FileSystem, Size, SizeRemaining FROM MSFT_Volume", volume => new StorageVolume(
            Path: Text(volume, "Path") ?? throw new SourceUnavailableException(MissingReason.ParseFailed, "An MSFT_Volume instance has no Path"),
            DriveLetter: volume["DriveLetter"] is char letter && letter != '\0'
                ? Reading.Known(letter.ToString())
                : Reading.Missing<string>(MissingReason.NotPresent, "The volume has no drive letter"),
            FileSystem: TextReading(volume, "FileSystem"),
            SizeBytes: UnsignedReading(volume, "Size"),
            FreeBytes: UnsignedReading(volume, "SizeRemaining")));

        return new StorageData(physicalDisks, partitions, volumes);
    }

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
            _ => null, // 14 and 18+ are reserved in the MSFT_PhysicalDisk documentation
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
            string.Create(CultureInfo.InvariantCulture, $"{property} {code} is not a value the MSFT_PhysicalDisk documentation defines"),
            (long)code);

    private static List<T> Query<T>(ManagementScope scope, TimeSpan timeout, string wql, Func<ManagementBaseObject, T> map)
    {
        var options = new System.Management.EnumerationOptions { Timeout = timeout, ReturnImmediately = true, Rewindable = false };
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql), options);
        using var results = searcher.Get();

        var items = new List<T>();
        foreach (var instance in results)
        {
            using (instance)
            {
                items.Add(map(instance));
            }
        }

        return items;
    }

    private static string? Text(ManagementBaseObject instance, string property) => instance[property] as string;

    private static Reading<string> TextReading(ManagementBaseObject instance, string property) =>
        Text(instance, property) is { } value
            ? Reading.Known(value)
            : Reading.Missing<string>(MissingReason.NotPresent, $"WMI returned no {property}");

    private static ulong? Number(ManagementBaseObject instance, string property) => instance[property] switch
    {
        byte value => value,
        ushort value => value,
        uint value => value,
        ulong value => value,
        _ => null,
    };

    private static Reading<ulong> UnsignedReading(ManagementBaseObject instance, string property) =>
        Number(instance, property) is { } value
            ? Reading.Known(value)
            : Reading.Missing<ulong>(MissingReason.NotPresent, $"WMI returned no {property}");
}
