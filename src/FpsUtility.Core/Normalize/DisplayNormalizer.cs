using System.Globalization;
using FpsUtility.Core.Facts;
using FpsUtility.Core.Raw;

namespace FpsUtility.Core.Normalize;

/// <summary>
/// Display outputs, joined to their GPU by opaque adapter id and to their monitor's EDID by monitor device path,
/// both within one snapshot (§5). From the EDID only the model identity is read; capabilities wait for step 4.
/// </summary>
internal static class DisplayNormalizer
{
    private const string Outputs = "/sources/display/data/outputs";
    private const string Monitors = "/sources/edid/data/monitors";

    public static Fact<IReadOnlyList<DisplayFacts>> Displays(SnapshotSources sources)
    {
        if (!Evidence.TryData(sources.Display, "display", out var display, out var unavailable))
        {
            return unavailable.Fact<IReadOnlyList<DisplayFacts>>();
        }

        var displays = display.Outputs.Select((output, index) =>
        {
            var at = $"{Outputs}/{index}";
            var edid = Edid(output, at, sources.Edid);

            return new DisplayFacts(
                Id: DisplayId(index),
                Gpu: Gpu(output, at, sources.Adapters),
                SharesSourceWith: SharedSource(display.Outputs, index),
                Connection: Evidence.Observed(output.Connection, Basis.Authoritative, $"{at}/connection"),
                DesktopResolution: Evidence.Map(
                    Evidence.Plausible(
                        Evidence.Observed(output.DesktopResolution, Basis.Authoritative, $"{at}/desktopResolution"),
                        static size => size.Width > 0 && size.Height > 0,
                        static size => string.Create(CultureInfo.InvariantCulture, $"{size.Width} × {size.Height} is not a desktop size")),
                    static size => new PixelSize(size.Width, size.Height)),
                SignalRefresh: Refresh(Evidence.Observed(output.SignalRefresh, Basis.Authoritative, $"{at}/signalRefresh")),
                PathRefresh: Refresh(Evidence.Observed(output.PathRefresh, Basis.Authoritative, $"{at}/pathRefresh")),
                OfferedModes: Modes(Evidence.Observed(output.OfferedModes, Basis.Authoritative, $"{at}/offeredModes")),
                MonitorName: Evidence.Observed(output.MonitorName, Basis.Authoritative, $"{at}/monitorName"),
                Monitor: edid.Identity,
                EdidOverride: edid.Override);
        });

        return Fact.Observed<IReadOnlyList<DisplayFacts>>([.. displays], Basis.Authoritative, Outputs);
    }

    /// <summary>
    /// <see cref="Rules.DisplaySharedSource"/>: the other outputs showing this output's desktop source. A source id is
    /// scoped to its adapter, so both ids must match. Only the outputs' display ids enter the fact, never the opaque ids.
    /// </summary>
    private static Fact<IReadOnlyList<string>> SharedSource(IReadOnlyList<DisplayOutput> outputs, int index)
    {
        var output = outputs[index];
        var own = Pointers(index, "adapterId", "sourceId");
        if (IsBlank(output.AdapterId) || IsBlank(output.SourceId))
        {
            return Fact.Missing<IReadOnlyList<string>>(FactGap.Implausible, "The output's adapter or source id is empty, so the outputs showing its source can't be established", own);
        }

        var others = Enumerable.Range(0, outputs.Count).Where(other => other != index).ToList();
        var unplaced = others
            .Where(other => IsBlank(outputs[other].AdapterId) || (outputs[other].AdapterId == output.AdapterId && IsBlank(outputs[other].SourceId)))
            .ToList();
        if (unplaced.Count > 0)
        {
            return Fact.Missing<IReadOnlyList<string>>(
                FactGap.Ambiguous,
                string.Create(CultureInfo.InvariantCulture, $"{unplaced.Count} other output(s) that may be on this adapter have no source id, so they may show this source"),
                [.. own, .. unplaced.SelectMany(other => Pointers(other, "adapterId", "sourceId"))]);
        }

        var sharing = others
            .Where(other => outputs[other].AdapterId == output.AdapterId && outputs[other].SourceId == output.SourceId)
            .ToList();

        // A target listed twice on one adapter is one output or two: the data can't say, so neither can the count.
        var group = sharing.Append(index).Select(member => (outputs[member].AdapterId, outputs[member].TargetId)).ToHashSet();
        var twice = Enumerable.Range(0, outputs.Count)
            .Where(candidate => group.Contains((outputs[candidate].AdapterId, outputs[candidate].TargetId)))
            .GroupBy(candidate => (outputs[candidate].AdapterId, outputs[candidate].TargetId))
            .Where(pair => pair.Count() > 1)
            .SelectMany(pair => pair)
            .Order()
            .ToList();
        if (twice.Count > 0)
        {
            return Fact.Missing<IReadOnlyList<string>>(
                FactGap.Ambiguous,
                "An adapter and target id pair among the outputs showing this source is listed more than once, so which outputs show it can't be established",
                [.. own, .. twice.SelectMany(duplicate => Pointers(duplicate, "adapterId", "targetId"))]);
        }

        return sharing.Count == 0
            ? Fact.Derived<IReadOnlyList<string>>([], Basis.Authoritative, Rules.DisplaySharedSource, [.. own, Outputs])
            : Fact.Derived<IReadOnlyList<string>>(
                [.. sharing.Select(DisplayId)],
                Basis.Authoritative,
                Rules.DisplaySharedSource,
                [.. own, .. sharing.SelectMany(other => Pointers(other, "adapterId", "sourceId"))]);
    }

    private static bool IsBlank(string id) => string.IsNullOrWhiteSpace(id);

    private static IEnumerable<string> Pointers(int index, params string[] fields) =>
        fields.Select(field => string.Create(CultureInfo.InvariantCulture, $"{Outputs}/{index}/{field}"));

    private static string DisplayId(int index) => string.Create(CultureInfo.InvariantCulture, $"display-{index + 1}");

    /// <summary>
    /// <see cref="Rules.DisplayModes"/>: the platform's list less the entries that aren't modes, a refresh of 0 or 1 Hz
    /// (the value platforms use for "the hardware's default") or a size that isn't positive.
    /// </summary>
    private static Fact<IReadOnlyList<VideoMode>> Modes(Fact<IReadOnlyList<DisplayMode>> listed)
    {
        if (!listed.IsKnown)
        {
            return Evidence.MissingLike<IReadOnlyList<DisplayMode>, IReadOnlyList<VideoMode>>(listed);
        }

        IReadOnlyList<VideoMode> modes = [.. listed.Value!
            .Where(static mode => mode.Width > 0 && mode.Height > 0 && mode.RefreshHz > 1)
            .Select(static mode => new VideoMode(mode.Width, mode.Height, mode.RefreshHz, mode.Interlaced))];
        return Fact.Derived(modes, listed.Basis!.Value, Rules.DisplayModes, listed.From);
    }

    /// <summary>
    /// A rate as an exact rational. 0/0 is the platform leaving the rate unset (not present); any other zero is
    /// not a rate (implausible).
    /// </summary>
    private static Fact<RefreshRate> Refresh(Fact<Rational> rate)
    {
        if (rate is { IsKnown: true, Value: { Numerator: 0, Denominator: 0 } })
        {
            return Fact.Missing<RefreshRate>(FactGap.NotPresent, "The platform left the rate unset (0/0)", rate.From);
        }

        return Evidence.Map(
            Evidence.Plausible(
                rate,
                static value => value.Numerator > 0 && value.Denominator > 0,
                static value => string.Create(CultureInfo.InvariantCulture, $"{value.Numerator}/{value.Denominator} Hz is not a refresh rate")),
            static value => new RefreshRate(value.Numerator, value.Denominator));
    }

    private static Fact<string> Gpu(DisplayOutput output, string at, SourceEntry<AdapterData> adapters)
    {
        if (!Evidence.TryData(adapters, "adapters", out var data, out var unavailable))
        {
            return unavailable.Fact<string>();
        }

        var gpus = GpuNormalizer.GpuIdsByAdapterId(data)[output.AdapterId].ToList();
        return gpus.Count switch
        {
            1 => Fact.Derived(gpus[0].Id, Basis.Authoritative, Rules.DisplayGpu, $"{at}/adapterId", gpus[0].Pointer),
            0 => Fact.Missing<string>(FactGap.Unmatched, "No adapter in the snapshot has the output's adapter id", $"{at}/adapterId", "/sources/adapters/data/adapters"),
            _ => Fact.Missing<string>(FactGap.Ambiguous, "More than one adapter has the output's adapter id", [$"{at}/adapterId", .. gpus.Select(gpu => gpu.Pointer)]),
        };
    }

    private static (Fact<MonitorIdentity> Identity, Fact<bool> Override) Edid(DisplayOutput output, string at, SourceEntry<EdidData> source)
    {
        if (!Evidence.TryData(source, "edid", out var edid, out var unavailable))
        {
            return (unavailable.Fact<MonitorIdentity>(), unavailable.Fact<bool>());
        }

        if (!output.MonitorDevicePath.IsKnown)
        {
            var noPath = Fact.Missing<MonitorIdentity>(FactGap.Unmatched, $"The output has no monitor device path to find its EDID by ({output.MonitorDevicePath.Detail})", $"{at}/monitorDevicePath");
            return (noPath, Evidence.MissingLike<MonitorIdentity, bool>(noPath));
        }

        var matches = edid.Monitors
            .Select((monitor, index) => (monitor, index))
            .Where(pair => pair.monitor.MonitorDevicePath == output.MonitorDevicePath.Value)
            .ToList();

        if (matches.Count != 1)
        {
            var none = Fact.Missing<MonitorIdentity>(
                matches.Count == 0 ? FactGap.Unmatched : FactGap.Ambiguous,
                matches.Count == 0 ? "No EDID in the snapshot belongs to the output's monitor" : "More than one EDID belongs to the output's monitor",
                $"{at}/monitorDevicePath",
                Monitors);
            return (none, Evidence.MissingLike<MonitorIdentity, bool>(none));
        }

        var (monitorEdid, edidIndex) = matches[0];
        var payload = Evidence.Observed(monitorEdid.Edid, Basis.Declared, $"{Monitors}/{edidIndex}/edid");
        if (!payload.IsKnown)
        {
            return (Evidence.MissingLike<EdidPayload, MonitorIdentity>(payload), Evidence.MissingLike<EdidPayload, bool>(payload));
        }

        var bytesAt = $"{Monitors}/{edidIndex}/edid/value/bytes";
        var validAt = $"{Monitors}/{edidIndex}/edid/value/checksumValidBeforeScrub";
        Fact<MonitorIdentity> identity;
        if (payload.Value!.ChecksumValidBeforeScrub is not [true, ..])
        {
            // The scrub recomputes checksums; this flag is the only record that the stored base block was intact.
            identity = Fact.Missing<MonitorIdentity>(FactGap.ParseFailed, "The EDID base block's checksum was invalid as stored, so its contents can't be trusted", bytesAt, validAt);
        }
        else
        {
            identity = EdidIdentity.Read(payload.Value.Bytes, out var gap, out var problem) is { } read
                ? Fact.Derived(read, Basis.Declared, Rules.DisplayMonitor, $"{at}/monitorDevicePath", bytesAt, validAt)
                : Fact.Missing<MonitorIdentity>(gap, problem!, bytesAt);
        }

        return (identity, Evidence.Observed(payload.Value.OverridePresent, Basis.Authoritative, $"{Monitors}/{edidIndex}/edid/value/overridePresent"));
    }
}

/// <summary>The model identity in an EDID base block (VESA E-EDID 1.4 §3.4): nothing else is read from it yet.</summary>
internal static class EdidIdentity
{
    private static readonly byte[] Header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

    /// <returns>The identity, or <c>null</c> with the gap and why: not a base block, or a manufacturer ID that isn't three letters A–Z.</returns>
    public static MonitorIdentity? Read(byte[] edid, out FactGap gap, out string? problem)
    {
        gap = FactGap.ParseFailed;
        if (edid.Length < 128 || !edid.AsSpan(0, Header.Length).SequenceEqual(Header))
        {
            problem = string.Create(CultureInfo.InvariantCulture, $"The {edid.Length} bytes are not a whole EDID base block");
            return null;
        }

        var packed = (edid[8] << 8) | edid[9];
        Span<char> letters = [Letter(packed >> 10), Letter(packed >> 5), Letter(packed)];
        if (letters.Contains('?'))
        {
            gap = FactGap.Implausible;
            problem = "The EDID's manufacturer ID is not three letters A–Z";
            return null;
        }

        problem = null;
        return new MonitorIdentity(new string(letters), edid[10] | (edid[11] << 8));
    }

    private static char Letter(int bits) => (bits & 0x1F) is var code && code is >= 1 and <= 26 ? (char)('A' + code - 1) : '?';
}
