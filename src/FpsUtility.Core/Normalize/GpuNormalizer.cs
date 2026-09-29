using System.Globalization;
using FpsUtility.Core.Facts;
using FpsUtility.Core.Raw;

namespace FpsUtility.Core.Normalize;

/// <summary>
/// PCI devices and graphics adapters, and the adapter ↔ PCI device relationship (architecture §5, "Joins happen in
/// the normaliser"). The relationship is claimed only when exactly one PCI device carries the adapter's identity.
/// </summary>
internal static class GpuNormalizer
{
    private const string PcieData = "/sources/pcie/data/devices";
    private const string AdapterData = "/sources/adapters/data/adapters";

    /// <summary>Widths PCI Express defines, in lanes.</summary>
    private static readonly HashSet<int> LinkWidths = [1, 2, 4, 8, 12, 16, 32];

    public static Fact<IReadOnlyList<PciDeviceFacts>> PciDevices(SourceEntry<PcieData> source)
    {
        if (!Evidence.TryData(source, "pcie", out var pcie, out var unavailable))
        {
            return unavailable.Fact<IReadOnlyList<PciDeviceFacts>>();
        }

        var devices = pcie.Devices.Select((device, index) =>
        {
            var at = $"{PcieData}/{index}";
            return new PciDeviceFacts(
                Id: PciId(index),
                Class: Evidence.Map(Evidence.Observed(device.BaseClass, Basis.Authoritative, $"{at}/baseClass"), static code => code switch
                {
                    1 => PciDeviceClass.MassStorage,
                    3 => PciDeviceClass.Display,
                    _ => PciDeviceClass.Other,
                }),
                PciIdentity: Evidence.Map(Evidence.Observed(device.PciIds, Basis.Authoritative, $"{at}/pciIds"), Function),
                PciSubsystem: Evidence.Map(Evidence.Observed(device.PciIds, Basis.Authoritative, $"{at}/pciIds"), Subsystem),
                Link: Link(device.Link, $"{at}/link"),
                UpstreamLink: device.Parent.IsKnown
                    ? Link(device.Parent.Value!.Link, $"{at}/parent/value/link")
                    : UnknownLink(Evidence.Observed(device.Parent, Basis.Authoritative, $"{at}/parent")));
        });

        return Fact.Observed<IReadOnlyList<PciDeviceFacts>>([.. devices], Basis.Authoritative, PcieData);
    }

    public static Fact<IReadOnlyList<GpuFacts>> Gpus(SourceEntry<AdapterData> adapters, SourceEntry<PcieData> pcie)
    {
        if (!Evidence.TryData(adapters, "adapters", out var data, out var unavailable))
        {
            return unavailable.Fact<IReadOnlyList<GpuFacts>>();
        }

        var identities = data.Adapters.Select((adapter, index) => Identity(adapter, $"{AdapterData}/{index}")).ToList();
        var subsystems = data.Adapters.Select((adapter, index) => SubsystemOf(adapter, $"{AdapterData}/{index}")).ToList();
        var matches = identities.Select((identity, index) => PciDeviceOf(identity, subsystems[index], pcie)).ToList();

        // A PCI device two adapters both match belongs to neither for certain.
        var claimed = matches.Where(match => match.IsKnown).GroupBy(match => match.Value!, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);

        var gpus = data.Adapters.Select((adapter, index) =>
        {
            var at = $"{AdapterData}/{index}";
            var match = matches[index];
            if (match.IsKnown && claimed.Contains(match.Value!))
            {
                match = Fact.Missing<string>(FactGap.Ambiguous, "Another adapter matches the same PCI device, so neither is claimed", match.From);
            }

            return new GpuFacts(
                Id: GpuId(index),
                Name: Evidence.Observed(adapter.Description, Basis.Authoritative, $"{at}/description"),
                PciIdentity: identities[index],
                PciSubsystem: subsystems[index],
                IsIntegrated: Evidence.Observed(adapter.IsIntegrated, Basis.Authoritative, $"{at}/isIntegrated"),
                IsHardware: Evidence.Observed(adapter.IsHardware, Basis.Authoritative, $"{at}/isHardware"),
                DriverVersion: Evidence.Observed(adapter.DriverVersion, Basis.Authoritative, $"{at}/driverVersion"),
                DedicatedMemoryBytes: Evidence.Observed(adapter.DedicatedMemoryBytes, Basis.Authoritative, $"{at}/dedicatedMemoryBytes"),
                PciDevice: match);
        });

        return Fact.Observed<IReadOnlyList<GpuFacts>>([.. gpus], Basis.Authoritative, AdapterData);
    }

    /// <summary>For each opaque adapter id, the snapshot-scoped GPU ids that carry it (normally one), for joining display outputs to their GPU.</summary>
    public static ILookup<string, (string Id, string Pointer)> GpuIdsByAdapterId(AdapterData adapters) =>
        adapters.Adapters
            .Select((adapter, index) => (adapter.AdapterId, Id: GpuId(index), Pointer: $"{AdapterData}/{index}/adapterId"))
            .ToLookup(entry => entry.AdapterId, entry => (entry.Id, entry.Pointer), StringComparer.Ordinal);

    /// <summary>The adapter's PCI identity: field by field when the platform gives it, else from the combined hardware ID (no subsystem).</summary>
    private static Fact<PciFunctionId> Identity(GraphicsAdapter adapter, string at)
    {
        if (adapter.PciIds.IsKnown)
        {
            return Fact.Observed(Function(adapter.PciIds.Value!), Basis.Authoritative, $"{at}/pciIds");
        }

        if (adapter.HardwareId.Value is { } combined)
        {
            return Fact.Derived(new PciFunctionId(combined.VendorId, combined.DeviceId, combined.RevisionId), Basis.Authoritative, Rules.PciIdentityFromHardwareId, $"{at}/hardwareId");
        }

        return Fact.Missing<PciFunctionId>(
            Evidence.GapOf(adapter.PciIds.Missing!.Value),
            $"{adapter.PciIds.Detail}; {adapter.HardwareId.Detail}",
            $"{at}/pciIds",
            $"{at}/hardwareId");
    }

    private static Fact<PciSubsystemId> SubsystemOf(GraphicsAdapter adapter, string at) =>
        adapter.PciIds.IsKnown
            ? Fact.Observed(Subsystem(adapter.PciIds.Value!), Basis.Authoritative, $"{at}/pciIds")
            : Fact.Missing<PciSubsystemId>(
                adapter.HardwareId.IsKnown ? FactGap.NotSupported : Evidence.GapOf(adapter.PciIds.Missing!.Value),
                adapter.HardwareId.IsKnown
                    ? "The platform reports the subsystem only combined into one value, whose split is not documented"
                    : adapter.PciIds.Detail!,
                $"{at}/pciIds",
                $"{at}/hardwareId");

    /// <summary>
    /// <see cref="Rules.GpuPciDevice"/>. A display-class or unreported-class device whose own identity couldn't be read
    /// might be the adapter's, so while one exists no match is unique: the relationship is ambiguous, not unmatched.
    /// </summary>
    private static Fact<string> PciDeviceOf(Fact<PciFunctionId> identity, Fact<PciSubsystemId> subsystem, SourceEntry<PcieData> source)
    {
        if (!Evidence.TryData(source, "pcie", out var pcie, out var unavailable))
        {
            return unavailable.Fact<string>();
        }

        if (!identity.IsKnown)
        {
            return Fact.Missing<string>(FactGap.Unmatched, $"The adapter's PCI identity is unknown, so no PCI device can be matched to it ({identity.Detail})", identity.From);
        }

        var possible = pcie.Devices
            .Select((device, index) => (device, index))
            .Where(pair => pair.device.BaseClass is not { IsKnown: true, Value: not 3 })
            .ToList();
        var candidates = possible
            .Where(pair => pair.device.PciIds.Value is { } ids && Function(ids) == identity.Value
                && (!subsystem.IsKnown || Subsystem(ids) == subsystem.Value))
            .ToList();
        var unreadable = possible.Where(pair => !pair.device.PciIds.IsKnown).ToList();

        if (unreadable.Count > 0 && candidates.Count <= 1)
        {
            return Fact.Missing<string>(
                FactGap.Ambiguous,
                string.Create(CultureInfo.InvariantCulture, $"{unreadable.Count} PCI device(s) of display or unreported class have no readable identity, so a unique match can't be shown"),
                [.. identity.From, .. unreadable.Select(pair => $"{PcieData}/{pair.index}/pciIds"), .. candidates.Select(pair => $"{PcieData}/{pair.index}/pciIds")]);
        }

        return candidates.Count switch
        {
            1 => Fact.Derived(PciId(candidates[0].index), Basis.Authoritative, Rules.GpuPciDevice, [.. identity.From, $"{PcieData}/{candidates[0].index}/pciIds"]),
            0 => Fact.Missing<string>(FactGap.Unmatched, "No PCI display device in the snapshot has the adapter's PCI identity (a software adapter has none)", [.. identity.From, PcieData]),
            _ => Fact.Missing<string>(
                FactGap.Ambiguous,
                string.Create(CultureInfo.InvariantCulture, $"{candidates.Count} PCI devices have the adapter's PCI identity (identical cards), so none is claimed"),
                [.. identity.From, .. candidates.Select(pair => $"{PcieData}/{pair.index}/pciIds")]),
        };
    }

    private static PcieLinkFacts Link(PcieLink link, string at) => new(
        CurrentWidth: Width(Evidence.Observed(link.CurrentWidth, Basis.Authoritative, $"{at}/currentWidth")),
        MaxWidth: Width(Evidence.Observed(link.MaxWidth, Basis.Authoritative, $"{at}/maxWidth")),
        CurrentSpeedGtps: Speed(Evidence.Observed(link.CurrentSpeedGtps, Basis.Authoritative, $"{at}/currentSpeedGtps")),
        MaxSpeedGtps: Speed(Evidence.Observed(link.MaxSpeedGtps, Basis.Authoritative, $"{at}/maxSpeedGtps")));

    /// <summary>Every link fact missing for the reason the parent is.</summary>
    private static PcieLinkFacts UnknownLink(Fact<PciParent> parent) => new(
        Evidence.MissingLike<PciParent, int>(parent),
        Evidence.MissingLike<PciParent, int>(parent),
        Evidence.MissingLike<PciParent, decimal>(parent),
        Evidence.MissingLike<PciParent, decimal>(parent));

    private static Fact<int> Width(Fact<int> width) =>
        Evidence.Plausible(width, LinkWidths.Contains, value => string.Create(CultureInfo.InvariantCulture, $"{value} lanes is not a PCI Express link width"));

    private static Fact<decimal> Speed(Fact<decimal> speed) =>
        Evidence.Plausible(speed, value => value > 0, value => string.Create(CultureInfo.InvariantCulture, $"{value} GT/s is not a link speed"));

    private static PciFunctionId Function(PciIds ids) => new(ids.VendorId, ids.DeviceId, ids.RevisionId);

    private static PciSubsystemId Subsystem(PciIds ids) => new(ids.SubsystemVendorId, ids.SubsystemId);

    private static string PciId(int index) => string.Create(CultureInfo.InvariantCulture, $"pci-{index + 1}");

    private static string GpuId(int index) => string.Create(CultureInfo.InvariantCulture, $"gpu-{index + 1}");
}
