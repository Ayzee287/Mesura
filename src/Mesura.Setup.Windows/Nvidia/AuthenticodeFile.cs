using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Mesura.Setup.Windows.Nvidia;

/// <summary>What Windows says about a file's Authenticode signature.</summary>
/// <param name="Valid">The signature is present, the file matches it and the chain is trusted (WinVerifyTrust).</param>
/// <param name="Subject">The signer's certificate subject, or empty when there is none.</param>
/// <param name="Detail">The Windows answer in words, for a message.</param>
public sealed record AuthenticodeResult(bool Valid, string Subject, string Detail);

/// <summary>
/// Verifies a file's Authenticode signature with <c>WinVerifyTrust</c> (read only: no user interface, no revocation lookup, no network), the check Windows itself
/// makes for a signed file; the signer is read separately so a caller can insist on a publisher. A valid signature by anyone is not enough to run a driver installer.
/// </summary>
public static class AuthenticodeFile
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);

    public static AuthenticodeResult Verify(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = new WinTrustFileInfo { StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(), FilePath = Marshal.StringToCoTaskMemUni(path) };
        var fileMemory = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(file, fileMemory, false);
            var data = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2, // WTD_UI_NONE
                RevocationChecks = 0, // WTD_REVOKE_NONE: no network
                UnionChoice = 1, // WTD_CHOICE_FILE
                FileInfo = fileMemory,
                StateAction = 0, // WTD_STATEACTION_IGNORE
                ProvFlags = 0x00000010, // WTD_REVOCATION_CHECK_NONE: nothing is fetched from the network
            };
            var action = GenericVerifyV2;
            var result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return new AuthenticodeResult(result == 0, Subject(path), Describe(result));
        }
        finally
        {
            Marshal.FreeCoTaskMem(file.FilePath);
            Marshal.FreeHGlobal(fileMemory);
        }
    }

    private static string Subject(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // The signer of a signed file is read from the file; the trust decision is WinVerifyTrust's, above.
            return X509Certificate.CreateFromSignedFile(path).Subject;
#pragma warning restore SYSLIB0057
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static string Describe(int result) => unchecked((uint)result) switch
    {
        0 => "the signature is valid",
        0x800B0100 => "the file is not signed",
        0x800B0109 => "the signature's chain ends in a certificate that isn't trusted",
        0x80096010 => "the file does not match its signature (it was changed)",
        0x800B0101 => "the signing certificate has expired",
        var other => $"Windows could not verify the signature (0x{other:X8})",
    };
}
