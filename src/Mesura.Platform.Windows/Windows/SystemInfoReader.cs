using System.Globalization;
using Microsoft.Win32;
using Mesura.Core.Facts;
using Mesura.Core.Normalize;
using Mesura.Core.Raw;
using Mesura.Core.SystemInfo;
using Windows.Win32;
using Windows.Win32.System.SystemInformation;

namespace Mesura.Platform.Windows.Windows;

/// <summary>
/// Reads the System page's report (read-only, no administrator rights): the diagnostic scan the product already has (firmware
/// tables, adapters, displays, storage), turned into facts and a <see cref="SystemReport"/>, plus the few values that scan
/// doesn't hold: Windows' own product name and build, memory in use, BIOS version, boot mode, Secure Boot and the power plan.
/// Each extra that can't be read stays unknown; none of this throws for a missing source.
/// </summary>
public static class SystemInfoReader
{
    private const string CurrentVersion = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    public static SystemReport Read(string appVersion)
    {
        var os = OsInfoReader.Read();
        var snapshot = SnapshotCollector.Collect(os, new AppInfo("Mesura", appVersion, Reading.Missing<string>(MissingReason.NotPresent, "A System page read, not a capture")));
        var facts = Normalizer.Normalize(snapshot);
        return SystemReportBuilder.Build(facts, ReadExtras(os.Build), DateTimeOffset.Now);
    }

    public static SystemExtras ReadExtras(int build)
    {
        var productName = ReadString(Registry.LocalMachine, CurrentVersion, "ProductName");
        if (productName is not null && build >= 22000)
        {
            productName = productName.Replace("Windows 10", "Windows 11", StringComparison.Ordinal); // the registry keeps the old name on Windows 11
        }

        var memory = ReadMemory();
        return new SystemExtras
        {
            OsProductName = productName,
            OsDisplayVersion = ReadString(Registry.LocalMachine, CurrentVersion, "DisplayVersion") ?? ReadString(Registry.LocalMachine, CurrentVersion, "ReleaseId"),
            InstalledOn = ReadInstallDate(),
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
            MemoryTotalBytes = memory?.Total,
            MemoryAvailableBytes = memory?.Available,
            CpuName = ReadString(Registry.LocalMachine, @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString")?.Trim(),
            PowerPlan = ReadPowerPlan(),
            ModernStandby = new SystemStateReader().ModernStandby,
            BiosVendor = Trimmed(ReadString(Registry.LocalMachine, @"HARDWARE\DESCRIPTION\System\BIOS", "BIOSVendor")),
            BiosVersion = Trimmed(ReadString(Registry.LocalMachine, @"HARDWARE\DESCRIPTION\System\BIOS", "BIOSVersion")),
            BiosDate = Trimmed(ReadString(Registry.LocalMachine, @"HARDWARE\DESCRIPTION\System\BIOS", "BIOSReleaseDate")),
            BootMode = ReadBootMode(),
            SecureBoot = ReadSecureBoot(),
        };
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static (ulong Total, ulong Available)? ReadMemory()
    {
        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MEMORYSTATUSEX>() };
            return PInvoke.GlobalMemoryStatusEx(ref status) ? (status.ullTotalPhys, status.ullAvailPhys) : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or DllNotFoundException)
        {
            return null;
        }
    }

    private static string? ReadBootMode()
    {
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(8))
            {
                return null;
            }

            var type = FIRMWARE_TYPE.FirmwareTypeUnknown;
            return PInvoke.GetFirmwareType(ref type) ? type switch
            {
                FIRMWARE_TYPE.FirmwareTypeUefi => "UEFI",
                FIRMWARE_TYPE.FirmwareTypeBios => "Legacy BIOS",
                _ => null,
            } : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or DllNotFoundException)
        {
            return null;
        }
    }

    private static bool? ReadSecureBoot()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            return key?.GetValue("UEFISecureBootEnabled") is int enabled ? enabled == 1 : null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static DateTimeOffset? ReadInstallDate()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(CurrentVersion);
            return key?.GetValue("InstallDate") is int seconds and > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime() : null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>The plan Windows is using, by name for the plans every PC has; any other is "Custom".</summary>
    private static string? ReadPowerPlan()
    {
        var guid = ReadString(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes", "ActivePowerScheme");
        if (guid is null)
        {
            return null;
        }

        return guid.ToLower(CultureInfo.InvariantCulture) switch
        {
            "381b4222-f694-41f0-9685-ff5bb260df2e" => "Balanced",
            "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" => "High performance",
            "a1841308-3541-4fab-bc81-f71556f20b4a" => "Power saver",
            "e9a42b02-d5df-448d-aa00-03f14749eb61" => "Ultimate Performance",
            _ => "Custom",
        };
    }

    private static string? ReadString(RegistryKey hive, string path, string name)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            return key?.GetValue(name) is string { Length: > 0 } value ? value : null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
