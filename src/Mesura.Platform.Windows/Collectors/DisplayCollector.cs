using System.Globalization;
using Mesura.Core.Raw;
using Windows.Win32;
using Windows.Win32.Devices.Display;
using Windows.Win32.Graphics.Gdi;

namespace Mesura.Platform.Windows.Collectors;

/// <summary>
/// <c>display</c>: every active path, its connection, current mode and the modes Windows offers
/// (architecture §4.4, §7). Standard user; no Windows 11-only flag.
/// </summary>
internal static class DisplayCollector
{
    public const string Source = "win32.QueryDisplayConfig, win32.DisplayConfigGetDeviceInfo, win32.EnumDisplaySettingsExW";

    /// <summary>A guard against a driver that never ends its mode list; real lists have tens to a few hundred entries.</summary>
    private const int MaxModeIndex = 100_000;

    /// <summary><c>DM_INTERLACED</c> in <c>DEVMODEW.dmDisplayFlags</c> (wingdi.h).</summary>
    private const uint DmInterlaced = 0x00000002;

    public static DisplayData Collect()
    {
        var (paths, modes) = DisplayConfig.QueryActivePaths();
        var outputs = new List<DisplayOutput>(paths.Length);

        foreach (var path in paths)
        {
            var sourceName = DisplayConfig.GetSourceName(path.sourceInfo);
            var (monitorName, monitorDevicePath) = DisplayConfig.GetTargetName(path.targetInfo);

            outputs.Add(new DisplayOutput(
                adapterId: DisplayConfig.FormatAdapterId(path.targetInfo.adapterId),
                sourceId: path.sourceInfo.id.ToString(CultureInfo.InvariantCulture),
                targetId: path.targetInfo.id.ToString(CultureInfo.InvariantCulture),
                sourceName: sourceName,
                connection: MapConnection(path.targetInfo.outputTechnology),
                desktopResolution: ReadDesktopResolution(path, modes),
                signalRefresh: ReadSignalRefresh(path, modes),
                pathRefresh: Reading.Known(new Rational(path.targetInfo.refreshRate.Numerator, path.targetInfo.refreshRate.Denominator)),
                monitorName: monitorName,
                monitorDevicePath: monitorDevicePath,
                offeredModes: sourceName.IsKnown
                    ? ReadOfferedModes(sourceName.Value!)
                    : Reading.Missing<IReadOnlyList<DisplayMode>>(sourceName.Missing!.Value, $"No source name to list modes with: {sourceName.Detail}")));
        }

        return new DisplayData(outputs);
    }

    /// <summary>One neutral kind per <c>DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY</c> code; an unnamed code maps to Other (§17.1). The code is kept as native.</summary>
    internal static Reading<ConnectionKind> MapConnection(DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY technology)
    {
        var code = unchecked((uint)technology);
        var kind = code switch
        {
            0 => ConnectionKind.Vga,
            1 => ConnectionKind.SVideo,
            2 => ConnectionKind.CompositeVideo,
            3 => ConnectionKind.ComponentVideo,
            4 => ConnectionKind.Dvi,
            5 => ConnectionKind.Hdmi,
            6 => ConnectionKind.Lvds,
            8 => ConnectionKind.DJpn,
            9 => ConnectionKind.Sdi,
            10 => ConnectionKind.DisplayPort,
            11 => ConnectionKind.DisplayPortEmbedded,
            12 => ConnectionKind.Udi,
            13 => ConnectionKind.UdiEmbedded,
            14 => ConnectionKind.SdtvDongle,
            15 => ConnectionKind.Miracast,
            16 => ConnectionKind.IndirectWired,
            17 => ConnectionKind.IndirectVirtual,
            18 => ConnectionKind.DisplayPortUsbTunnel,
            0x80000000 => ConnectionKind.Internal,
            _ => ConnectionKind.Other, // includes DISPLAYCONFIG_OUTPUT_TECHNOLOGY_OTHER (0xFFFFFFFF)
        };

        return Reading.Known(kind, code);
    }

    private static Reading<Resolution> ReadDesktopResolution(in DISPLAYCONFIG_PATH_INFO path, DISPLAYCONFIG_MODE_INFO[] modes)
    {
        var index = path.sourceInfo.modeInfoIdx;
        if (!TryGetMode(index, modes, DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE, out var mode, out var problem))
        {
            return Reading.Missing<Resolution>(problem.Reason, $"Source mode: {problem.Detail}");
        }

        return Reading.Known(new Resolution(checked((int)mode.sourceMode.width), checked((int)mode.sourceMode.height)));
    }

    private static Reading<Rational> ReadSignalRefresh(in DISPLAYCONFIG_PATH_INFO path, DISPLAYCONFIG_MODE_INFO[] modes)
    {
        var index = path.targetInfo.modeInfoIdx;
        if (!TryGetMode(index, modes, DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_TARGET, out var mode, out var problem))
        {
            return Reading.Missing<Rational>(problem.Reason, $"Target mode: {problem.Detail}");
        }

        var vSync = mode.targetMode.targetVideoSignalInfo.vSyncFreq;
        return Reading.Known(new Rational(vSync.Numerator, vSync.Denominator));
    }

    private static bool TryGetMode(
        uint index,
        DISPLAYCONFIG_MODE_INFO[] modes,
        DISPLAYCONFIG_MODE_INFO_TYPE expected,
        out DISPLAYCONFIG_MODE_INFO mode,
        out (MissingReason Reason, string Detail) problem)
    {
        mode = default;
        problem = default;

        if (index == PInvoke.DISPLAYCONFIG_PATH_MODE_IDX_INVALID)
        {
            problem = (MissingReason.NotPresent, "the path has no mode of this kind (DISPLAYCONFIG_PATH_MODE_IDX_INVALID)");
            return false;
        }

        if (index >= modes.Length)
        {
            problem = (MissingReason.ParseFailed, string.Create(CultureInfo.InvariantCulture, $"mode index {index} is outside the {modes.Length} modes returned"));
            return false;
        }

        mode = modes[index];
        if (mode.infoType != expected)
        {
            problem = (MissingReason.ParseFailed, string.Create(CultureInfo.InvariantCulture, $"mode {index} is {mode.infoType}, not {expected}"));
            return false;
        }

        return true;
    }

    private static unsafe Reading<IReadOnlyList<DisplayMode>> ReadOfferedModes(string sourceName)
    {
        var offered = new HashSet<DisplayMode>();
        var devMode = new DEVMODEW { dmSize = (ushort)sizeof(DEVMODEW) };

        for (var i = 0; i < MaxModeIndex; i++)
        {
            if (!PInvoke.EnumDisplaySettingsEx(sourceName, (ENUM_DISPLAY_SETTINGS_MODE)i, ref devMode, 0))
            {
                return offered.Count > 0
                    ? Reading.Known<IReadOnlyList<DisplayMode>>([.. offered])
                    : Reading.Missing<IReadOnlyList<DisplayMode>>(MissingReason.NotPresent, "EnumDisplaySettingsExW listed no modes for this source");
            }

            offered.Add(new DisplayMode(
                checked((int)devMode.dmPelsWidth),
                checked((int)devMode.dmPelsHeight),
                checked((int)devMode.dmDisplayFrequency),
                (devMode.dmDisplayFlags & DmInterlaced) != 0));
        }

        return Reading.Missing<IReadOnlyList<DisplayMode>>(
            MissingReason.ParseFailed,
            string.Create(CultureInfo.InvariantCulture, $"EnumDisplaySettingsExW did not end its mode list within {MaxModeIndex} entries"));
    }
}
