using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using FpsUtility.Core.Privacy;
using FpsUtility.Core.Raw;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.SystemInformation;

namespace FpsUtility.Platform.Windows.Collectors;

/// <summary>
/// <c>smbios</c>: the SMBIOS structure table from <c>GetSystemFirmwareTable('RSMB')</c>, user mode
/// (architecture §4.4, §7). Windows' 8-byte <c>RawSMBIOSData</c> header is removed and its version
/// fields kept on their own; identifying fields are overwritten before the table leaves this class (§13).
/// </summary>
internal static class SmbiosCollector
{
    public const string Source = "win32.GetSystemFirmwareTable('RSMB')";

    private const int RawSmbiosHeaderLength = 8;
    private const int MaxAttempts = 3;

    public static SmbiosData Collect()
    {
        var raw = ReadFirmwareTable();
        if (raw.Length < RawSmbiosHeaderLength)
        {
            throw new SourceUnavailableException(MissingReason.ParseFailed, string.Create(CultureInfo.InvariantCulture, $"The RSMB table is {raw.Length} bytes, shorter than its 8-byte header"));
        }

        // RawSMBIOSData: Used20CallingMethod, SMBIOSMajorVersion, SMBIOSMinorVersion, DmiRevision (bytes), Length (DWORD), table.
        var length = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4, 4));
        if (length > (uint)(raw.Length - RawSmbiosHeaderLength))
        {
            throw new SourceUnavailableException(MissingReason.ParseFailed, string.Create(CultureInfo.InvariantCulture, $"The RSMB header declares {length} table bytes, but only {raw.Length - RawSmbiosHeaderLength} were returned"));
        }

        var scrubbed = SmbiosScrubber.Scrub(raw.AsSpan(RawSmbiosHeaderLength, (int)length));
        return new SmbiosData(
            MajorVersion: raw[1],
            MinorVersion: raw[2],
            DmiRevision: raw[3],
            Table: scrubbed.Table,
            FieldsScrubbed: scrubbed.FieldsScrubbed,
            ZeroedFromOffset: scrubbed.ZeroedFromOffset);
    }

    private static byte[] ReadFirmwareTable()
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var size = PInvoke.GetSystemFirmwareTable(FIRMWARE_TABLE_PROVIDER.RSMB, 0, default);
            if (size == 0)
            {
                throw Win32Errors.Failed("GetSystemFirmwareTable(RSMB) size query", (WIN32_ERROR)Marshal.GetLastPInvokeError());
            }

            var buffer = new byte[size];
            var written = PInvoke.GetSystemFirmwareTable(FIRMWARE_TABLE_PROVIDER.RSMB, 0, buffer);
            if (written == 0)
            {
                throw Win32Errors.Failed("GetSystemFirmwareTable(RSMB)", (WIN32_ERROR)Marshal.GetLastPInvokeError());
            }

            if (written <= size)
            {
                return buffer[..(int)written];
            }

            // The table grew between the calls; the return value is the size now needed.
        }

        throw new SourceUnavailableException(MissingReason.SourceFailed, "GetSystemFirmwareTable(RSMB) kept reporting a larger table than it had just reported");
    }
}
