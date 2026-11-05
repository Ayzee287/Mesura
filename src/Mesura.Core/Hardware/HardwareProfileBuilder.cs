using Mesura.Core.Checks;
using Mesura.Core.Facts;
using Mesura.Core.Raw;
using Mesura.Core.Windows;

namespace Mesura.Core.Hardware;

/// <summary>
/// What the diagnostic snapshot doesn't hold and the platform layer reads on its own: values that need a Windows call (the power capabilities, the
/// processor topology, the registry), as the setup layer's reader already reads them for the operations' requirements. Every part is optional.
/// </summary>
/// <param name="CpuVendorId">The processor's vendor string (<c>GenuineIntel</c>, <c>AuthenticAMD</c>).</param>
/// <param name="SystemDriveLetter">The drive letter Windows is installed on (<c>C</c>), to find the system disk.</param>
/// <param name="Adapters">The network adapters as the adapter reader classifies them; <c>null</c> when they couldn't be read.</param>
public sealed record HardwareExtras
{
    public int WindowsBuild { get; init; }

    public bool? PolicyEdition { get; init; }

    public bool? HasBattery { get; init; }

    public bool? ModernStandby { get; init; }

    public ulong? MemoryBytes { get; init; }

    public bool? HybridCpu { get; init; }

    public string? CpuName { get; init; }

    public string? CpuVendorId { get; init; }

    public string? SystemDriveLetter { get; init; }

    public IReadOnlyList<AdapterReading>? Adapters { get; init; }
}

/// <summary>
/// Builds the <see cref="HardwareProfile"/> from the normalised diagnostic facts plus a few platform reads (<see cref="HardwareExtras"/>). Pure: the
/// same facts always give the same profile, and a fact that is missing becomes an unknown value, never a default.
/// </summary>
public static class HardwareProfileBuilder
{
    public static HardwareProfile Build(NormalizedSnapshot facts, HardwareExtras extras)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(extras);

        var hasBattery = extras.HasBattery ?? Known(facts.Power.BatteryPresent);
        return new HardwareProfile(
            new WindowsPlatform(extras.WindowsBuild, Known(facts.Os.Edition), Known(facts.Os.Version), extras.PolicyEdition),
            FormOf(facts.Machine.ChassisType),
            new PowerPlatform(hasBattery, extras.ModernStandby, Known(facts.Power.OnAcPower)),
            CpuOf(facts, extras),
            extras.MemoryBytes,
            GpusOf(facts),
            DisksOf(facts, extras.SystemDriveLetter),
            NetworkOf(extras.Adapters));
    }

    /// <summary>The form from the declared chassis; an undeclared one stays unknown.</summary>
    public static DeviceForm FormOf(Fact<ChassisType> chassis) => Policies.FormFactorOf(chassis) switch
    {
        FormFactor.FullDesktop => DeviceForm.Desktop,
        FormFactor.Compact => DeviceForm.Compact,
        FormFactor.Portable => DeviceForm.Portable,
        _ => DeviceForm.Unknown,
    };

    private static CpuProfile? CpuOf(NormalizedSnapshot facts, HardwareExtras extras)
    {
        var sockets = facts.Processors.IsKnown ? facts.Processors.Value!.Where(p => Known(p.Populated) != false).ToList() : [];
        var first = sockets.FirstOrDefault();
        var name = Clean(extras.CpuName) ?? (first is null ? null : Clean(Known(first.Version)));
        if (name is null && first is null && extras.CpuVendorId is null)
        {
            return null;
        }

        var maker = MakerOf(extras.CpuVendorId, first is null ? null : Known(first.Manufacturer), name);
        return new CpuProfile(
            name,
            maker,
            Sum(sockets, p => p.CoresEnabled.IsKnown && p.CoresEnabled.Value > 0 ? p.CoresEnabled : p.CoreCount),
            Sum(sockets, p => p.ThreadCount),
            extras.HybridCpu);
    }

    /// <summary>Cores and threads of every populated socket, or <c>null</c> when any socket doesn't say.</summary>
    private static int? Sum(IReadOnlyList<ProcessorFacts> sockets, Func<ProcessorFacts, Fact<int>> pick)
    {
        if (sockets.Count == 0)
        {
            return null;
        }

        var total = 0;
        foreach (var socket in sockets)
        {
            var fact = pick(socket);
            if (!fact.IsKnown || fact.Value <= 0)
            {
                return null;
            }

            total += fact.Value;
        }

        return total;
    }

    private static CpuMaker MakerOf(string? vendorId, string? manufacturer, string? name)
    {
        var text = string.Join(' ', new[] { vendorId, manufacturer, name }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (text.Length == 0)
        {
            return CpuMaker.Unknown;
        }

        return text.Contains("GenuineIntel", StringComparison.OrdinalIgnoreCase) || text.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? CpuMaker.Intel
            : text.Contains("AuthenticAMD", StringComparison.OrdinalIgnoreCase) || text.Contains("AMD", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase) ? CpuMaker.Amd
            : CpuMaker.Other;
    }

    private static List<GpuProfile> GpusOf(NormalizedSnapshot facts)
    {
        var gpus = new List<GpuProfile>();
        if (!facts.Gpus.IsKnown)
        {
            return gpus;
        }

        foreach (var gpu in facts.Gpus.Value!)
        {
            if (gpu.IsHardware.IsKnown && !gpu.IsHardware.Value)
            {
                continue; // the Basic Render Driver and other software adapters
            }

            var id = gpu.PciIdentity.IsKnown ? gpu.PciIdentity.Value : null;
            var vendor = id?.VendorId ?? 0;
            gpus.Add(new GpuProfile(
                Clean(Known(gpu.Name)) ?? "",
                vendor switch
                {
                    SystemSummary.NvidiaVendorId => GpuMaker.Nvidia,
                    SystemSummary.AmdVendorId => GpuMaker.Amd,
                    0x8086 => GpuMaker.Intel,
                    0 => GpuMaker.Unknown,
                    _ => GpuMaker.Other,
                },
                vendor,
                id?.DeviceId ?? 0,
                Clean(Known(gpu.DriverVersion)),
                gpu.IsIntegrated.IsKnown ? gpu.IsIntegrated.Value : null));
        }

        return gpus;
    }

    private static List<DiskProfile> DisksOf(NormalizedSnapshot facts, string? systemDrive)
    {
        var disks = new List<DiskProfile>();
        if (!facts.Disks.IsKnown)
        {
            return disks;
        }

        // The disk that holds the system drive's volume: volume -> disk, joined by the snapshot's own ids.
        string? systemDiskId = null;
        if (systemDrive is { Length: > 0 } && facts.Volumes.IsKnown)
        {
            var letter = systemDrive.TrimEnd(':', '\\');
            var volume = facts.Volumes.Value!.FirstOrDefault(v => v.DriveLetter.IsKnown && string.Equals(v.DriveLetter.Value?.TrimEnd(':', '\\'), letter, StringComparison.OrdinalIgnoreCase));
            systemDiskId = volume is not null && volume.Disk.IsKnown ? volume.Disk.Value : null;
        }

        foreach (var disk in facts.Disks.Value!)
        {
            disks.Add(new DiskProfile(
                Clean(Known(disk.Model)),
                ClassOf(disk.BusType, disk.MediaType),
                disk.SizeBytes.IsKnown ? disk.SizeBytes.Value : null,
                systemDiskId is not null && disk.Id == systemDiskId,
                disk.BusType.IsKnown ? disk.BusType.Value : BusKind.Unknown));
        }

        return disks;
    }

    /// <summary>NVMe by its bus; a hard disk by its media; SATA and SAS solid state by their media; USB and cards as removable; anything not said stays unknown.</summary>
    public static StorageClass ClassOf(Fact<BusKind> bus, Fact<MediaKind> media)
    {
        var busKind = bus.IsKnown ? bus.Value : BusKind.Unknown;
        var mediaKind = media.IsKnown ? media.Value : MediaKind.Unspecified;
        if (busKind == BusKind.Nvme)
        {
            return StorageClass.Nvme;
        }

        if (busKind is BusKind.Usb or BusKind.Sd or BusKind.Mmc)
        {
            return StorageClass.Removable;
        }

        return mediaKind switch
        {
            MediaKind.Hdd => StorageClass.Hdd,
            MediaKind.Ssd or MediaKind.Scm => StorageClass.Ssd,
            _ => busKind is BusKind.Unknown or BusKind.Sata or BusKind.Ata or BusKind.Sas or BusKind.Scsi ? StorageClass.Unknown : StorageClass.Other,
        };
    }

    /// <summary>Physical Ethernet and Wi-Fi adapters only: virtual switches, VPN adapters, Bluetooth and the rest are not hardware to tune.</summary>
    private static List<NetworkProfile> NetworkOf(IReadOnlyList<AdapterReading>? adapters) =>
        [.. (adapters ?? []).Where(a => a.Physical && a.Kind is AdapterKind.Ethernet or AdapterKind.WiFi)
            .Select(a => new NetworkProfile(string.IsNullOrWhiteSpace(a.Description) ? a.Name : a.Description, a.Kind, string.IsNullOrWhiteSpace(a.Vendor) ? null : a.Vendor, a.Up))];

    private static string? Known(Fact<string> fact) => fact.IsKnown ? fact.Value : null;

    private static bool? Known(Fact<bool> fact) => fact.IsKnown ? fact.Value : null;

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
