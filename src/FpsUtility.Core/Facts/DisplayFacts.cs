using System.Text.Json.Serialization;
using FpsUtility.Core.Raw;
using FpsUtility.Core.Raw.Serialization;

namespace FpsUtility.Core.Facts;

/// <summary>
/// One active display output: a desktop driven to a monitor input (raw <c>display</c>, joined with <c>edid</c> and
/// <c>adapters</c>). There is deliberately no maximum-refresh fact: reading the monitor's capabilities from its EDID
/// waits for the EDID parser and its corpus (§16 step 4).
/// </summary>
/// <param name="Id">Snapshot-scoped, in the raw snapshot's order of outputs.</param>
/// <param name="Gpu">The <see cref="GpuFacts.Id"/> of the adapter driving the output.</param>
/// <param name="SharesSourceWith">
/// The other active outputs showing the same desktop source (duplicate or "clone" mode), as their <see cref="Id"/>s
/// (<see cref="Rules.DisplaySharedSource"/>). A known empty list: the output shows its source alone. Outputs that share
/// a source share its desktop resolution and its offered modes.
/// </param>
/// <param name="SignalRefresh">The vertical refresh of the signal sent to the monitor, exactly as a rational in Hz.</param>
/// <param name="PathRefresh">The rate recorded on the path itself; it can differ from the signal's (dynamic refresh, §17.1).</param>
/// <param name="OfferedModes">The modes the platform offers the output right now, in whole Hz as listed, less entries that aren't modes (<see cref="Rules.DisplayModes"/>).</param>
/// <param name="MonitorName">The monitor's name as the platform reports it.</param>
/// <param name="Monitor">The monitor's model identity from its EDID, read only from a base block that was intact as stored: never a unit identifier (§13 scrubs those).</param>
/// <param name="EdidOverride">Whether an INF EDID override is installed, so the platform uses other EDID blocks than the monitor's.</param>
public sealed record DisplayFacts(
    string Id,
    Fact<string> Gpu,
    Fact<IReadOnlyList<string>> SharesSourceWith,
    Fact<ConnectionKind> Connection,
    Fact<PixelSize> DesktopResolution,
    Fact<RefreshRate> SignalRefresh,
    Fact<RefreshRate> PathRefresh,
    Fact<IReadOnlyList<VideoMode>> OfferedModes,
    Fact<string> MonitorName,
    Fact<MonitorIdentity> Monitor,
    Fact<bool> EdidOverride);

/// <summary>A size in pixels; both sides positive.</summary>
public sealed record PixelSize
{
    public PixelSize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }
}

/// <summary>A refresh rate in Hz as an exact rational: numerator over denominator, both positive, never rounded.</summary>
public sealed record RefreshRate
{
    public RefreshRate(uint numerator, uint denominator)
    {
        ArgumentOutOfRangeException.ThrowIfZero(numerator);
        ArgumentOutOfRangeException.ThrowIfZero(denominator);
        Numerator = numerator;
        Denominator = denominator;
    }

    public uint Numerator { get; }

    public uint Denominator { get; }
}

/// <summary>A display mode as the platform lists it; <see cref="RefreshHz"/> is whole Hz. Width, height and refresh are positive.</summary>
public sealed record VideoMode
{
    public VideoMode(int width, int height, int refreshHz, bool interlaced)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(refreshHz);
        Width = width;
        Height = height;
        RefreshHz = refreshHz;
        Interlaced = interlaced;
    }

    public int Width { get; }

    public int Height { get; }

    public int RefreshHz { get; }

    public bool Interlaced { get; }
}

/// <param name="ManufacturerId">The three-letter PNP manufacturer ID, e.g. <c>BOE</c>.</param>
/// <param name="ProductCode">The manufacturer's product code.</param>
public sealed record MonitorIdentity(string ManufacturerId, int ProductCode);
