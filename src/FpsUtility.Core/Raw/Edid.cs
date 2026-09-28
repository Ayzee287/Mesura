using System.Text.Json.Serialization;

namespace FpsUtility.Core.Raw;

/// <summary>The <c>edid</c> section: the EDID of each active output's monitor (architecture §4.4).</summary>
public sealed record EdidData
{
    [JsonConstructor]
    public EdidData(IReadOnlyList<MonitorEdid> monitors)
    {
        Monitors = Canonical.Sort(monitors, static (a, b) => Canonical.Ordinal(a.MonitorDevicePath, b.MonitorDevicePath));
    }

    public IReadOnlyList<MonitorEdid> Monitors { get; }
}

/// <param name="MonitorDevicePath">Opaque; the same value as <c>display.outputs[].monitorDevicePath</c>.</param>
/// <param name="Edid">The monitor's EDID, or why there is none (KVMs and some adapters provide none).</param>
public sealed record MonitorEdid(string MonitorDevicePath, Reading<EdidPayload> Edid);

/// <summary>
/// EDID bytes exactly as the platform stores them, except the unit identifiers and any content the
/// scrubber doesn't understand, which are zeroed at collection (architecture §13, <c>Privacy.EdidScrubber</c>).
/// The block count is never taken from the buffer length (§4.4 rule 2).
/// </summary>
/// <param name="Bytes">The whole stored buffer, base64 in JSON.</param>
/// <param name="ChecksumValidBeforeScrub">
/// Per 128-byte block, in buffer order: whether its checksum was valid as stored. A block the scrub
/// changed gets a recomputed checksum, so this is the only record of the original's validity.
/// </param>
/// <param name="FieldsScrubbed">How many fields held data and were zeroed.</param>
/// <param name="OverridePresent">Whether an INF EDID override is installed for this monitor; if so, Windows uses its blocks instead of these.</param>
public sealed record EdidPayload(
    byte[] Bytes,
    IReadOnlyList<bool> ChecksumValidBeforeScrub,
    int FieldsScrubbed,
    Reading<bool> OverridePresent);
