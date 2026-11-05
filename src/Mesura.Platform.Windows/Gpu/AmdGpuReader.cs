using Mesura.Platform.Windows.Gpu.Adlx;

namespace Mesura.Platform.Windows.Gpu;

/// <summary>Whether Mesura could talk to AMD's ADLX on this PC, and if not, why.</summary>
public enum AmdAvailability
{
    /// <summary>ADLX answered.</summary>
    Available,

    /// <summary>No <c>amdadlx64.dll</c> in the system folder: no AMD driver with ADLX (an NVIDIA or Intel-only PC, or an old AMD driver).</summary>
    NoDriver,

    /// <summary>The DLL is present but could not be loaded or lacks a required entry point.</summary>
    Unusable,

    /// <summary>The DLL is present and loads, but ADLX would not start (version refused, or the driver could not initialise it).</summary>
    NotStarted,
}

/// <summary>A feature as ADLX reports it: <c>null</c> where the call failed or the feature is not offered, with the reason in <see cref="Problem"/>.</summary>
public sealed record AmdFeature(bool? Supported, bool? Enabled, string? Problem)
{
    public static readonly AmdFeature Unknown = new(null, null, null);
}

/// <summary>One reading of the GPU's metrics (each value is <c>null</c> where ADLX did not give it).</summary>
public sealed record AmdMetrics(double? UsagePercent, int? ClockMhz, int? MemoryClockMhz, double? TemperatureC, double? HotspotC, double? PowerW, double? BoardPowerW, int? FanRpm, int? VoltageMv);

/// <summary>Which kinds of GPU tuning ADLX says exist for the GPU, and whether it is at factory settings. Only whether they exist: no tuning value is read or set here.</summary>
public sealed record AmdTuningSupport(bool? Auto, bool? Preset, bool? ManualGfx, bool? ManualVram, bool? ManualFan, bool? ManualPower, bool? AtFactory);

/// <summary>Tessellation as ADLX reports it: the mode (AMD optimized, use application settings, override) and the level (1 = off, 2 to 64).</summary>
public sealed record AmdTessellation(bool? Supported, string? Mode, int? Level);

/// <summary>
/// One display ADLX knows, with what the driver says about it. The EDID is never read (it holds the monitor's serial number); the name is the model the driver shows. Every value the driver did not give is <c>null</c>.
/// </summary>
public sealed record AmdDisplayReport(
    int Index,
    string? Name,
    uint? ManufacturerId,
    string? Type,
    string? Connector,
    int? NativeWidth,
    int? NativeHeight,
    double? RefreshHz,
    string? ScanType,
    int? GpuUniqueId,
    AmdFeature FreeSync,
    AmdFeature VirtualSuperResolution,
    AmdFeature GpuScaling,
    AmdFeature IntegerScaling,
    string? ScalingMode,
    string? ColorDepth,
    string? PixelFormat,
    IReadOnlyList<string> Problems);

/// <summary>What ADLX reports about one Radeon GPU. Every value Windows or the driver did not give is <c>null</c>, never a default.</summary>
public sealed record AmdGpuReport(
    int Index,
    string? Name,
    string? VendorId,
    string? DeviceId,
    string? RevisionId,
    string? SubSystemId,
    string? PnpString,
    uint? VramMb,
    string? VramType,
    string? BiosVersion,
    int? GpuType,
    bool? External,
    int? UniqueId,
    AmdFeature AntiLag,
    int? AntiLagLevel,
    AmdFeature Chill,
    AmdFeature Boost,
    AmdFeature ImageSharpening,
    AmdFeature EnhancedSync,
    AmdFeature FrameRateTarget,
    AmdFeature SmartAccessMemory,
    AmdTuningSupport? Tuning,
    AmdMetrics? Metrics,
    AmdFeature VerticalRefresh,
    string? VerticalRefreshMode,
    AmdFeature RadeonSuperResolution,
    int? SuperResolutionSharpness,
    AmdTessellation? Tessellation,
    IReadOnlyList<string> Problems);

/// <summary>The whole reading: whether ADLX was there, which versions, and each GPU.</summary>
public sealed record AmdAdlxReport(AmdAvailability Availability, string? DllVersion, string? AskedVersion, IReadOnlyList<AmdGpuReport> Gpus, string? Failure, string? HybridGraphics, IReadOnlyList<AmdDisplayReport> Displays);

/// <summary>
/// Reads what AMD's driver says about the Radeon GPUs through ADLX, in this process, by calling the objects of <c>amdadlx64.dll</c> directly (no helper program): the GPUs, the 3D features
/// each supports and has on, which kinds of tuning exist, SmartAccess Memory, and one metrics reading. Read only: no setter is ever called. Every call is checked: an answer that is not success
/// is recorded as "unknown" with the result's name and the rest is still read; the objects are released in order and ADLX is terminated even when a step fails.
/// <para>
/// A crash inside AMD's DLL (an access violation) cannot be caught by managed code and ends the process: a caller that must survive one (the window) runs this in a child process
/// (<c>mesura gpu amd --json</c>), which is what that command is for.
/// </para>
/// </summary>
public static class AmdGpuReader
{
    /// <summary>Reads this PC through the real <c>amdadlx64.dll</c>.</summary>
    public static AmdAdlxReport Read()
    {
        var entry = AdlxEntryPoints.LoadSystemLibrary(out var failure);
        return entry is null ? Unavailable(failure) : Read(entry);
    }

    internal static AmdAdlxReport Read(AdlxEntryPoints entry)
    {
        using var session = AdlxSession.TryOpen(entry, out var failure, out var result);
        if (session is null)
        {
            return new(failure == AdlxOpenFailure.InitializeFailed ? AmdAvailability.NotStarted : Availability(failure), null, null, [], failure == AdlxOpenFailure.InitializeFailed ? $"ADLX answered {result}." : failure.ToString(), null, []);
        }

        var gpus = new List<AmdGpuReport>();
        var displays = new List<AmdDisplayReport>();
        string? problem = null;
        string? hybrid = null;
        try
        {
            var hybridReading = new AdlxSystem(session).HybridGraphics();
            hybrid = hybridReading.Ok ? HybridName(hybridReading.Value) : null;
            var list = new AdlxSystem(session).Gpus();
            if (list.Value is null)
            {
                problem = $"The GPU list couldn't be read: {list.Result}.";
            }
            else
            {
                using var gpuList = list.Value;
                using var services3d = new AdlxSystem(session).Services3D().Value;
                using var tuning = new AdlxSystem(session).Tuning().Value;
                using var performance = new AdlxSystem(session).Performance().Value;
                for (uint i = 0; i < gpuList.Size; i++)
                {
                    var one = gpuList.At(i);
                    if (one.Value is null)
                    {
                        gpus.Add(Missing((int)i, one.Result));
                        continue;
                    }

                    using var gpu = one.Value;
                    gpus.Add(ReadGpu((int)i, gpu, services3d, tuning, performance));
                }

                displays.AddRange(ReadDisplays(new AdlxSystem(session)));
            }
        }
        catch (ObjectDisposedException e)
        {
            problem = "ADLX was closed while it was being read: " + e.Message;
        }

        return new(AmdAvailability.Available, session.DllVersion.ToString(), session.AskedVersion.ToString(), gpus, problem, hybrid, displays);
    }

    private static AmdAdlxReport Unavailable(AdlxOpenFailure failure) => new(Availability(failure), null, null, [], failure.ToString(), null, []);

    private static AmdAvailability Availability(AdlxOpenFailure failure) => failure switch
    {
        AdlxOpenFailure.DllMissing => AmdAvailability.NoDriver,
        AdlxOpenFailure.DllUnusable => AmdAvailability.Unusable,
        _ => AmdAvailability.NotStarted,
    };

    private static AmdGpuReport Missing(int index, AdlxResult why) => new(index, null, null, null, null, null, null, null, null, null, null, null, null,
        AmdFeature.Unknown, null, AmdFeature.Unknown, AmdFeature.Unknown, AmdFeature.Unknown, AmdFeature.Unknown, AmdFeature.Unknown, AmdFeature.Unknown, null, null,
        AmdFeature.Unknown, null, AmdFeature.Unknown, null, null, [$"The GPU couldn't be obtained: {why}."]);

    private static AmdGpuReport ReadGpu(int index, AdlxGpu gpu, Adlx3DServices? services3d, AdlxTuningServices? tuning, AdlxPerformanceServices? performance)
    {
        var problems = new List<string>();
        T? Get<T>(AdlxReading<T> reading, string what)
            where T : struct
        {
            if (!reading.Ok)
            {
                problems.Add($"{what}: {reading.Result}");
            }

            return reading.Known();
        }

        string? GetText(AdlxReading<string?> reading, string what)
        {
            if (!reading.Ok)
            {
                problems.Add($"{what}: {reading.Result}");
            }

            return reading.KnownText();
        }

        var bios = gpu.BiosInfo();
        if (!bios.Ok)
        {
            problems.Add($"BIOS: {bios.Result}");
        }

        AmdFeature Feature(Func<AdlxGpu, AdlxReading<AdlxToggle>>? obtain, string what, out int? level, bool wantLevel = false)
        {
            level = null;
            if (obtain is null)
            {
                problems.Add($"{what}: services unavailable");
                return new(null, null, "services unavailable");
            }

            var found = obtain(gpu);
            if (found.Value is null)
            {
                // The driver doesn't offer the feature at all (NotSupported or an unknown interface): an answer, recorded as such.
                return new(null, null, found.Result.ToString());
            }

            using var toggle = found.Value;
            var supportedResult = toggle.IsSupported();
            var supported = supportedResult.Known();
            if (supported is not true)
            {
                return new(supported, null, supportedResult.Ok ? null : $"IsSupported: {supportedResult.Result}");
            }

            // Only a supported feature is asked whether it is on; a failed answer is unknown, never "off".
            var enabled = toggle.IsEnabled();
            if (wantLevel)
            {
                level = toggle.AntiLagLevel().Known();
            }

            return new(true, enabled.Known(), enabled.Ok ? null : $"IsEnabled: {enabled.Result}");
        }

        var antiLag = Feature(services3d is null ? null : services3d.AntiLag, "Anti-Lag", out var antiLagLevel, wantLevel: true);
        var chill = Feature(services3d is null ? null : services3d.Chill, "Chill", out _);
        var boost = Feature(services3d is null ? null : services3d.Boost, "Boost", out _);
        var sharpening = Feature(services3d is null ? null : services3d.ImageSharpening, "Image Sharpening", out _);
        var enhanced = Feature(services3d is null ? null : services3d.EnhancedSync, "Enhanced Sync", out _);
        var frameRate = Feature(services3d is null ? null : services3d.FrameRateTargetControl, "Frame rate target", out _);
        var sam = Feature(tuning is null ? null : tuning.SmartAccessMemory, "SmartAccess Memory", out _);

        AmdTuningSupport? support = tuning is null ? null : new(
            Get(tuning.AutoTuning(gpu), "Auto tuning"), Get(tuning.PresetTuning(gpu), "Preset tuning"), Get(tuning.ManualGfxTuning(gpu), "Manual GFX tuning"),
            Get(tuning.ManualVramTuning(gpu), "Manual VRAM tuning"), Get(tuning.ManualFanTuning(gpu), "Manual fan tuning"), Get(tuning.ManualPowerTuning(gpu), "Manual power tuning"),
            Get(tuning.IsAtFactory(gpu), "At factory"));

        AmdMetrics? metrics = null;
        if (performance is not null)
        {
            var found = performance.CurrentMetrics(gpu);
            if (found.Value is null)
            {
                problems.Add($"Metrics: {found.Result}");
            }
            else
            {
                using var m = found.Value;
                metrics = new(Get(m.UsagePercent(), "usage"), Get(m.ClockMhz(), "clock"), Get(m.MemoryClockMhz(), "memory clock"), Get(m.TemperatureC(), "temperature"),
                    Get(m.HotspotC(), "hotspot"), Get(m.PowerW(), "power"), Get(m.BoardPowerW(), "board power"), Get(m.FanRpm(), "fan"), Get(m.VoltageMv(), "voltage"));
            }
        }

        // V-Sync (with its mode), Radeon Super Resolution (with its sharpness) and tessellation: read only, each asked for and recorded like the others.
        var vsync = Feature(services3d is null ? null : services3d.WaitForVerticalRefresh, "Wait for vertical refresh", out _);
        string? vsyncMode = null;
        if (vsync.Supported is true && services3d is not null && services3d.WaitForVerticalRefresh(gpu) is { Value: { } vsyncObject })
        {
            using (vsyncObject)
            {
                var mode = vsyncObject.Int(5);
                vsyncMode = mode.Ok ? VsyncModeName(mode.Value) : null;
                if (!mode.Ok)
                {
                    problems.Add($"V-Sync mode: {mode.Result}");
                }
            }
        }

        var superResolution = Feature(services3d is null ? null : _ => services3d.RadeonSuperResolution(), "Radeon Super Resolution", out _);
        int? sharpness = null;
        if (superResolution.Supported is true && services3d is not null && services3d.RadeonSuperResolution() is { Value: { } rsrObject })
        {
            using (rsrObject)
            {
                sharpness = rsrObject.Int(7).Known();
            }
        }

        AmdTessellation? tessellation = null;
        if (services3d is not null)
        {
            var found = services3d.Tessellation(gpu);
            if (found.Value is null)
            {
                tessellation = new(null, null, null);
                problems.Add($"Tessellation: {found.Result}");
            }
            else
            {
                using var t = found.Value;
                var supported = t.IsSupported().Known();
                tessellation = supported is true ? new(true, TessellationModeName(t.Value().Known()), t.SecondValue().Known()) : new(supported, null, null);
            }
        }

        return new(index, GetText(gpu.Name(), "Name"), GetText(gpu.VendorId(), "VendorId"), GetText(gpu.DeviceId(), "DeviceId"), GetText(gpu.RevisionId(), "RevisionId"), GetText(gpu.SubSystemId(), "SubSystemId"),
            GetText(gpu.PnpString(), "PNP string"), Get(gpu.TotalVramMb(), "VRAM"), GetText(gpu.VramType(), "VRAM type"), bios.Ok ? bios.Value.Version : null, Get(gpu.GpuType(), "GPU type"),
            Get(gpu.IsExternal(), "IsExternal"), Get(gpu.UniqueId(), "UniqueId"), antiLag, antiLagLevel, chill, boost, sharpening, enhanced, frameRate, sam, support, metrics,
            vsync, vsyncMode, superResolution, sharpness, tessellation, problems);
    }

    /// <summary>Every display ADLX lists, with the display features read for each. Read only; the EDID is not read.</summary>
    private static IEnumerable<AmdDisplayReport> ReadDisplays(AdlxSystem system)
    {
        var services = system.Displays();
        if (services.Value is null)
        {
            return [new AmdDisplayReport(-1, null, null, null, null, null, null, null, null, null, AmdFeature.Unknown, AmdFeature.Unknown, AmdFeature.Unknown, AmdFeature.Unknown, null, null, null,
                [$"The display services couldn't be obtained: {services.Result}."])];
        }

        var result = new List<AmdDisplayReport>();
        using var displayServices = services.Value;
        var listed = displayServices.Displays();
        if (listed.Value is null)
        {
            return [new AmdDisplayReport(-1, null, null, null, null, null, null, null, null, null, AmdFeature.Unknown, AmdFeature.Unknown, AmdFeature.Unknown, AmdFeature.Unknown, null, null, null,
                [$"The display list couldn't be read: {listed.Result}."])];
        }

        using var list = listed.Value;
        for (uint i = 0; i < list.Size; i++)
        {
            var one = list.At(i);
            if (one.Value is null)
            {
                result.Add(new AmdDisplayReport((int)i, null, null, null, null, null, null, null, null, null, AmdFeature.Unknown, AmdFeature.Unknown, AmdFeature.Unknown, AmdFeature.Unknown, null, null, null,
                    [$"The display couldn't be obtained: {one.Result}."]));
                continue;
            }

            using var display = one.Value;
            result.Add(ReadDisplay((int)i, display, displayServices));
        }

        return result;
    }

    private static AmdDisplayReport ReadDisplay(int index, AdlxDisplay display, AdlxDisplayServices services)
    {
        var problems = new List<string>();
        T? Get<T>(AdlxReading<T> reading, string what)
            where T : struct
        {
            if (!reading.Ok)
            {
                problems.Add($"{what}: {reading.Result}");
            }

            return reading.Known();
        }

        AmdFeature Toggle(AdlxReading<AdlxToggle> found, string what)
        {
            if (found.Value is null)
            {
                return new(null, null, found.Result.ToString());
            }

            using var toggle = found.Value;
            var supportedResult = toggle.IsSupported();
            var supported = supportedResult.Known();
            if (supported is not true)
            {
                return new(supported, null, supportedResult.Ok ? null : $"IsSupported: {supportedResult.Result}");
            }

            var enabled = toggle.IsEnabled();
            return new(true, enabled.Known(), enabled.Ok ? null : $"IsEnabled: {enabled.Result}");
        }

        string? Value(AdlxReading<AdlxValueFeature> found, string what, Func<int, string> name)
        {
            if (found.Value is null)
            {
                problems.Add($"{what}: {found.Result}");
                return null;
            }

            using var feature = found.Value;
            var supported = feature.IsSupported();
            if (!supported.Ok || !supported.Value)
            {
                return supported.Ok ? "not supported" : null;
            }

            var value = feature.Value();
            if (!value.Ok)
            {
                problems.Add($"{what}: {value.Result}");
                return null;
            }

            return name(value.Value);
        }

        var resolution = display.NativeResolution();
        if (!resolution.Ok)
        {
            problems.Add($"Native resolution: {resolution.Result}");
        }

        string? displayName = display.Name() is { Ok: true } n ? n.Value : null;
        return new(index, displayName, Get(display.ManufacturerId(), "Manufacturer id"), display.DisplayType() is { Ok: true } t ? DisplayTypeName(t.Value) : null,
            display.ConnectorType() is { Ok: true } c ? ConnectorName(c.Value) : null, resolution.Ok ? resolution.Value.Width : null, resolution.Ok ? resolution.Value.Height : null,
            Get(display.RefreshRate(), "Refresh rate"), display.ScanType() is { Ok: true } s ? (s.Value == 0 ? "progressive" : s.Value == 1 ? "interlaced" : $"unknown ({s.Value})") : null,
            Get(display.GpuUniqueId(), "GPU of the display"),
            Toggle(services.FreeSync(display), "FreeSync"), Toggle(services.VirtualSuperResolution(display), "Virtual Super Resolution"), Toggle(services.GpuScaling(display), "GPU scaling"),
            Toggle(services.IntegerScaling(display), "Integer scaling"),
            Value(services.ScalingMode(display), "Scaling mode", ScalingModeName), Value(services.ColorDepth(display), "Color depth", ColorDepthName), Value(services.PixelFormat(display), "Pixel format", PixelFormatName),
            problems);
    }

    // The names of ADLX's enumerations (ADLXDefines.h of SDK 1.5). A value outside the list is shown as unknown with its number, never mapped to the nearest name.
    private static string Named(string[] names, int value) => value >= 0 && value < names.Length ? names[value] : $"unknown ({value})";

    private static string HybridName(int value) => Named(["none", "AMD hybrid", "other hybrid"], value);

    private static string VsyncModeName(int value) => Named(["always off", "off unless the application asks", "on unless the application asks", "always on"], value);

    private static string? TessellationModeName(int? value) => value is null ? null : Named(["AMD optimized", "use application settings", "override application settings"], value.Value);

    private static string DisplayTypeName(int value) => Named(["unknown", "monitor", "television", "LCD panel", "digital flat panel", "component video", "projector"], value);

    private static string ConnectorName(int value) => Named(["unknown", "VGA", "DVI-D", "DVI-I", "CV dongle NTSC", "CV dongle JPN", "CV dongle non-I2C JPN", "CV dongle non-I2C NTSC", "proprietary", "HDMI type A", "HDMI type B", "S-Video", "composite", "RCA 3-component", "DisplayPort", "eDP", "wireless display", "USB type C"], value);

    private static string ScalingModeName(int value) => Named(["preserve aspect ratio", "full panel", "centered"], value);

    private static string ColorDepthName(int value) => Named(["unknown", "6 bpc", "8 bpc", "10 bpc", "12 bpc", "14 bpc", "16 bpc"], value);

    private static string PixelFormatName(int value) => Named(["unknown", "RGB 4:4:4 full", "YCbCr 4:4:4", "YCbCr 4:2:2", "RGB 4:4:4 limited", "YCbCr 4:2:0"], value);
}
