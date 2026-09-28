using System.Globalization;
using System.Text.RegularExpressions;
using FpsUtility.Core.Raw;
using Windows.Win32;
using Windows.Win32.Devices.DeviceAndDriverInstallation;
using Windows.Win32.Foundation;

namespace FpsUtility.Platform.Windows.Collectors;

/// <summary>
/// <c>pcie</c>: current and maximum link width and speed of every present PCI display (base class 3)
/// and mass-storage (base class 1) device, and of any whose class Windows doesn't report, and of its parent, from the PnP device properties
/// (architecture §4.4, §7; D003). Not the NVIDIA/AMD APIs. A property Windows doesn't report stays
/// missing; nothing is filled in.
/// </summary>
internal static partial class PcieCollector
{
    public const string Source = "win32.CM_Get_Device_ID_ListW, win32.CM_Get_DevNode_PropertyW (DEVPKEY_PciDevice_*, DEVPKEY_Device_HardwareIds)";

    private const uint MassStorageClass = 0x01;
    private const uint DisplayClass = 0x03;

    public static PcieData Collect()
    {
        var devices = new List<PciDevice>();

        foreach (var instanceId in DeviceNodes.ListPresent("PCI"))
        {
            var located = DeviceNodes.Locate(instanceId, out var node);
            if (located == CONFIGRET.CR_NO_SUCH_DEVNODE)
            {
                continue; // removed after it was listed: no longer present
            }

            if (located != CONFIGRET.CR_SUCCESS)
            {
                throw new SourceUnavailableException(Win32Errors.Reason(located), Win32Errors.Describe("CM_Locate_DevNodeW", located));
            }

            // Out of scope only when Windows reports a class other than storage or display. A device whose
            // class can't be read stays listed, with the reason, so a GPU is never silently absent.
            var baseClass = DeviceNodes.GetUnsigned(node, PInvoke.DEVPKEY_PciDevice_BaseClass, "DEVPKEY_PciDevice_BaseClass");
            if (baseClass.IsKnown && baseClass.Value is not (MassStorageClass or DisplayClass))
            {
                continue;
            }

            devices.Add(new PciDevice(
                InstanceId: instanceId,
                BaseClass: baseClass.IsKnown ? Reading.Known((int)baseClass.Value) : Reading.Missing<int>(baseClass.Missing!.Value, baseClass.Detail!),
                PciIds: ReadPciIds(node),
                Link: ReadLink(node),
                Parent: ReadParent(node)));
        }

        return new PcieData(devices);
    }

    /// <summary>
    /// PCIe link-speed code to GT/s. Codes 1 and 2 are named in the Windows SDK (pciprop.h). Codes 3–6
    /// follow the PCI Express Link Status register's Current Link Speed encoding, which codes 1 and 2
    /// match; code 3 is what PC-000's Gen3 NVMe controller reports. The Gen3+ mapping on GPUs is
    /// still (verify) in architecture §7. Any other code stays missing, with the code kept.
    /// </summary>
    internal static Reading<decimal> MapSpeed(Reading<uint> code, string keyName)
    {
        if (!code.IsKnown)
        {
            return Reading.Missing<decimal>(code.Missing!.Value, code.Detail!);
        }

        decimal? gtps = code.Value switch
        {
            1 => 2.5m,
            2 => 5.0m,
            3 => 8.0m,
            4 => 16.0m,
            5 => 32.0m,
            6 => 64.0m,
            _ => null,
        };

        return gtps is { } value
            ? Reading.Known(value, code.Value)
            : Reading.Missing<decimal>(
                MissingReason.ParseFailed,
                string.Create(CultureInfo.InvariantCulture, $"{keyName}: {code.Value} is not a PCIe link-speed code"),
                code.Value);
    }

    /// <summary>
    /// Decodes the most specific hardware ID, <c>PCI\VEN_v(4)&amp;DEV_d(4)&amp;SUBSYS_s(4)n(4)&amp;REV_r(2)</c>,
    /// where s is the subsystem ID and n the subsystem vendor ID (Learn, "Identifiers for PCI Devices").
    /// A lossless re-encoding of that string; anything else stays missing.
    /// </summary>
    internal static Reading<PciIds> DecodeHardwareId(Reading<string[]> hardwareIds)
    {
        if (!hardwareIds.IsKnown)
        {
            return Reading.Missing<PciIds>(hardwareIds.Missing!.Value, hardwareIds.Detail!);
        }

        foreach (var id in hardwareIds.Value!)
        {
            var match = FullHardwareId().Match(id);
            if (match.Success)
            {
                return Reading.Known(new PciIds(
                    VendorId: Hex(match.Groups["ven"].Value),
                    DeviceId: Hex(match.Groups["dev"].Value),
                    SubsystemVendorId: Hex(match.Groups["svid"].Value),
                    SubsystemId: Hex(match.Groups["sid"].Value),
                    RevisionId: Hex(match.Groups["rev"].Value)));
            }
        }

        return Reading.Missing<PciIds>(
            MissingReason.ParseFailed,
            $"No hardware ID has the PCI\\VEN_&DEV_&SUBSYS_&REV_ form: {string.Join(" | ", hardwareIds.Value!)}");
    }

    private static Reading<PciIds> ReadPciIds(uint node) =>
        DecodeHardwareId(DeviceNodes.GetStringList(node, PInvoke.DEVPKEY_Device_HardwareIds, "DEVPKEY_Device_HardwareIds"));

    private static PcieLink ReadLink(uint node) => new(
        CurrentWidth: Width(DeviceNodes.GetUnsigned(node, PInvoke.DEVPKEY_PciDevice_CurrentLinkWidth, "DEVPKEY_PciDevice_CurrentLinkWidth")),
        MaxWidth: Width(DeviceNodes.GetUnsigned(node, PInvoke.DEVPKEY_PciDevice_MaxLinkWidth, "DEVPKEY_PciDevice_MaxLinkWidth")),
        CurrentSpeedGtps: MapSpeed(DeviceNodes.GetUnsigned(node, PInvoke.DEVPKEY_PciDevice_CurrentLinkSpeed, "DEVPKEY_PciDevice_CurrentLinkSpeed"), "DEVPKEY_PciDevice_CurrentLinkSpeed"),
        MaxSpeedGtps: MapSpeed(DeviceNodes.GetUnsigned(node, PInvoke.DEVPKEY_PciDevice_MaxLinkSpeed, "DEVPKEY_PciDevice_MaxLinkSpeed"), "DEVPKEY_PciDevice_MaxLinkSpeed"));

    private static Reading<PciParent> ReadParent(uint node)
    {
        var parent = DeviceNodes.GetParent(node);
        if (!parent.IsKnown)
        {
            return Reading.Missing<PciParent>(parent.Missing!.Value, parent.Detail!);
        }

        var parentId = DeviceNodes.GetInstanceId(parent.Value);
        return parentId.IsKnown
            ? Reading.Known(new PciParent(parentId.Value!, ReadLink(parent.Value)))
            : Reading.Missing<PciParent>(parentId.Missing!.Value, parentId.Detail!);
    }

    /// <summary>The width property is already a lane count (pciprop.h <c>LinkWidth_By_n = n</c>), so it is kept as is.</summary>
    private static Reading<int> Width(Reading<uint> lanes) =>
        lanes.IsKnown
            ? Reading.Known(checked((int)lanes.Value))
            : Reading.Missing<int>(lanes.Missing!.Value, lanes.Detail!);

    private static int Hex(string digits) => int.Parse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^PCI\\VEN_(?<ven>[0-9A-F]{4})&DEV_(?<dev>[0-9A-F]{4})&SUBSYS_(?<sid>[0-9A-F]{4})(?<svid>[0-9A-F]{4})&REV_(?<rev>[0-9A-F]{2})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FullHardwareId();
}
