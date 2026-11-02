using System.Globalization;

namespace Mesura.Core.Windows;

/// <summary>What a network adapter is, for deciding which settings apply to it (0.5).</summary>
public enum AdapterKind
{
    /// <summary>A physical wired adapter (802.3).</summary>
    Ethernet,

    /// <summary>A physical wireless adapter (802.11).</summary>
    WiFi,

    /// <summary>A virtual switch, a virtual machine's adapter, a tunnel: nothing Mesura tunes.</summary>
    Virtual,

    /// <summary>A VPN's adapter (a TAP or Wintun device, a vendor's tunnel): never touched.</summary>
    Vpn,

    /// <summary>Bluetooth, USB tethering, WAN miniports and the rest.</summary>
    Other,
}

/// <summary>
/// One advanced property of an adapter as its driver declares it (<c>Get-NetAdapterAdvancedProperty</c>, read through
/// <c>MSFT_NetAdapterAdvancedPropertySettingData</c>): the registry keyword and value, the text Windows shows, and what the driver says is valid:
/// the registry values of its list (an enumeration) or a numeric range.
/// </summary>
public sealed record AdapterPropertyReading(
    string Keyword,
    string DisplayName,
    string RegistryValue,
    string DisplayValue,
    IReadOnlyList<string> ValidRegistryValues,
    IReadOnlyList<string> ValidDisplayValues,
    long? NumericMin = null,
    long? NumericMax = null,
    long? NumericStep = null);

/// <summary>
/// A network adapter and what it reports: identity (stable <see cref="InterfaceGuid"/>, name, maker, driver), what kind it is, whether it is up, its
/// advanced properties, its protocol bindings and whether Windows may turn it off to save power (<c>null</c> when it can't say).
/// </summary>
public sealed record AdapterReading(
    string InterfaceGuid,
    string Name,
    string Description,
    string Vendor,
    int? PciVendorId,
    string DriverVersion,
    string DriverDate,
    AdapterKind Kind,
    bool Physical,
    bool Up,
    IReadOnlyDictionary<string, AdapterPropertyReading> Properties,
    IReadOnlyDictionary<string, bool> Bindings,
    bool? AllowTurnOff)
{
    /// <summary>The adapter as a person knows it: "Intel(R) Ethernet Controller I225-V (Ethernet)".</summary>
    public string Label => string.IsNullOrWhiteSpace(Description) ? Name : $"{Description} ({Name})";
}

/// <summary>What kind of adapter a set of Windows facts describes, and the rule that makes a setting apply to it.</summary>
public static class AdapterClassifier
{
    private static readonly string[] VpnMarkers =
    [
        "TAP-Windows", "TAP-NordVPN", "Wintun", "WireGuard", "OpenVPN", "NordLynx", "Cisco AnyConnect", "AnyConnect", "Fortinet", "Check Point", "GlobalProtect",
        "PANGP", "SoftEther", "ProtonVPN", "Mullvad", "Surfshark", "ExpressVPN", "Tailscale", "ZeroTier", "Hamachi", "VPN",
    ];

    private static readonly string[] VirtualMarkers =
    [
        "Hyper-V", "vEthernet", "VMware", "VirtualBox", "Virtual Adapter", "Microsoft Kernel Debug", "Loopback", "Teredo", "ISATAP", "6to4", "IP-HTTPS", "Npcap", "WSL", "TAP",
    ];

    /// <summary>
    /// Wi-Fi when the interface type is 71 or the NDIS medium is native 802.11 (9); Ethernet for a hardware interface of type 6 on 802.3 (14); virtual and VPN
    /// by what the adapter says it is (not a hardware interface, a tunnel type, or a known name); anything else is "other".
    /// </summary>
    public static AdapterKind Classify(string description, int interfaceType, int? ndisMedium, bool hardwareInterface, bool virtualFlag)
    {
        var text = description ?? "";
        if (VpnMarkers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase)) && (!hardwareInterface || virtualFlag || interfaceType is 53 or 131 or 23))
        {
            return AdapterKind.Vpn;
        }

        if (virtualFlag || !hardwareInterface || interfaceType is 131 or 53 || VirtualMarkers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase)))
        {
            return AdapterKind.Virtual;
        }

        if (interfaceType == 71 || ndisMedium == 9)
        {
            return AdapterKind.WiFi;
        }

        if (interfaceType == 6 && (ndisMedium is null or 14 or 0))
        {
            return text.Contains("Remote NDIS", StringComparison.OrdinalIgnoreCase) || text.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) ? AdapterKind.Other : AdapterKind.Ethernet;
        }

        return AdapterKind.Other;
    }

    /// <summary>The maker's name from the driver provider, else from the PCI vendor id of the device.</summary>
    public static string Vendor(string? driverProvider, int? pciVendorId)
    {
        var fromId = pciVendorId switch
        {
            0x8086 => "Intel",
            0x10EC => "Realtek",
            0x14E4 => "Broadcom",
            0x168C or 0x1969 or 0x17CB => "Qualcomm",
            0x1814 => "Ralink/MediaTek",
            0x14C3 => "MediaTek",
            0x11AB => "Marvell",
            0x1186 => "D-Link",
            0x1AE0 => "Google",
            0x15B3 => "Mellanox",
            0x1D6A => "Aquantia/Marvell",
            _ => null,
        };
        if (!string.IsNullOrWhiteSpace(driverProvider) && !driverProvider.Equals("Microsoft", StringComparison.OrdinalIgnoreCase))
        {
            return driverProvider.Trim();
        }

        return fromId ?? (string.IsNullOrWhiteSpace(driverProvider) ? "" : driverProvider.Trim());
    }

    /// <summary>The PCI vendor id in a PnP device id such as <c>PCI\VEN_8086&amp;DEV_A0F0&amp;...</c>, or <c>null</c> (a USB or software device).</summary>
    public static int? PciVendor(string? pnpDeviceId)
    {
        const string marker = "PCI\\VEN_";
        var text = pnpDeviceId ?? "";
        var at = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return at >= 0 && text.Length >= at + marker.Length + 4
            && int.TryParse(text.AsSpan(at + marker.Length, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var id) ? id : null;
    }
}

/// <summary>What an adapter setting asks of one property, binding or the power-management switch.</summary>
public enum AdapterSettingKind
{
    /// <summary>An advanced property of the driver, by registry keyword.</summary>
    Property,

    /// <summary>A protocol binding of the adapter, by component id (<c>ms_lldp</c>).</summary>
    Binding,

    /// <summary>"Allow the computer to turn off this device to save power".</summary>
    PowerManagement,
}

/// <summary>How the wanted value of a property is found.</summary>
public enum AdapterGoal
{
    /// <summary>The registry value given, only when the driver lists it as valid (or, for a number, inside its range).</summary>
    RegistryValue,

    /// <summary>The driver's own entry whose text is the display value given (for vendor keywords whose numbers mean different things per driver).</summary>
    DisplayValue,

    /// <summary>The largest value the driver allows (a numeric property's maximum).</summary>
    Maximize,
}

/// <summary>
/// One thing an adapter operation sets on every adapter it applies to. It is never a blind write: a property is touched only on an adapter whose driver
/// declares the keyword and lists the wanted value as valid, only on the <see cref="Kinds"/> given, and (when <see cref="Vendors"/> is given) only for those
/// makers, so a vendor's keyword is never written to another vendor's card.
/// </summary>
public sealed record AdapterSetting(
    AdapterSettingKind Kind,
    string Key,
    AdapterGoal Goal,
    string Value,
    bool Enabled,
    IReadOnlyList<AdapterKind> Kinds,
    IReadOnlyList<string> Vendors)
{
    public string Target => Kind switch
    {
        AdapterSettingKind.Property => $"adapter property {Key}",
        AdapterSettingKind.Binding => $"adapter binding {Key}",
        _ => "adapter power management",
    };

    /// <summary>Whether this setting is meant for <paramref name="adapter"/> at all (physical, of the right kind and maker).</summary>
    public bool Matches(AdapterReading adapter) =>
        adapter.Physical && Kinds.Contains(adapter.Kind)
        && (Vendors.Count == 0 || Vendors.Any(v => adapter.Vendor.Contains(v, StringComparison.OrdinalIgnoreCase) || adapter.Description.Contains(v, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// What this setting would write on <paramref name="adapter"/>: the registry value (or <c>True</c>/<c>False</c> for a binding or the power switch), or
    /// <c>null</c> when the adapter can't take it (the driver has no such keyword, doesn't list the value, or the binding isn't there).
    /// </summary>
    public string? Wanted(AdapterReading adapter)
    {
        switch (Kind)
        {
            case AdapterSettingKind.Binding:
                return adapter.Bindings.ContainsKey(Key) ? Enabled.ToString() : null;
            case AdapterSettingKind.PowerManagement:
                return adapter.AllowTurnOff is null ? null : Enabled.ToString();
        }

        if (!adapter.Properties.TryGetValue(Key, out var property))
        {
            return null;
        }

        switch (Goal)
        {
            case AdapterGoal.Maximize:
                return property.NumericMax is { } max && (property.NumericMin ?? 0) <= max ? max.ToString(CultureInfo.InvariantCulture) : null;
            case AdapterGoal.DisplayValue:
            {
                var at = property.ValidDisplayValues.ToList().FindIndex(d => d.Equals(Value, StringComparison.OrdinalIgnoreCase));
                return at >= 0 && at < property.ValidRegistryValues.Count ? property.ValidRegistryValues[at] : null;
            }

            default:
                if (property.ValidRegistryValues.Count > 0)
                {
                    return property.ValidRegistryValues.Contains(Value, StringComparer.OrdinalIgnoreCase) ? Value : null;
                }

                return long.TryParse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && property.NumericMin is { } lo && property.NumericMax is { } hi && number >= lo && number <= hi ? Value : null;
        }
    }

    /// <summary>The adapter's current value for this setting in the same words as <see cref="Wanted"/>, or <c>null</c> when it can't be read.</summary>
    public string? Current(AdapterReading adapter) => Kind switch
    {
        AdapterSettingKind.Binding => adapter.Bindings.TryGetValue(Key, out var on) ? on.ToString() : null,
        AdapterSettingKind.PowerManagement => adapter.AllowTurnOff?.ToString(),
        _ => adapter.Properties.TryGetValue(Key, out var p) ? p.RegistryValue : null,
    };
}
