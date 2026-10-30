using System.Globalization;
using System.Management;
using Mesura.Core.Firmware;
using Microsoft.Win32;

namespace Mesura.Platform.Windows.Windows;

/// <summary>
/// The readings the BIOS &amp; Firmware page needs beyond the System report (0.4): whether virtualization is on, whether Windows sees a TPM, the
/// processor's vendor and microcode revision, and the memory window Windows assigned to each graphics adapter (what Resizable BAR enlarges).
/// Read-only, no administrator rights; a reading that can't be taken is <c>null</c> and the page says "can't tell", never a guess. The
/// Win32_Tpm class would say more but refuses a standard user, so the TPM comes from the device list instead.
/// </summary>
public static class FirmwareReader
{
    private const string ProcessorKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";

    public static FirmwareExtras Read() => new()
    {
        Virtualization = ReadVirtualization(),
        HypervisorRunning = ReadHypervisor(),
        CpuVendor = ReadString(ProcessorKey, "VendorIdentifier"),
        Microcode = ReadMicrocode(),
        TpmPresent = ReadTpm(out var version),
        TpmVersion = version,
        GpuWindows = ReadGpuWindows(),
    };

    private static bool? ReadVirtualization()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT VirtualizationFirmwareEnabled FROM Win32_Processor");
            using var results = searcher.Get();
            foreach (var row in results)
            {
                using (row)
                {
                    if (row["VirtualizationFirmwareEnabled"] is bool enabled)
                    {
                        return enabled;
                    }
                }
            }
        }
        catch (Exception e) when (e is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static bool? ReadHypervisor()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT HypervisorPresent FROM Win32_ComputerSystem");
            using var results = searcher.Get();
            foreach (var row in results)
            {
                using (row)
                {
                    if (row["HypervisorPresent"] is bool present)
                    {
                        return present;
                    }
                }
            }
        }
        catch (Exception e) when (e is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    /// <summary>
    /// The microcode revision Windows reports (<c>Update Revision</c>): eight bytes with the revision in the upper dword on the systems where it is
    /// eight bytes, a plain dword where it is four. Anything else is not read.
    /// </summary>
    internal static uint? ReadMicrocode()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ProcessorKey);
            return key?.GetValue("Update Revision") is byte[] bytes ? ParseMicrocode(bytes) : null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    internal static uint? ParseMicrocode(byte[] bytes) => bytes.Length switch
    {
        >= 8 => BitConverter.ToUInt32(bytes, 4) is > 0 and var upper ? upper : BitConverter.ToUInt32(bytes, 0) is > 0 and var lower ? lower : null,
        4 => BitConverter.ToUInt32(bytes, 0) is > 0 and var only ? only : null,
        _ => null,
    };

    private static bool? ReadTpm(out string? version)
    {
        version = null;
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT Name, Status FROM Win32_PnPEntity WHERE Name LIKE 'Trusted Platform Module%'");
            using var results = searcher.Get();
            foreach (var row in results)
            {
                using (row)
                {
                    var name = row["Name"] as string;
                    var number = name?.Split(' ').LastOrDefault();
                    version = number is { Length: > 0 } && char.IsDigit(number[0]) ? number : null;
                    return string.Equals(row["Status"] as string, "OK", StringComparison.OrdinalIgnoreCase);
                }
            }

            return false;
        }
        catch (Exception e) when (e is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The largest memory range assigned to each display adapter. The association between a device and its ranges carries only each range's start,
    /// so the ranges are listed once and matched by that start to get their ends.
    /// </summary>
    private static IReadOnlyList<GpuWindow> ReadGpuWindows()
    {
        try
        {
            var ends = new Dictionary<ulong, ulong>();
            using (var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT StartingAddress, EndingAddress FROM Win32_DeviceMemoryAddress"))
            using (var rows = searcher.Get())
            {
                foreach (var row in rows)
                {
                    using (row)
                    {
                        if (row["StartingAddress"] is { } s && row["EndingAddress"] is { } e)
                        {
                            ends[Convert.ToUInt64(s, CultureInfo.InvariantCulture)] = Convert.ToUInt64(e, CultureInfo.InvariantCulture);
                        }
                    }
                }
            }

            var adapters = new List<(string Name, string Pnp)>();
            using (var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT Name, PNPDeviceID FROM Win32_VideoController"))
            using (var rows = searcher.Get())
            {
                foreach (var row in rows)
                {
                    using (row)
                    {
                        if (row["Name"] is string name && row["PNPDeviceID"] is string pnp)
                        {
                            adapters.Add((name, pnp));
                        }
                    }
                }
            }

            var largest = adapters.ToDictionary(a => a.Pnp, _ => 0UL, StringComparer.OrdinalIgnoreCase);
            using (var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT Antecedent, Dependent FROM Win32_PnPAllocatedResource"))
            using (var rows = searcher.Get())
            {
                foreach (var row in rows)
                {
                    using (row)
                    {
                        var dependent = row["Dependent"] as string;
                        var antecedent = row["Antecedent"] as string;
                        if (dependent is null || antecedent is null || !antecedent.Contains("Win32_DeviceMemoryAddress", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        foreach (var adapter in adapters)
                        {
                            // The reference text holds the device id with its backslashes doubled.
                            if (dependent.Contains(adapter.Pnp.Replace(@"\", @"\\", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase)
                                && StartOf(antecedent) is { } start && ends.TryGetValue(start, out var end) && end >= start)
                            {
                                largest[adapter.Pnp] = Math.Max(largest[adapter.Pnp], end - start + 1);
                            }
                        }
                    }
                }
            }

            return adapters.Where(a => largest[a.Pnp] > 0).Select(a => new GpuWindow(a.Name, largest[a.Pnp])).ToList();
        }
        catch (Exception e) when (e is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or OverflowException or FormatException)
        {
            return [];
        }
    }

    /// <summary>The start address in a WMI reference such as <c>\\HOST\root\cimv2:Win32_DeviceMemoryAddress.StartingAddress="274877906944"</c>.</summary>
    internal static ulong? StartOf(string reference)
    {
        const string marker = "StartingAddress=\"";
        var at = reference.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var rest = reference[(at + marker.Length)..];
        var end = rest.IndexOf('"', StringComparison.Ordinal);
        return end > 0 && ulong.TryParse(rest[..end], NumberStyles.None, CultureInfo.InvariantCulture, out var start) ? start : null;
    }

    private static string? ReadString(string path, string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key?.GetValue(name) is string { Length: > 0 } value ? value : null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
