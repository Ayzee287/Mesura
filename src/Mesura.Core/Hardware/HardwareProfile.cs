using Mesura.Core.Raw;
using Mesura.Core.Windows;

namespace Mesura.Core.Hardware;

/// <summary>
/// The kind of case the PC is built into, from the chassis type its firmware declares (SMBIOS). A battery alone never makes a laptop (a
/// desktop on a UPS has one) and a missing battery never makes a desktop (a laptop with its battery removed): an undeclared or
/// unrecognised chassis is <see cref="Unknown"/>, and the planner treats that as "couldn't tell", not as a desktop.
/// </summary>
public enum DeviceForm
{
    Unknown,

    /// <summary>A desktop case (tower, mini tower, low-profile, pizza box).</summary>
    Desktop,

    /// <summary>A small or all-in-one case: a mini PC, an all-in-one, a sealed or space-saving case.</summary>
    Compact,

    /// <summary>A laptop-class device: laptop, notebook, tablet, convertible, detachable, hand-held.</summary>
    Portable,
}

/// <summary>
/// How much room the case gives to heat, inferred from <see cref="DeviceForm"/> only. It is not a measurement: Windows offers no reading of a
/// PC's thermal headroom, so Mesura says "the case limits cooling" and never "this PC runs at N degrees".
/// </summary>
public enum CoolingClass
{
    Unknown,

    /// <summary>A desktop case: room for airflow and larger coolers (the actual cooler is not known).</summary>
    Open,

    /// <summary>A laptop or compact case: cooling is bounded by the chassis.</summary>
    Limited,
}

public enum CpuMaker
{
    Unknown,
    Intel,
    Amd,
    Other,
}

public enum GpuMaker
{
    Unknown,
    Nvidia,
    Amd,
    Intel,
    Other,
}

/// <summary>What a drive is, from the bus and the media type Windows reports (an unspecified media type on a SATA bus stays unknown).</summary>
public enum StorageClass
{
    Unknown,
    Nvme,

    /// <summary>A SATA, SAS or other solid-state drive.</summary>
    Ssd,
    Hdd,

    /// <summary>A USB or card drive.</summary>
    Removable,
    Other,
}

/// <summary>The Windows the profile was read on: what decides which settings exist and which policies are honoured.</summary>
/// <param name="PolicyEdition">Pro and above (machine policies are honoured); <c>null</c> when the edition couldn't be read.</param>
public sealed record WindowsPlatform(int Build, string? Edition, string? Version, bool? PolicyEdition)
{
    public bool IsWindows11 => Build >= 22000;
}

/// <summary>Power-related facts: whether a system battery exists (a UPS does not count), Modern Standby, and whether the PC was on mains power when read.</summary>
public sealed record PowerPlatform(bool? HasBattery, bool? ModernStandby, bool? OnMains);

/// <param name="Hybrid">Cores of more than one efficiency class (Intel hybrid, AMD heterogeneous); <c>null</c> when it couldn't be read.</param>
public sealed record CpuProfile(string? Name, CpuMaker Maker, int? Cores, int? Threads, bool? Hybrid);

/// <param name="VendorId">The PCI vendor id (0x10DE NVIDIA, 0x1002 AMD, 0x8086 Intel).</param>
/// <param name="Integrated">The platform's own integrated-graphics flag; never inferred from a vendor or a name.</param>
public sealed record GpuProfile(string Name, GpuMaker Maker, int VendorId, int DeviceId, string? DriverVersion, bool? Integrated)
{
    public bool IsDiscrete => Integrated == false;
}

/// <param name="HoldsWindows">Whether the system drive's volume lives on this disk; <c>false</c> also when that could not be established.</param>
/// <param name="Bus">The bus the platform reports (SATA, NVMe, USB...), for settings that apply to one kind of link.</param>
public sealed record DiskProfile(string? Model, StorageClass Class, ulong? SizeBytes, bool HoldsWindows, BusKind Bus = BusKind.Unknown);

/// <summary>A physical network adapter (Ethernet or Wi-Fi): virtual, VPN and other adapters are not part of the hardware.</summary>
public sealed record NetworkProfile(string Name, AdapterKind Kind, string? Maker, bool Up);

/// <summary>
/// What Mesura knows about this PC's hardware and Windows, in one place, for deciding which optimisations apply to it (0.6). Every value that
/// couldn't be read is <c>null</c>, <c>Unknown</c> or absent, never guessed: a planner treats an unread fact as "can't tell" and leaves what
/// depends on it alone. Holds model names and PCI ids (hardware classes), never serial numbers, addresses, user or computer names.
/// </summary>
public sealed record HardwareProfile(
    WindowsPlatform Windows,
    DeviceForm Form,
    PowerPlatform Power,
    CpuProfile? Cpu,
    ulong? MemoryBytes,
    IReadOnlyList<GpuProfile> Gpus,
    IReadOnlyList<DiskProfile> Disks,
    IReadOnlyList<NetworkProfile> Network)
{
    public bool IsPortable => Form == DeviceForm.Portable;

    /// <summary>The cooling the case allows, inferred from the form only.</summary>
    public CoolingClass Cooling => Form switch
    {
        DeviceForm.Desktop => CoolingClass.Open,
        DeviceForm.Compact or DeviceForm.Portable => CoolingClass.Limited,
        _ => CoolingClass.Unknown,
    };

    public bool HasNvidia => Gpus.Any(g => g.Maker == GpuMaker.Nvidia);

    public bool HasAmdGpu => Gpus.Any(g => g.Maker == GpuMaker.Amd);

    public bool HasDiscreteGpu => Gpus.Any(g => g.IsDiscrete);

    /// <summary>The drive Windows is installed on, when it could be established.</summary>
    public DiskProfile? SystemDisk => Disks.FirstOrDefault(d => d.HoldsWindows);

    public bool HasWifi => Network.Any(n => n.Kind == AdapterKind.WiFi);

    public bool HasEthernet => Network.Any(n => n.Kind == AdapterKind.Ethernet);

    /// <summary>Installed memory in gigabytes, rounded to the nearest (Windows reports what it can use, a little under what is installed); <c>null</c> when unread.</summary>
    public int? MemoryGb => MemoryBytes is { } bytes ? (int)Math.Round(bytes / (1024.0 * 1024 * 1024), MidpointRounding.AwayFromZero) : null;
}
