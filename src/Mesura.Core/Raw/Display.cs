using System.Text.Json.Serialization;
using Mesura.Core.Raw.Serialization;

namespace Mesura.Core.Raw;

/// <summary>The <c>display</c> section: every active output (architecture §4.4).</summary>
public sealed record DisplayData
{
    [JsonConstructor]
    public DisplayData(IReadOnlyList<DisplayOutput> outputs)
    {
        // Boot-stable keys first; the adapter LUID changes on reboot, so it only breaks ties.
        Outputs = Canonical.Sort(outputs, static (a, b) =>
        {
            var order = Canonical.Ordinal(a.MonitorDevicePath.Value, b.MonitorDevicePath.Value);
            order = order != 0 ? order : Canonical.Ordinal(a.TargetId, b.TargetId);
            order = order != 0 ? order : Canonical.Ordinal(a.SourceId, b.SourceId);
            return order != 0 ? order : Canonical.Ordinal(a.AdapterId, b.AdapterId);
        });
    }

    public IReadOnlyList<DisplayOutput> Outputs { get; }
}

/// <summary>One active path from a display source (a desktop) to a target (a monitor input).</summary>
public sealed record DisplayOutput
{
    [JsonConstructor]
    public DisplayOutput(
        string adapterId,
        string sourceId,
        string targetId,
        Reading<string> sourceName,
        Reading<ConnectionKind> connection,
        Reading<Resolution> desktopResolution,
        Reading<Rational> signalRefresh,
        Reading<Rational> pathRefresh,
        Reading<string> monitorName,
        Reading<string> monitorDevicePath,
        Reading<IReadOnlyList<DisplayMode>> offeredModes)
    {
        AdapterId = adapterId;
        SourceId = sourceId;
        TargetId = targetId;
        SourceName = sourceName;
        Connection = connection;
        DesktopResolution = desktopResolution;
        SignalRefresh = signalRefresh;
        PathRefresh = pathRefresh;
        MonitorName = monitorName;
        MonitorDevicePath = monitorDevicePath;
        OfferedModes = Canonical.Sort(offeredModes, static (a, b) =>
        {
            var order = a.Width.CompareTo(b.Width);
            order = order != 0 ? order : a.Height.CompareTo(b.Height);
            order = order != 0 ? order : a.RefreshHz.CompareTo(b.RefreshHz);
            return order != 0 ? order : a.Interlaced.CompareTo(b.Interlaced);
        });
    }

    /// <summary>Opaque id of the graphics adapter driving this output; joins <c>adapters</c> (§4.4 rule 3).</summary>
    public string AdapterId { get; }

    /// <summary>Opaque id of the display source on that adapter.</summary>
    public string SourceId { get; }

    /// <summary>Opaque id of the target (monitor input) on that adapter.</summary>
    public string TargetId { get; }

    /// <summary>Opaque name the platform gives the source; the offered modes were read with it.</summary>
    public Reading<string> SourceName { get; }

    /// <summary>The connection kind. Native: the platform's output-technology code.</summary>
    public Reading<ConnectionKind> Connection { get; }

    /// <summary>The desktop resolution of the source, in pixels.</summary>
    public Reading<Resolution> DesktopResolution { get; }

    /// <summary>The vertical refresh of the video signal sent to the monitor, in Hz, as a rational.</summary>
    public Reading<Rational> SignalRefresh { get; }

    /// <summary>
    /// The refresh the platform records on the path itself, in Hz, as a rational. It can differ from
    /// <see cref="SignalRefresh"/> (for example a virtual rate under Windows 11 dynamic refresh; verify, §17.1).
    /// </summary>
    public Reading<Rational> PathRefresh { get; }

    /// <summary>The monitor's name as the platform reports it (usually from its EDID).</summary>
    public Reading<string> MonitorName { get; }

    /// <summary>Opaque monitor device path; joins the <c>edid</c> section.</summary>
    public Reading<string> MonitorDevicePath { get; }

    /// <summary>
    /// The distinct modes the platform offers for this source right now (width, height, refresh,
    /// interlaced). Bit depth and fixed-output variants are not kept.
    /// </summary>
    public Reading<IReadOnlyList<DisplayMode>> OfferedModes { get; }
}

/// <summary>Connection kinds, one per platform output technology, so the mapping is lossless.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<ConnectionKind>))]
public enum ConnectionKind
{
    /// <summary>Reported as "other", or a code the collector doesn't name (the native code is kept).</summary>
    Other,
    Vga,
    SVideo,
    CompositeVideo,
    ComponentVideo,
    Dvi,
    Hdmi,
    Lvds,
    DJpn,
    Sdi,
    DisplayPort,

    /// <summary>Embedded DisplayPort (eDP), a built-in panel.</summary>
    DisplayPortEmbedded,
    Udi,
    UdiEmbedded,
    SdtvDongle,
    Miracast,
    IndirectWired,
    IndirectVirtual,
    DisplayPortUsbTunnel,

    /// <summary>A built-in connection the platform reports only as internal.</summary>
    Internal,
}

public sealed record Resolution(int Width, int Height);

/// <summary>A rate as numerator / denominator, exactly as reported (the denominator may be 0 if the platform left it unset).</summary>
public sealed record Rational(uint Numerator, uint Denominator);

/// <param name="RefreshHz">The refresh as the platform lists it for this mode, in whole Hz.</param>
public sealed record DisplayMode(int Width, int Height, int RefreshHz, bool Interlaced);
