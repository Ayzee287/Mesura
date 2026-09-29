using System.Text.Json.Serialization;
using FpsUtility.Core.Raw.Serialization;

namespace FpsUtility.Core.Facts;

/// <summary>
/// A graphics adapter (raw <c>adapters</c>), joined to its PCI device. Its id is snapshot-scoped; two identical
/// cards can swap ids between scans, because only their run-dependent adapter ids tell them apart.
/// </summary>
/// <param name="Id">Snapshot-scoped, in the raw snapshot's order of adapters (PCI identity first).</param>
/// <param name="Name">The adapter's name as its driver reports it.</param>
/// <param name="IsIntegrated">The platform's own integrated-graphics flag; never inferred from a vendor or a name.</param>
/// <param name="IsHardware">False for a software adapter.</param>
/// <param name="DedicatedMemoryBytes">Memory dedicated to the adapter, in bytes; 0 is a real value (an integrated GPU).</param>
/// <param name="PciDevice">The <see cref="PciDeviceFacts.Id"/> of the adapter's PCI function.</param>
public sealed record GpuFacts(
    string Id,
    Fact<string> Name,
    Fact<PciFunctionId> PciIdentity,
    Fact<PciSubsystemId> PciSubsystem,
    Fact<bool> IsIntegrated,
    Fact<bool> IsHardware,
    Fact<string> DriverVersion,
    Fact<ulong> DedicatedMemoryBytes,
    Fact<string> PciDevice);

/// <summary>A PCI function's model identity: vendor, device and revision IDs. Model data, never a unit identifier.</summary>
public sealed record PciFunctionId(int VendorId, int DeviceId, int RevisionId);

/// <summary>A PCI function's subsystem (the board maker's) vendor and device IDs.</summary>
public sealed record PciSubsystemId(int VendorId, int DeviceId);

/// <summary>
/// A PCI display or storage function, or one whose class the platform didn't report (raw <c>pcie</c>). Link states
/// are the scan's: an idle reading. A link's speed is judged only from a reading under load (B5, §8), later.
/// </summary>
/// <param name="Id">Snapshot-scoped, in the raw snapshot's order of devices.</param>
/// <param name="Link">The device's own link.</param>
/// <param name="UpstreamLink">The link of the device's parent (usually the port it hangs off).</param>
public sealed record PciDeviceFacts(
    string Id,
    Fact<PciDeviceClass> Class,
    Fact<PciFunctionId> PciIdentity,
    Fact<PciSubsystemId> PciSubsystem,
    PcieLinkFacts Link,
    PcieLinkFacts UpstreamLink);

/// <summary>The PCI base classes the collector lists; <see cref="Other"/> is any other code, which the raw snapshot keeps.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<PciDeviceClass>))]
public enum PciDeviceClass
{
    MassStorage,
    Display,
    Other,
}

/// <summary>A PCI Express link's width in lanes and speed in GT/s, current and maximum, as the platform reported them at scan time.</summary>
public sealed record PcieLinkFacts(
    Fact<int> CurrentWidth,
    Fact<int> MaxWidth,
    Fact<decimal> CurrentSpeedGtps,
    Fact<decimal> MaxSpeedGtps);
