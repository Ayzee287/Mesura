using System.Globalization;
using System.Runtime.InteropServices;
using Mesura.Core.Raw;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.SystemInformation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Mesura.Platform.Windows;

/// <summary>
/// Reads the OS facts the platform check decides on, before any collection (D011; architecture
/// §17.1). It only reads; <c>Core.Support.PlatformSupport</c> decides.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Build: <see cref="Environment.OSVersion"/>, which reads the real version. The Win32 version
/// APIs are not used for it, because without a <c>supportedOS</c> manifest entry they report 6.2.</item>
/// <item>Architecture: the native machine from <c>IsWow64Process2</c>, which stays Arm64 when this x64
/// process runs under emulation on Windows 11 on Arm.</item>
/// <item>Product type: <c>VerifyVersionInfoW</c> on <c>VER_PRODUCT_TYPE</c>, the mechanism behind the
/// documented <c>IsWindowsServer()</c> helper. It compares no version number, so the version lie
/// doesn't apply.</item>
/// <item>Edition, version and update revision: the <c>CurrentVersion</c> registry values
/// <c>EditionID</c>, <c>DisplayVersion</c> and <c>UBR</c>, for the envelope and the refusal message
/// only. <c>ProductName</c> is never read: it says "Windows 10" on Windows 11.</item>
/// </list>
/// </remarks>
public static class OsInfoReader
{
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    public static OsInfo Read()
    {
        var (edition, version, updateRevision) = ReadCurrentVersion();

        return new OsInfo(
            Platform: OsPlatform.Windows,
            Edition: edition,
            Version: version,
            Build: Environment.OSVersion.Version.Build,
            UpdateRevision: updateRevision,
            Architecture: ReadArchitecture(),
            ProductType: ReadProductType(),
            Session: Reading.Known(PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_REMOTESESSION) != 0 ? SessionType.Remote : SessionType.Local));
    }

    internal static Reading<MachineArchitecture> MapMachine(ushort machine) => machine switch
    {
        0x8664 => Reading.Known(MachineArchitecture.X64, machine), // IMAGE_FILE_MACHINE_AMD64
        0xAA64 => Reading.Known(MachineArchitecture.Arm64, machine), // IMAGE_FILE_MACHINE_ARM64
        0x014C => Reading.Known(MachineArchitecture.X86, machine), // IMAGE_FILE_MACHINE_I386
        _ => Reading.Known(MachineArchitecture.Other, machine),
    };

    private static unsafe Reading<MachineArchitecture> ReadArchitecture()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10586))
        {
            return Reading.Missing<MachineArchitecture>(MissingReason.NotSupported, "IsWow64Process2 needs Windows 10 build 10586 or later");
        }

        IMAGE_FILE_MACHINE processMachine;
        IMAGE_FILE_MACHINE nativeMachine;
        if (!PInvoke.IsWow64Process2(PInvoke.GetCurrentProcess(), &processMachine, &nativeMachine))
        {
            var error = (WIN32_ERROR)Marshal.GetLastPInvokeError();
            return Reading.Missing<MachineArchitecture>(Win32Errors.Reason(error), Win32Errors.Describe("IsWow64Process2", error));
        }

        return MapMachine((ushort)nativeMachine);
    }

    private static Reading<OsProductType> ReadProductType()
    {
        var request = new OSVERSIONINFOEXW
        {
            dwOSVersionInfoSize = (uint)Marshal.SizeOf<OSVERSIONINFOEXW>(),
            wProductType = (byte)PInvoke.VER_NT_WORKSTATION,
        };
        var condition = PInvoke.VerSetConditionMask(0, VER_FLAGS.VER_PRODUCT_TYPE, (byte)PInvoke.VER_EQUAL);

        if (PInvoke.VerifyVersionInfo(ref request, VER_FLAGS.VER_PRODUCT_TYPE, condition))
        {
            return Reading.Known(OsProductType.Workstation);
        }

        var error = (WIN32_ERROR)Marshal.GetLastPInvokeError();
        return error == WIN32_ERROR.ERROR_OLD_WIN_VERSION
            ? Reading.Known(OsProductType.Server) // the product type is not VER_NT_WORKSTATION: a server or domain controller
            : Reading.Missing<OsProductType>(Win32Errors.Reason(error), Win32Errors.Describe("VerifyVersionInfoW(VER_PRODUCT_TYPE)", error));
    }

    private static (Reading<string> Edition, Reading<string> Version, Reading<int> UpdateRevision) ReadCurrentVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(CurrentVersionKey);
            if (key is null)
            {
                var missing = $@"HKLM\{CurrentVersionKey} is missing";
                return (Reading.Missing<string>(MissingReason.NotPresent, missing), Reading.Missing<string>(MissingReason.NotPresent, missing), Reading.Missing<int>(MissingReason.NotPresent, missing));
            }

            return (StringValue(key, "EditionID"), StringValue(key, "DisplayVersion"), DwordValue(key, "UBR"));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            var reason = ex is IOException ? MissingReason.SourceFailed : MissingReason.AccessDenied;
            var detail = $@"HKLM\{CurrentVersionKey}: {ex.GetType().Name}";
            return (Reading.Missing<string>(reason, detail), Reading.Missing<string>(reason, detail), Reading.Missing<int>(reason, detail));
        }
    }

    private static Reading<string> StringValue(RegistryKey key, string name) =>
        key.GetValue(name) is string { Length: > 0 } value
            ? Reading.Known(value)
            : Reading.Missing<string>(MissingReason.NotPresent, $"The registry value {name} is not set");

    private static Reading<int> DwordValue(RegistryKey key, string name) =>
        key.GetValue(name) is int value
            ? Reading.Known(value)
            : Reading.Missing<int>(MissingReason.NotPresent, string.Create(CultureInfo.InvariantCulture, $"The registry value {name} is not set as a DWORD"));
}
