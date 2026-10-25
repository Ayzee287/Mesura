using Mesura.Core.Privacy;
using Mesura.Core.Raw;
using Mesura.Platform.Windows.Collectors;

namespace Mesura.Platform.Tests;

/// <summary>How Storage WMI properties become the <c>storage</c> section (architecture §4.4), fed with fake instances.</summary>
public sealed class StorageMappingTests
{
    private static readonly Dictionary<string, object?> Nvme = new()
    {
        ["DeviceId"] = "0",
        ["PhysicalLocation"] = "PCI Slot 8 : Bus 1 : Device 0 : Function 0 : Adapter 0",
        ["Model"] = "SAMSUNG MZVLQ512HALU-00000",
        ["FriendlyName"] = "Ann's games disk", // what the user renamed it to; never collected
        ["MediaType"] = (ushort)4,
        ["BusType"] = (ushort)17,
        ["HealthStatus"] = (ushort)0,
        ["OperationalStatus"] = new ushort[] { 2 },
        ["Size"] = 512_110_190_592UL,
    };

    private static readonly UserProfileRedactor Profile = new(@"C:\Users\Ann");

    [Fact]
    public void Model_is_the_hardware_model_never_the_editable_friendly_name()
    {
        var disk = StorageCollector.MapPhysicalDisk(name => Nvme.GetValueOrDefault(name), Profile);

        Assert.Equal("SAMSUNG MZVLQ512HALU-00000", disk.Model.Value);
        Assert.DoesNotContain("FriendlyName", StorageCollector.PhysicalDiskQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_disk_without_a_model_says_so_rather_than_falling_back_to_another_name()
    {
        var withoutModel = new Dictionary<string, object?>(Nvme) { ["Model"] = null };

        var disk = StorageCollector.MapPhysicalDisk(name => withoutModel.GetValueOrDefault(name), Profile);

        Assert.False(disk.Model.IsKnown);
        Assert.Equal(MissingReason.NotPresent, disk.Model.Missing);
    }

    [Fact]
    public void The_location_key_comes_from_the_physical_location_and_the_os_disk_location()
    {
        var physical = StorageCollector.MapPhysicalDisk(name => Nvme.GetValueOrDefault(name), Profile);
        var os = StorageCollector.MapDisk(name => name switch
        {
            "Number" => 0U,
            "BusType" => (ushort)17,
            "Location" => "PCI Slot 8 : Bus 1 : Device 0 : Function 0 : Adapter 0",
            _ => null,
        }, Profile);

        Assert.Equal("PCI Slot 8 : Bus 1 : Device 0 : Function 0 : Adapter 0", physical.Location.Value);
        Assert.Equal(physical.Location.Value, os.Location.Value);
        Assert.Equal(0, os.Number);
        Assert.Equal(BusKind.Nvme, os.BusType.Value);
    }

    [Fact]
    public void A_virtual_disk_without_a_location_is_missing_it_not_empty()
    {
        var os = StorageCollector.MapDisk(name => name switch
        {
            "Number" => 3U,
            "BusType" => (ushort)15,
            _ => null, // Learn: Location is NULL for Hyper-V and VHD disks
        }, Profile);

        Assert.Equal(BusKind.FileBackedVirtual, os.BusType.Value);
        Assert.Equal(MissingReason.NotPresent, os.Location.Missing);
    }

    /// <summary>A mounted VHD or VHDX can report its image file's path as its location, which names the user's files.</summary>
    [Theory]
    [InlineData(15, @"C:\Users\Ann\Disks\games.vhdx")] // a file-backed virtual disk
    [InlineData(15, "Integrated : Adapter 0 : Port 0")] // a virtual disk, whatever the text says
    [InlineData(11, @"D:\Images\dev.vhdx")] // any value that is a drive path
    [InlineData(11, @"\\server\share\disk.vhdx")] // any value that is a UNC path
    public void A_location_that_can_name_a_file_is_withheld(int busType, string location)
    {
        var os = StorageCollector.MapDisk(name => name switch
        {
            "Number" => 4U,
            "BusType" => (ushort)busType,
            "Location" => location,
            _ => null,
        }, Profile);

        Assert.Equal(MissingReason.Withheld, os.Location.Missing);
        Assert.DoesNotContain("Ann", os.Location.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_kept_location_still_has_the_profile_path_rewritten()
    {
        var physical = StorageCollector.MapPhysicalDisk(
            name => name == "PhysicalLocation" ? @"Enclosure C:\Users\Ann slot 2" : Nvme.GetValueOrDefault(name),
            Profile);

        Assert.Equal(@"Enclosure %USERPROFILE% slot 2", physical.Location.Value);
    }

    /// <summary>§13: these properties identify a unit or hold user text, so no query may read them.</summary>
    [Theory]
    [InlineData("SerialNumber")]
    [InlineData("AdapterSerialNumber")] // holds the disk serial on NVMe (seen on PC-000)
    [InlineData("UniqueId")]
    [InlineData("FriendlyName")]
    [InlineData("Description")]
    [InlineData("Guid")]
    [InlineData("Signature")]
    [InlineData("FileSystemLabel")]
    [InlineData("ObjectId")]
    public void No_storage_query_reads_an_identifier_or_user_text(string property)
    {
        string[] queries = [StorageCollector.PhysicalDiskQuery, StorageCollector.DiskQuery, StorageCollector.PartitionQuery, StorageCollector.VolumeQuery];

        Assert.All(queries, query => Assert.DoesNotContain(property, SelectedProperties(query)));
    }

    [Fact]
    public void Partition_access_paths_are_sorted_and_the_profile_path_rewritten()
    {
        var partition = StorageCollector.MapPartition(
            name => name switch
            {
                "DiskNumber" => 1U,
                "PartitionNumber" => 2U,
                "Size" => 100UL,
                "AccessPaths" => new[] { @"\\?\Volume{0f0e0d0c-0000-0000-0000-000000000001}\", @"C:\Users\Ann\Mounted\", @"D:\" },
                _ => null,
            },
            new UserProfileRedactor(@"C:\Users\Ann"));

        Assert.Equal([@"%USERPROFILE%\Mounted\", @"D:\", @"\\?\Volume{0f0e0d0c-0000-0000-0000-000000000001}\"], partition.AccessPaths);
    }

    private static string[] SelectedProperties(string query) =>
        [.. query["SELECT ".Length..query.IndexOf(" FROM ", StringComparison.Ordinal)].Split(", ")];
}
