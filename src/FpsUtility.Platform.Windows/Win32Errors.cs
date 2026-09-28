using System.Globalization;
using FpsUtility.Core.Raw;
using Windows.Win32.Devices.DeviceAndDriverInstallation;
using Windows.Win32.Foundation;

namespace FpsUtility.Platform.Windows;

/// <summary>
/// Maps Win32, CfgMgr32 and COM failures to a <see cref="MissingReason"/> and a detail that names the
/// call and the code. Details use the code's symbolic name, never the localised system message, so
/// they read the same on every machine.
/// </summary>
internal static class Win32Errors
{
    public static SourceUnavailableException Failed(string call, WIN32_ERROR error) =>
        new(Reason(error), Describe(call, error));

    public static MissingReason Reason(WIN32_ERROR error) => error switch
    {
        WIN32_ERROR.ERROR_ACCESS_DENIED => MissingReason.AccessDenied,
        WIN32_ERROR.ERROR_NOT_SUPPORTED or WIN32_ERROR.ERROR_CALL_NOT_IMPLEMENTED => MissingReason.NotSupported,
        _ => MissingReason.SourceFailed,
    };

    public static string Describe(string call, WIN32_ERROR error) =>
        string.Create(CultureInfo.InvariantCulture, $"{call} failed: {error} ({(uint)error})");

    public static MissingReason Reason(CONFIGRET result) => result switch
    {
        CONFIGRET.CR_ACCESS_DENIED => MissingReason.AccessDenied,
        CONFIGRET.CR_NO_SUCH_VALUE or CONFIGRET.CR_NO_SUCH_DEVNODE => MissingReason.NotPresent,
        _ => MissingReason.SourceFailed,
    };

    public static string Describe(string call, CONFIGRET result) =>
        string.Create(CultureInfo.InvariantCulture, $"{call} returned {result} ({(uint)result})");

    public static string Describe(string call, int hresult) =>
        string.Create(CultureInfo.InvariantCulture, $"{call} failed: HRESULT 0x{hresult:X8}");

    public static MissingReason ReasonForHResult(int hresult) => hresult switch
    {
        unchecked((int)0x80070005) => MissingReason.AccessDenied, // E_ACCESSDENIED
        unchecked((int)0x80004001) or unchecked((int)0x80070032) => MissingReason.NotSupported, // E_NOTIMPL, HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)
        _ => MissingReason.SourceFailed,
    };
}
