using System.Text.Json.Serialization;

namespace Mesura.Core.Raw;

/// <summary>
/// The <c>pcie</c> section: link state of every present PCI display and storage device and of its
/// parent port (architecture §4.4, §7). A present PCI device whose class Windows couldn't report is
/// listed too, with the reason, so a missing GPU is never silently absent. A scan reading is an idle reading; speed is judged only from
/// a capture-time sample, later (B5).
/// </summary>
public sealed record PcieData
{
    [JsonConstructor]
    public PcieData(IReadOnlyList<PciDevice> devices)
    {
        Devices = Canonical.Sort(devices, static (a, b) => Canonical.Ordinal(a.InstanceId, b.InstanceId));
    }

    public IReadOnlyList<PciDevice> Devices { get; }
}

/// <param name="InstanceId">Opaque device instance id.</param>
/// <param name="BaseClass">The PCI base class code: 1 mass storage, 3 display; missing if Windows didn't report it.</param>
/// <param name="PciIds">The PCI identity, decoded from the platform's hardware ID.</param>
/// <param name="Link">The device's own link.</param>
/// <param name="Parent">The device's parent (usually the port or bridge it hangs off) and that parent's link.</param>
public sealed record PciDevice(
    string InstanceId,
    Reading<int> BaseClass,
    Reading<PciIds> PciIds,
    PcieLink Link,
    Reading<PciParent> Parent);

/// <param name="InstanceId">Opaque device instance id of the parent.</param>
public sealed record PciParent(string InstanceId, PcieLink Link);

/// <summary>
/// One PCI Express link as the platform reports it. Widths are lane counts. Speeds are GT/s; the
/// native value is the link-speed code (1 = 2.5, 2 = 5.0, 3 = 8.0, 4 = 16.0, 5 = 32.0, 6 = 64.0).
/// </summary>
public sealed record PcieLink(
    Reading<int> CurrentWidth,
    Reading<int> MaxWidth,
    Reading<decimal> CurrentSpeedGtps,
    Reading<decimal> MaxSpeedGtps);
