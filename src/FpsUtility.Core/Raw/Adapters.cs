using System.Text.Json.Serialization;

namespace FpsUtility.Core.Raw;

/// <summary>The <c>adapters</c> section: every graphics adapter (architecture §4.4).</summary>
public sealed record AdapterData
{
    [JsonConstructor]
    public AdapterData(IReadOnlyList<GraphicsAdapter> adapters)
    {
        // Boot-stable keys first; the adapter LUID changes on reboot, so it only breaks ties.
        Adapters = Canonical.Sort(adapters, static (a, b) =>
        {
            var order = Canonical.Ordinal(IdentityKey(a), IdentityKey(b));
            order = order != 0 ? order : Canonical.Ordinal(a.Description.Value, b.Description.Value);
            return order != 0 ? order : Canonical.Ordinal(a.AdapterId, b.AdapterId);
        });
    }

    public IReadOnlyList<GraphicsAdapter> Adapters { get; }

    private static string? IdentityKey(GraphicsAdapter adapter) =>
        adapter.HardwareId.Value is { } id
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{id.VendorId:X4}{id.DeviceId:X4}{id.SubsystemCombined:X8}{id.RevisionId:X2}")
            : null;
}

/// <param name="AdapterId">Opaque; the same value as <c>display.outputs[].adapterId</c>. It is the adapter's LUID, which is valid only until reboot or a driver restart: a join key within one snapshot, run-dependent across scans.</param>
/// <param name="Description">The adapter's name as its driver reports it.</param>
/// <param name="PciIds">The PCI identity reported field by field, when the platform offers that.</param>
/// <param name="HardwareId">The PCI identity as a single combined record, as reported (the subsystem vendor and subsystem ID are one value here).</param>
/// <param name="IsIntegrated">The platform's own integrated-graphics flag; never inferred from a vendor or a name.</param>
/// <param name="IsHardware">False for a software adapter.</param>
/// <param name="DriverVersion">The driver version in its four-part form, e.g. <c>32.0.101.7088</c>.</param>
/// <param name="DedicatedMemoryBytes">Memory dedicated to the adapter, in bytes.</param>
public sealed record GraphicsAdapter(
    string AdapterId,
    Reading<string> Description,
    Reading<PciIds> PciIds,
    Reading<PciHardwareId> HardwareId,
    Reading<bool> IsIntegrated,
    Reading<bool> IsHardware,
    Reading<string> DriverVersion,
    Reading<ulong> DedicatedMemoryBytes);

/// <summary>A PCI function's identity, each ID separately. Shared by <c>adapters</c> and <c>pcie</c>, which the normaliser joins on it (§5).</summary>
public sealed record PciIds(int VendorId, int DeviceId, int SubsystemVendorId, int SubsystemId, int RevisionId);

/// <param name="SubsystemCombined">The 32-bit subsystem value exactly as reported, not split.</param>
public sealed record PciHardwareId(int VendorId, int DeviceId, uint SubsystemCombined, int RevisionId);
