using System.Globalization;
using Mesura.Core.Raw;
using Windows.Win32;
using Windows.Win32.Devices.Display;
using Windows.Win32.Foundation;

namespace Mesura.Platform.Windows;

/// <summary>
/// Reads the active display paths with <c>QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS)</c> and the names
/// of their sources and targets. No Windows 11-only flag is used (architecture §17.1). Both the
/// <c>display</c> and <c>edid</c> collectors call it, so each stays independent of the other.
/// </summary>
internal static class DisplayConfig
{
    private const int MaxAttempts = 5;

    public static (DISPLAYCONFIG_PATH_INFO[] Paths, DISPLAYCONFIG_MODE_INFO[] Modes) QueryActivePaths()
    {
        const QUERY_DISPLAY_CONFIG_FLAGS flags = QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var error = PInvoke.GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount);
            if (error != WIN32_ERROR.ERROR_SUCCESS)
            {
                throw Win32Errors.Failed("GetDisplayConfigBufferSizes", error);
            }

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            error = PInvoke.QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes);

            if (error == WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER)
            {
                continue; // the topology changed between the two calls
            }

            if (error != WIN32_ERROR.ERROR_SUCCESS)
            {
                throw Win32Errors.Failed("QueryDisplayConfig", error);
            }

            return (paths[..(int)pathCount], modes[..(int)modeCount]);
        }

        throw new SourceUnavailableException(
            MissingReason.SourceFailed,
            string.Create(CultureInfo.InvariantCulture, $"QueryDisplayConfig kept returning ERROR_INSUFFICIENT_BUFFER; the display topology changed {MaxAttempts} times while it was read"));
    }

    /// <summary>The monitor's name and device path for one path's target.</summary>
    public static unsafe (Reading<string> Name, Reading<string> DevicePath) GetTargetName(in DISPLAYCONFIG_PATH_TARGET_INFO target)
    {
        var request = new DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                size = (uint)sizeof(DISPLAYCONFIG_TARGET_DEVICE_NAME),
                adapterId = target.adapterId,
                id = target.id,
            },
        };

        var error = PInvoke.DisplayConfigGetDeviceInfo(&request.header);
        if (error != 0)
        {
            var missing = Reading.Missing<string>(
                Win32Errors.Reason((WIN32_ERROR)error),
                Win32Errors.Describe("DisplayConfigGetDeviceInfo(GET_TARGET_NAME)", (WIN32_ERROR)error));
            return (missing, missing);
        }

        return (
            NonEmpty(request.monitorFriendlyDeviceName.ToString(), "DisplayConfigGetDeviceInfo(GET_TARGET_NAME) returned no monitor name"),
            NonEmpty(request.monitorDevicePath.ToString(), "DisplayConfigGetDeviceInfo(GET_TARGET_NAME) returned no monitor device path"));
    }

    /// <summary>The GDI device name of one path's source, e.g. <c>\\.\DISPLAY1</c>.</summary>
    public static unsafe Reading<string> GetSourceName(in DISPLAYCONFIG_PATH_SOURCE_INFO source)
    {
        var request = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                size = (uint)sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME),
                adapterId = source.adapterId,
                id = source.id,
            },
        };

        var error = PInvoke.DisplayConfigGetDeviceInfo(&request.header);
        return error != 0
            ? Reading.Missing<string>(
                Win32Errors.Reason((WIN32_ERROR)error),
                Win32Errors.Describe("DisplayConfigGetDeviceInfo(GET_SOURCE_NAME)", (WIN32_ERROR)error))
            : NonEmpty(request.viewGdiDeviceName.ToString(), "DisplayConfigGetDeviceInfo(GET_SOURCE_NAME) returned no device name");
    }

    /// <summary>An adapter LUID as an opaque string, identical in every section that names the adapter.</summary>
    public static string FormatAdapterId(LUID luid) =>
        string.Create(CultureInfo.InvariantCulture, $"{unchecked((uint)luid.HighPart):X8}{luid.LowPart:X8}");

    private static Reading<string> NonEmpty(string value, string whenEmpty) =>
        string.IsNullOrEmpty(value) ? Reading.Missing<string>(MissingReason.NotPresent, whenEmpty) : Reading.Known(value);
}
