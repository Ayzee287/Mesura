using System.Globalization;
using System.Management;
using Mesura.Core.Windows;

namespace Mesura.Platform.Windows.Windows;

/// <summary>
/// Reads the network adapters through Windows' own network management classes (<c>root\StandardCimv2</c>, read only, the same data
/// <c>Get-NetAdapter</c>, <c>Get-NetAdapterAdvancedProperty</c>, <c>Get-NetAdapterBinding</c> and <c>Get-NetAdapterPowerManagement</c> show, in no display
/// language): identity and kind from <c>MSFT_NetAdapter</c>, the driver's advanced properties with the values it lists as valid, the protocol bindings and
/// the power switch. A part that can't be read for an adapter (some drivers refuse the power query) is left unknown for that adapter only; a failure of the
/// adapter list itself is <c>null</c>. Properties, bindings and power are read for physical adapters only: nothing else is ever tuned.
/// </summary>
internal static class NetworkAdapterReader
{
    private const string Namespace = @"root\StandardCimv2";

    public static IReadOnlyList<AdapterReading>? Read()
    {
        try
        {
            var adapters = new List<AdapterReading>();
            using var searcher = new ManagementObjectSearcher(Namespace,
                "SELECT Name, InterfaceDescription, InterfaceGuid, InterfaceType, NdisPhysicalMedium, HardwareInterface, Virtual, DriverProvider, DriverVersionString, DriverDate, "
                + "InterfaceOperationalStatus, PnPDeviceID FROM MSFT_NetAdapter");
            using var rows = searcher.Get();
            foreach (var row in rows)
            {
                using (row)
                {
                    if (row["InterfaceGuid"] is not string guid || row["Name"] is not string name)
                    {
                        continue;
                    }

                    var description = row["InterfaceDescription"] as string ?? "";
                    var hardware = row["HardwareInterface"] as bool? ?? false;
                    var virtualFlag = row["Virtual"] as bool? ?? false;
                    var interfaceType = ToInt(row["InterfaceType"]) ?? 0;
                    var kind = AdapterClassifier.Classify(description, interfaceType, ToInt(row["NdisPhysicalMedium"]), hardware, virtualFlag);
                    var physical = hardware && !virtualFlag && kind is AdapterKind.Ethernet or AdapterKind.WiFi or AdapterKind.Other;
                    var pciVendor = AdapterClassifier.PciVendor(row["PnPDeviceID"] as string);
                    var up = ToInt(row["InterfaceOperationalStatus"]) == 1;
                    var provider = row["DriverProvider"] as string;
                    var properties = physical ? ReadProperties(name) : new Dictionary<string, AdapterPropertyReading>();
                    var bindings = physical ? ReadBindings(name) : new Dictionary<string, bool>();
                    bool? allowTurnOff = physical ? ReadPower(name) : null;
                    adapters.Add(new AdapterReading(guid.ToUpperInvariant(), name, description, AdapterClassifier.Vendor(provider, pciVendor), pciVendor,
                        row["DriverVersionString"] as string ?? "", DriverDate(row["DriverDate"]), kind, physical, up, properties, bindings, allowTurnOff));
                }
            }

            return adapters;
        }
        catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            return null;
        }
    }

    private static Dictionary<string, AdapterPropertyReading> ReadProperties(string adapterName)
    {
        var result = new Dictionary<string, AdapterPropertyReading>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(Namespace,
                "SELECT RegistryKeyword, RegistryValue, DisplayName, DisplayValue, ValidDisplayValues, ValidRegistryValues, NumericParameterMinValue, NumericParameterMaxValue, "
                + $"NumericParameterStepValue FROM MSFT_NetAdapterAdvancedPropertySettingData WHERE Name = '{Escape(adapterName)}'");
            using var rows = searcher.Get();
            foreach (var row in rows)
            {
                using (row)
                {
                    if (row["RegistryKeyword"] is not string keyword || keyword.Length == 0 || AdapterPrivacy.IsPrivate(keyword))
                    {
                        continue;
                    }

                    var values = row["RegistryValue"] as string[] ?? [];
                    result[keyword] = new AdapterPropertyReading(keyword, row["DisplayName"] as string ?? "", values.Length > 0 ? values[0] : "", row["DisplayValue"] as string ?? "",
                        row["ValidRegistryValues"] as string[] ?? [], row["ValidDisplayValues"] as string[] ?? [],
                        ToLong(row["NumericParameterMinValue"]), ToLong(row["NumericParameterMaxValue"]), ToLong(row["NumericParameterStepValue"]));
                }
            }
        }
        catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            result.Clear(); // an adapter whose properties can't be read offers none
        }

        return result;
    }

    private static Dictionary<string, bool> ReadBindings(string adapterName)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(Namespace, $"SELECT ComponentID, Enabled FROM MSFT_NetAdapterBindingSettingData WHERE Name = '{Escape(adapterName)}'");
            using var rows = searcher.Get();
            foreach (var row in rows)
            {
                using (row)
                {
                    if (row["ComponentID"] is string id && row["Enabled"] is bool enabled)
                    {
                        result[id] = enabled;
                    }
                }
            }
        }
        catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            result.Clear();
        }

        return result;
    }

    /// <summary>"Allow the computer to turn off this device": 1 disabled, 2 enabled, 0 or an error unsupported (<c>null</c>).</summary>
    private static bool? ReadPower(string adapterName)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Namespace, $"SELECT AllowComputerToTurnOffDevice FROM MSFT_NetAdapterPowerManagementSettingData WHERE Name = '{Escape(adapterName)}'");
            using var rows = searcher.Get();
            foreach (var row in rows)
            {
                using (row)
                {
                    return ToInt(row["AllowComputerToTurnOffDevice"]) switch { 1 => false, 2 => true, _ => null };
                }
            }
        }
        catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // Some drivers answer "a device attached to the system is not functioning": the switch is then not offered.
        }

        return null;
    }

    private static string Escape(string name) => name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);

    private static int? ToInt(object? value) =>
        value is null ? null : int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static long? ToLong(object? value) =>
        value is null ? null : long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>The driver date as <c>yyyy-MM-dd</c>: Windows returns it already in that form, or as a CIM date-time.</summary>
    private static string DriverDate(object? value)
    {
        if (value is not string text || text.Length < 8)
        {
            return "";
        }

        if (text.Length >= 10 && text[4] == '-' && text[7] == '-')
        {
            return text[..10];
        }

        try
        {
            var date = ManagementDateTimeConverter.ToDateTime(text);
            return date.Year > 1601 ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
        }
        catch (ArgumentOutOfRangeException)
        {
            return "";
        }
    }
}
