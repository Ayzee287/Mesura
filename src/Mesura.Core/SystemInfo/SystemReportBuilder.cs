using System.Globalization;
using Mesura.Core.Facts;
using Mesura.Core.Raw;

namespace Mesura.Core.SystemInfo;

/// <summary>
/// Builds the <see cref="SystemReport"/> from the normalised facts (the diagnostic scan: what the firmware declares, the
/// adapters, the displays, the storage) and the few extra readings the platform layer adds. Only known facts become values; a
/// fact with a gap becomes <c>null</c>, which the page shows as unavailable.
/// </summary>
public static class SystemReportBuilder
{
    public static SystemReport Build(NormalizedSnapshot facts, SystemExtras extras, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(extras);

        var processors = Known(facts.Processors).Where(cpu => cpu.Populated.Value).Select(cpu => new SystemProcessor(
            Clean(cpu.Version.Value) ?? extras.CpuName,
            Clean(cpu.Manufacturer.Value),
            cpu.CoresEnabled.Value is > 0 ? cpu.CoresEnabled.Value : cpu.CoreCount.Value is > 0 ? cpu.CoreCount.Value : null,
            cpu.ThreadCount.Value is > 0 ? cpu.ThreadCount.Value : null,
            cpu.MaxSpeedMhz.Value is > 0 ? cpu.MaxSpeedMhz.Value : null,
            Clean(cpu.Socket.Value))).ToList();
        if (processors.Count == 0 && extras.CpuName is { Length: > 0 })
        {
            processors.Add(new SystemProcessor(extras.CpuName, null, null, Environment.ProcessorCount, null, null));
        }

        var modules = Known(facts.MemoryDevices).Where(device => device.Populated.Value && device.SizeBytes.Value > 0).Select(device => new SystemMemoryModule(
            Clean(device.Locator.Value),
            device.SizeBytes.IsKnown ? device.SizeBytes.Value : null,
            device.Type.IsKnown ? MemoryTypeName(device.Type.Value) : null,
            device.ConfiguredSpeedMts.Value is > 0 ? device.ConfiguredSpeedMts.Value : device.MaxSpeedMts.Value is > 0 ? device.MaxSpeedMts.Value : null,
            Clean(device.Manufacturer.Value),
            device.FormFactor.IsKnown ? FormFactorName(device.FormFactor.Value) : null)).ToList();
        var installed = modules.Count > 0 && modules.All(m => m.SizeBytes is not null) ? modules.Sum(m => (long)m.SizeBytes!.Value) : 0;
        var total = extras.MemoryTotalBytes ?? (installed > 0 ? (ulong)installed : null);

        var gpus = Known(facts.Gpus).Where(gpu => gpu.IsHardware.Value is not false).Select(gpu => new SystemGpu(
            Clean(gpu.Name.Value),
            gpu.PciIdentity.Value is { } id ? VendorName(id.VendorId) : null,
            gpu.IsIntegrated.IsKnown ? gpu.IsIntegrated.Value : null,
            Clean(gpu.DriverVersion.Value),
            gpu.DedicatedMemoryBytes.Value is > 0 ? gpu.DedicatedMemoryBytes.Value : null)).ToList();

        var disks = Known(facts.Disks).Select(disk => new SystemDisk(
            Clean(disk.Model.Value),
            disk.MediaType.IsKnown && disk.MediaType.Value != MediaKind.Unspecified ? MediaName(disk.MediaType.Value) : null,
            disk.BusType.IsKnown && disk.BusType.Value != BusKind.Unknown ? BusName(disk.BusType.Value) : null,
            disk.SizeBytes.Value is > 0 ? disk.SizeBytes.Value : null,
            disk.Health.IsKnown && disk.Health.Value != HealthState.Unknown ? disk.Health.Value.ToString() : null)).ToList();

        var volumes = Known(facts.Volumes).Where(volume => volume.DriveLetter.IsKnown).Select(volume => new SystemVolume(
            Clean(volume.DriveLetter.Value),
            Clean(volume.FileSystem.Value),
            volume.SizeBytes.Value is > 0 ? volume.SizeBytes.Value : null,
            volume.FreeBytes.IsKnown ? volume.FreeBytes.Value : null)).OrderBy(v => v.Letter, StringComparer.OrdinalIgnoreCase).ToList();

        var displays = Known(facts.Displays).Select(display =>
        {
            var current = display.SignalRefresh.Value is { } rate ? Math.Round((double)rate.Numerator / rate.Denominator, 2) : (double?)null;
            var fastest = display.OfferedModes.Value?.Where(mode => !mode.Interlaced).Select(mode => (double)mode.RefreshHz).DefaultIfEmpty(0).Max();
            return new SystemDisplay(
                Clean(display.MonitorName.Value),
                display.Connection.IsKnown ? ConnectionName(display.Connection.Value) : null,
                display.DesktopResolution.Value?.Width,
                display.DesktopResolution.Value?.Height,
                current,
                fastest is > 0 ? fastest : null,
                Clean(display.Gpu.Value));
        }).ToList();

        var os = facts.Os;
        var name = extras.OsProductName ?? (os.Build.Value >= 22000 ? "Windows 11" : os.Build.IsKnown ? "Windows 10" : null);
        return new SystemReport(
            now,
            new SystemMachine(
                Clean(facts.Machine.SystemManufacturer.Value),
                Clean(facts.Machine.SystemProduct.Value),
                facts.Machine.ChassisType.IsKnown ? ChassisName(facts.Machine.ChassisType.Value) : null,
                Clean(facts.Machine.BoardManufacturer.Value),
                Clean(facts.Machine.BoardProduct.Value)),
            new SystemOs(
                name,
                Clean(os.Edition.Value),
                extras.OsDisplayVersion ?? Clean(os.Version.Value),
                os.Build.IsKnown ? os.Build.Value : null,
                os.UpdateRevision.IsKnown ? os.UpdateRevision.Value : null,
                os.Architecture.IsKnown ? os.Architecture.Value.ToString().ToUpperInvariant() : null,
                extras.InstalledOn,
                extras.Uptime),
            new SystemFirmware(extras.BiosVendor, extras.BiosVersion, extras.BiosDate, extras.BootMode, extras.SecureBoot),
            processors,
            new SystemMemory(total, extras.MemoryAvailableBytes, modules),
            gpus,
            disks,
            volumes,
            displays,
            new SystemPower(
                facts.Power.BatteryPresent.IsKnown ? facts.Power.BatteryPresent.Value : null,
                facts.Power.OnAcPower.IsKnown ? facts.Power.OnAcPower.Value : null,
                extras.PowerPlan,
                extras.ModernStandby));
    }

    private static IReadOnlyList<T> Known<T>(Fact<IReadOnlyList<T>> fact) => fact.IsKnown && fact.Value is { } list ? list : [];

    /// <summary>Firmware strings are padded and sometimes carry "To be filled by O.E.M."; those are not values.</summary>
    internal static string? Clean(string? text)
    {
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        string[] placeholders = ["to be filled by o.e.m.", "default string", "system product name", "system manufacturer", "not specified", "unknown", "none", "n/a", "o.e.m."];
        return placeholders.Contains(value, StringComparer.OrdinalIgnoreCase) ? null : value;
    }

    private static string VendorName(int vendorId) => vendorId switch
    {
        0x10DE => "NVIDIA",
        0x1002 or 0x1022 => "AMD",
        0x8086 => "Intel",
        0x1414 => "Microsoft",
        _ => "0x" + vendorId.ToString("X4", CultureInfo.InvariantCulture),
    };

    private static string MediaName(MediaKind kind) => kind switch
    {
        MediaKind.Hdd => "HDD",
        MediaKind.Ssd => "SSD",
        MediaKind.Scm => "SCM",
        _ => kind.ToString(),
    };

    private static string BusName(BusKind kind) => kind switch
    {
        BusKind.Nvme => "NVMe",
        BusKind.Sata => "SATA",
        BusKind.Usb => "USB",
        BusKind.Sas => "SAS",
        BusKind.Scsi => "SCSI",
        BusKind.Ata => "ATA",
        BusKind.Raid => "RAID",
        BusKind.Sd => "SD",
        BusKind.Mmc => "MMC",
        BusKind.StorageSpaces => "Storage Spaces",
        BusKind.FileBackedVirtual => "Virtual",
        _ => kind.ToString(),
    };

    private static string MemoryTypeName(MemoryType type) => type switch
    {
        MemoryType.Ddr => "DDR",
        MemoryType.Ddr2 => "DDR2",
        MemoryType.Ddr3 => "DDR3",
        MemoryType.Ddr4 => "DDR4",
        MemoryType.Lpddr => "LPDDR",
        MemoryType.Lpddr2 => "LPDDR2",
        MemoryType.Lpddr3 => "LPDDR3",
        MemoryType.Lpddr4 => "LPDDR4",
        MemoryType.Unknown or MemoryType.Other => "",
        _ => type.ToString().ToUpperInvariant(),
    };

    private static string FormFactorName(MemoryFormFactor form) => form switch
    {
        MemoryFormFactor.Dimm => "DIMM",
        MemoryFormFactor.Sodimm => "SO-DIMM",
        MemoryFormFactor.RowOfChips => "Soldered",
        MemoryFormFactor.Camm => "CAMM",
        _ => "",
    };

    private static string ConnectionName(ConnectionKind kind) => kind switch
    {
        ConnectionKind.Vga => "VGA",
        ConnectionKind.Dvi => "DVI",
        ConnectionKind.Hdmi => "HDMI",
        ConnectionKind.DisplayPort => "DisplayPort",
        ConnectionKind.DisplayPortEmbedded or ConnectionKind.Lvds => "Built-in panel",
        ConnectionKind.Internal => "Built-in panel",
        ConnectionKind.Other => "",
        _ => kind.ToString(),
    };

    private static string ChassisName(ChassisType kind) => kind switch
    {
        ChassisType.Desktop or ChassisType.LowProfileDesktop or ChassisType.MiniTower or ChassisType.Tower or ChassisType.SpaceSaving => "Desktop",
        ChassisType.Laptop or ChassisType.Notebook or ChassisType.SubNotebook or ChassisType.Portable => "Laptop",
        ChassisType.AllInOne => "All-in-one",
        ChassisType.MiniPc or ChassisType.StickPc => "Mini PC",
        ChassisType.Tablet or ChassisType.Convertible or ChassisType.Detachable => "Tablet or 2-in-1",
        ChassisType.HandHeld => "Handheld",
        _ => "",
    };
}
