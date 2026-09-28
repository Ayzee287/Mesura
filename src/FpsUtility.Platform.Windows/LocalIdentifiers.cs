using System.ComponentModel;
using System.Management;
using FpsUtility.Core.Privacy;
using Microsoft.Win32;

namespace FpsUtility.Platform.Windows;

/// <summary>
/// Reads this machine's own identifiers so <see cref="IdentifierLeakCheck"/> can prove a fixture holds
/// none of them. They exist only in memory for that comparison; nothing here is written to the
/// snapshot, a file or the console (architecture §13). Read-only, standard user.
/// </summary>
public static class LocalIdentifiers
{
    private const string MonitorEdidCategory = "monitor EDID identifier";

    /// <returns>The identifiers read, and the categories that couldn't be read (so the check can say what it didn't cover).</returns>
    public static (IReadOnlyList<LocalIdentifier> Identifiers, IReadOnlyList<string> Unread) Read()
    {
        var identifiers = new List<LocalIdentifier>();
        var unread = new List<string>();

        void Add(string category, IdentifierKind kind, Func<IEnumerable<string?>> read)
        {
            try
            {
                foreach (var value in read())
                {
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        identifiers.Add(new LocalIdentifier(category, value, kind));
                    }
                }
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                unread.Add(category);
            }
        }

        const IdentifierKind Personal = IdentifierKind.Personal;
        const IdentifierKind Hardware = IdentifierKind.Hardware;

        Add("computer name", Personal, () => [Environment.MachineName, System.Net.Dns.GetHostName()]);
        Add("user name", Personal, () => [Environment.UserName]);
        Add("user profile path", Personal, () => [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)]);
        Add("machine GUID", Personal, () => [RegistryValue(@"SOFTWARE\Microsoft\Cryptography", "MachineGuid")]);
        Add("Windows product ID", Personal, () => [RegistryValue(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductId")]);
        Add("registered owner", Personal, () => [RegistryValue(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "RegisteredOwner")]);
        Add("registered organisation", Personal, () => [RegistryValue(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "RegisteredOrganization")]);
        Add("system serial number", Hardware, () => Wmi(@"root\cimv2", "SELECT SerialNumber FROM Win32_BIOS", "SerialNumber"));
        Add("system UUID", Hardware, () => Wmi(@"root\cimv2", "SELECT UUID FROM Win32_ComputerSystemProduct", "UUID"));
        Add("system identifying number", Hardware, () => Wmi(@"root\cimv2", "SELECT IdentifyingNumber FROM Win32_ComputerSystemProduct", "IdentifyingNumber"));
        Add("board serial number", Hardware, () => Wmi(@"root\cimv2", "SELECT SerialNumber FROM Win32_BaseBoard", "SerialNumber"));
        Add("chassis serial number", Hardware, () => Wmi(@"root\cimv2", "SELECT SerialNumber FROM Win32_SystemEnclosure", "SerialNumber"));
        Add("chassis asset tag", Hardware, () => Wmi(@"root\cimv2", "SELECT SMBIOSAssetTag FROM Win32_SystemEnclosure", "SMBIOSAssetTag"));
        Add("memory serial number", Hardware, () => Wmi(@"root\cimv2", "SELECT SerialNumber FROM Win32_PhysicalMemory", "SerialNumber"));
        Add("processor serial number", Hardware, () => Wmi(@"root\cimv2", "SELECT SerialNumber FROM Win32_Processor", "SerialNumber"));
        Add("disk serial number", Hardware, () => Wmi(@"root\cimv2", "SELECT SerialNumber FROM Win32_DiskDrive", "SerialNumber"));
        Add("disk serial number", Hardware, () => Wmi(@"root\Microsoft\Windows\Storage", "SELECT SerialNumber FROM MSFT_PhysicalDisk", "SerialNumber"));

        // On NVMe this holds the drive's own serial, padded with spaces and followed by a suffix such as "_0005"
        // (the form seen on PC-000), and it differs from SerialNumber above. The serial is the first part.
        Add("disk serial number", Hardware, () => Wmi(@"root\Microsoft\Windows\Storage", "SELECT AdapterSerialNumber FROM MSFT_PhysicalDisk", "AdapterSerialNumber")
            .Select(value => value?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()));
        Add("disk unique ID", Hardware, () => Wmi(@"root\Microsoft\Windows\Storage", "SELECT UniqueId FROM MSFT_PhysicalDisk", "UniqueId"));
        Add("network MAC address", Hardware, () => Wmi(@"root\cimv2", "SELECT MACAddress FROM Win32_NetworkAdapter WHERE MACAddress IS NOT NULL", "MACAddress"));
        Add("monitor serial number", Hardware, MonitorSerials);

        identifiers.AddRange(MonitorEdidIdentifiers(unread));

        return (identifiers, unread);
    }

    /// <summary>The failures that mean "this source can't be read here"; anything else is a bug and propagates.</summary>
    private static bool IsReadFailure(Exception ex) =>
        ex is ManagementException or UnauthorizedAccessException or System.Security.SecurityException or IOException
            or System.Runtime.InteropServices.COMException or System.Net.Sockets.SocketException or Win32Exception
            or InvalidOperationException;

    /// <summary>
    /// Every identifier field in the EDID of every monitor Windows has recorded (connected or not): each field
    /// the collector's own scrub zeroes, taken separately. The leak check then proves none of them survives
    /// anywhere in the fixture, including where firmware repeats a serial in another block: a tiled monitor's
    /// tile serial equals its base-block serial, and that copy was once missed. A monitor whose key can't be
    /// read is skipped, and the category is reported as not fully checked.
    /// </summary>
    private static List<LocalIdentifier> MonitorEdidIdentifiers(List<string> unread)
    {
        var found = new List<LocalIdentifier>();
        try
        {
            using var display = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\DISPLAY");
            foreach (var model in display?.GetSubKeyNames() ?? [])
            {
                using var modelKey = display!.OpenSubKey(model);
                foreach (var instance in modelKey?.GetSubKeyNames() ?? [])
                {
                    try
                    {
                        using var parameters = modelKey!.OpenSubKey(instance + @"\Device Parameters");
                        if (parameters?.GetValue("EDID") is byte[] edid)
                        {
                            found.AddRange(EdidFields(edid).Select(field => LocalIdentifier.Binary(MonitorEdidCategory, field)));
                        }
                    }
                    catch (Exception ex) when (IsReadFailure(ex))
                    {
                        AddOnce(unread, MonitorEdidCategory);
                    }
                }
            }
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            AddOnce(unread, MonitorEdidCategory);
        }

        return found;
    }

    /// <summary>The original bytes of each field the scrub zeroes. A buffer that isn't an EDID is kept by no collector, so it has none.</summary>
    internal static IEnumerable<byte[]> EdidFields(byte[] edid)
    {
        EdidScrubber.Result scrubbed;
        try
        {
            scrubbed = EdidScrubber.Scrub(edid);
        }
        catch (FormatException)
        {
            return [];
        }

        return scrubbed.ZeroedFields.Select(field => edid[field]);
    }

    private static void AddOnce(List<string> list, string item)
    {
        if (!list.Contains(item))
        {
            list.Add(item);
        }
    }

    private static string? RegistryValue(string key, string name)
    {
        using var opened = Registry.LocalMachine.OpenSubKey(key);
        return opened?.GetValue(name) as string;
    }

    private static List<string?> Wmi(string scope, string query, string property)
    {
        using var searcher = new ManagementObjectSearcher(scope, query);
        using var results = searcher.Get();
        var values = new List<string?>();
        foreach (var instance in results)
        {
            using (instance)
            {
                values.Add(instance[property]?.ToString());
            }
        }

        return values;
    }

    /// <summary>The EDID serial string Windows decodes for each monitor (<c>WmiMonitorID.SerialNumberID</c>, UInt16 characters).</summary>
    private static List<string?> MonitorSerials()
    {
        using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT SerialNumberID FROM WmiMonitorID");
        using var results = searcher.Get();
        var values = new List<string?>();
        foreach (var instance in results)
        {
            using (instance)
            {
                if (instance["SerialNumberID"] is ushort[] characters)
                {
                    values.Add(new string([.. characters.TakeWhile(c => c != 0).Select(c => (char)c)]));
                }
            }
        }

        return values;
    }
}
