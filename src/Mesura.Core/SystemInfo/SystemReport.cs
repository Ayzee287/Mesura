namespace Mesura.Core.SystemInfo;

/// <summary>
/// What the System page shows (0.3): this PC's hardware and Windows configuration, as plain values. A value that couldn't be
/// read is <c>null</c> (or an empty list): the page says "unavailable" for it and never shows a made-up number. It holds no
/// serial numbers, MAC or IP addresses, product keys, user or computer names, so a report can be shared as it is.
/// </summary>
public sealed record SystemReport(
    DateTimeOffset GeneratedAt,
    SystemMachine Machine,
    SystemOs Os,
    SystemFirmware Firmware,
    IReadOnlyList<SystemProcessor> Processors,
    SystemMemory Memory,
    IReadOnlyList<SystemGpu> Gpus,
    IReadOnlyList<SystemDisk> Disks,
    IReadOnlyList<SystemVolume> Volumes,
    IReadOnlyList<SystemDisplay> Displays,
    SystemPower Power);

/// <summary>The manufacturer and model the firmware declares, and the kind of machine (laptop, desktop...).</summary>
public sealed record SystemMachine(string? Manufacturer, string? Model, string? Kind, string? BoardManufacturer, string? Board);

public sealed record SystemOs(string? Name, string? Edition, string? Version, int? Build, int? Revision, string? Architecture, DateTimeOffset? InstalledOn, TimeSpan? Uptime);

/// <summary>BIOS or UEFI: vendor, version, release date; the boot mode and Secure Boot state.</summary>
public sealed record SystemFirmware(string? Vendor, string? Version, string? ReleaseDate, string? BootMode, bool? SecureBoot);

public sealed record SystemProcessor(string? Name, string? Manufacturer, int? Cores, int? Threads, int? MaxMhz, string? Socket);

/// <param name="SlotCount">Memory slots the firmware declares, filled or not; <c>null</c> when it declares none.</param>
public sealed record SystemMemory(ulong? TotalBytes, ulong? AvailableBytes, IReadOnlyList<SystemMemoryModule> Modules, int? SlotCount = null)
{
    public ulong? UsedBytes => TotalBytes is { } total && AvailableBytes is { } free && free <= total ? total - free : null;
}

/// <param name="ConfiguredMts">The speed the module is configured to run at (SMBIOS "configured speed"), in MT/s.</param>
/// <param name="MaxMts">The module's maximum capable speed as the firmware declares it: usually the JEDEC speed, never the XMP or EXPO rating (D004).</param>
/// <param name="PartNumber">The part number as declared (trimmed); a model name, not a serial number.</param>
/// <param name="Bank">The bank or channel the slot sits in, as the firmware names it.</param>
public sealed record SystemMemoryModule(string? Slot, ulong? SizeBytes, string? Type, int? SpeedMts, string? Manufacturer, string? FormFactor,
    int? ConfiguredMts = null, int? MaxMts = null, string? PartNumber = null, string? Bank = null);

public sealed record SystemGpu(string? Name, string? Vendor, bool? Integrated, string? DriverVersion, ulong? MemoryBytes);

public sealed record SystemDisk(string? Model, string? Media, string? Bus, ulong? SizeBytes, string? Health);

public sealed record SystemVolume(string? Letter, string? FileSystem, ulong? SizeBytes, ulong? FreeBytes)
{
    public ulong? UsedBytes => SizeBytes is { } size && FreeBytes is { } free && free <= size ? size - free : null;
}

public sealed record SystemDisplay(string? Name, string? Connection, int? Width, int? Height, double? RefreshHz, double? MaxRefreshHz, string? Gpu);

public sealed record SystemPower(bool? HasBattery, bool? OnAc, string? PlanName, bool? ModernStandby);

/// <summary>
/// What the page needs that the diagnostic snapshot doesn't hold (Windows' own product name, memory in use, the BIOS version...),
/// read by the platform layer; every part is optional.
/// </summary>
public sealed record SystemExtras
{
    public string? OsProductName { get; init; }

    public string? OsDisplayVersion { get; init; }

    public DateTimeOffset? InstalledOn { get; init; }

    public TimeSpan? Uptime { get; init; }

    public ulong? MemoryTotalBytes { get; init; }

    public ulong? MemoryAvailableBytes { get; init; }

    public string? CpuName { get; init; }

    public string? PowerPlan { get; init; }

    public bool? ModernStandby { get; init; }

    public string? BiosVendor { get; init; }

    public string? BiosVersion { get; init; }

    public string? BiosDate { get; init; }

    public string? BootMode { get; init; }

    public bool? SecureBoot { get; init; }
}
