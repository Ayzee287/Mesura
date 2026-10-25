using System.Runtime.InteropServices;
using Mesura.Core.Raw;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Mesura.Platform.Windows.Collectors;

/// <summary><c>power</c>: AC line and battery presence from <c>GetSystemPowerStatus</c> (architecture §4.4).</summary>
internal static class PowerCollector
{
    public const string Source = "win32.GetSystemPowerStatus";

    private const byte AcLineOffline = 0;
    private const byte AcLineOnline = 1;
    private const byte StatusUnknown = 255;
    private const byte NoSystemBattery = 128;

    public static PowerData Collect()
    {
        if (!PInvoke.GetSystemPowerStatus(out var status))
        {
            throw Win32Errors.Failed("GetSystemPowerStatus", (WIN32_ERROR)Marshal.GetLastPInvokeError());
        }

        return new PowerData(MapAcLine(status.ACLineStatus), MapBatteryPresent(status.BatteryFlag));
    }

    internal static Reading<bool> MapAcLine(byte acLineStatus) => acLineStatus switch
    {
        AcLineOnline => Reading.Known(true, acLineStatus),
        AcLineOffline => Reading.Known(false, acLineStatus),
        StatusUnknown => Reading.Missing<bool>(MissingReason.NotSupported, "Windows reports the AC line status as unknown (255)", acLineStatus),
        _ => Reading.Missing<bool>(MissingReason.ParseFailed, $"{acLineStatus} is not a documented AC line status", acLineStatus),
    };

    /// <summary>BatteryFlag is a bit set: 128 means no system battery; 255 means the status is unknown.</summary>
    internal static Reading<bool> MapBatteryPresent(byte batteryFlag) => batteryFlag switch
    {
        StatusUnknown => Reading.Missing<bool>(MissingReason.NotSupported, "Windows reports the battery status as unknown (255)", batteryFlag),
        _ => Reading.Known((batteryFlag & NoSystemBattery) == 0, batteryFlag),
    };
}
