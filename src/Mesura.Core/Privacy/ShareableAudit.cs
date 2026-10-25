using System.Globalization;
using System.Text.RegularExpressions;
using Mesura.Core.Raw;

namespace Mesura.Core.Privacy;

/// <summary>
/// A privacy problem found in a file meant to leave its machine: which file, what kind of data, and where (a JSON
/// Pointer or <c>line N</c>), with what to do about it. It never holds the value itself.
/// </summary>
public sealed record PrivacyFinding(string File, string Category, string Pointer, string Remediation);

/// <summary>
/// The identifier-free half of the privacy verification a Track A package gets (architecture §4.7, §13). It needs
/// none of the capturing PC's identifiers, so it runs on the capturing PC and again on the owner's machine before a
/// package is accepted. The other half, <see cref="IdentifierLeakCheck"/>, compares with the PC's own identifiers and
/// can run only where they are.
/// </summary>
/// <remarks>
/// <para>For a scrubbed raw snapshot (<see cref="AuditScrubbedSnapshot"/>), the invariants <c>capture-fixture</c>
/// and the kit promise: the EDID and SMBIOS scrubs have nothing left to remove, every opaque id is a stand-in, and no
/// real volume GUID is left.</para>
/// <para>For every JSON file (<see cref="AuditJson"/>), patterns that are personal wherever they appear: a path into a
/// user's profile folder, a Steam personal value that wasn't redacted, a Windows account SID, a MAC address, an e-mail
/// address and a Steam account id (SteamID64).</para>
/// It never changes a file. A finding means the file must not be shared as it is.
/// </remarks>
public static partial class ShareableAudit
{
    public const string UnscrubbedEdid = "EDID identifier field not scrubbed";
    public const string UnscrubbedSmbios = "SMBIOS field not scrubbed";
    public const string OpaqueId = "opaque platform id without a stand-in";
    public const string VolumeGuid = "volume GUID";
    public const string UserFolder = "path into a user's profile folder";
    public const string SteamPersonal = "Steam personal value not redacted";
    public const string AccountSid = "Windows account SID";
    public const string MacAddress = "MAC address";
    public const string EmailAddress = "e-mail address";
    public const string SteamAccountId = "Steam account id";

    private const string RecaptureAdvice =
        "Don't edit the file. Capture again with the kit; if it happens again, the scrub or the stand-ins miss this field, which is a privacy defect to fix in the product first.";

    private const string TextAdvice =
        "Don't share the file as it is. If the value came from an operator answer, capture again and answer without it; if it came from the machine, it is a privacy defect to fix in the product first.";

    /// <summary>The structural invariants of a scrubbed raw snapshot, plus <see cref="AuditJson"/>.</summary>
    public static IReadOnlyList<PrivacyFinding> AuditScrubbedSnapshot(string file, ReadOnlySpan<byte> json, RawSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var findings = new List<PrivacyFinding>();
        var sources = snapshot.Sources;

        if (sources.Edid.Data is { } edid)
        {
            for (var i = 0; i < edid.Monitors.Count; i++)
            {
                if (edid.Monitors[i].Edid.Value is not { } payload)
                {
                    continue;
                }

                bool clean;
                try
                {
                    clean = EdidScrubber.Scrub(payload.Bytes).FieldsScrubbed == 0;
                }
                catch (FormatException)
                {
                    clean = false; // not an EDID the scrub can walk, so its identifier fields can't be shown to be empty
                }

                if (!clean)
                {
                    findings.Add(new(file, UnscrubbedEdid, Pointer($"/sources/edid/data/monitors/{i}/edid/value/bytes"), RecaptureAdvice));
                }
            }
        }

        if (sources.Smbios.Data is { } smbios)
        {
            var again = SmbiosScrubber.Scrub(smbios.Table);
            if (again.FieldsScrubbed > 0 || !again.Table.AsSpan().SequenceEqual(smbios.Table))
            {
                findings.Add(new(file, UnscrubbedSmbios, "/sources/smbios/data/table", RecaptureAdvice));
            }
        }

        foreach (var text in JsonStrings.Of(json))
        {
            var standIn = text.Holder switch
            {
                "adapterId" => AdapterStandIn().IsMatch(text.Value),
                "instanceId" => InstanceStandIn().IsMatch(text.Value),
                "monitorDevicePath" => MonitorPathStandIn().IsMatch(text.Value),
                _ => true,
            };

            if (!standIn)
            {
                findings.Add(new(file, OpaqueId, text.Pointer, RecaptureAdvice));
            }
        }

        findings.AddRange(AuditJson(file, json));
        return findings;
    }

    /// <summary>Personal patterns in every string of a JSON file (the list in the remarks).</summary>
    public static IReadOnlyList<PrivacyFinding> AuditJson(string file, ReadOnlySpan<byte> json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);

        var findings = new List<PrivacyFinding>();
        foreach (var text in JsonStrings.Of(json))
        {
            if (text.Property is "bytes" or "table")
            {
                continue; // byte payloads: the structural audit covers them
            }

            foreach (var category in PersonalPatterns(text.Value))
            {
                findings.Add(new(file, category, text.Pointer, category == SteamPersonal || category == VolumeGuid ? RecaptureAdvice : TextAdvice));
            }
        }

        return findings;
    }

    /// <summary>Personal patterns in plain text, by line.</summary>
    public static IReadOnlyList<PrivacyFinding> AuditText(string file, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        ArgumentNullException.ThrowIfNull(text);

        var findings = new List<PrivacyFinding>();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (var category in PersonalPatterns(lines[i]))
            {
                findings.Add(new(file, category, string.Create(CultureInfo.InvariantCulture, $"line {i + 1}"), TextAdvice));
            }
        }

        return findings;
    }

    private static IEnumerable<string> PersonalPatterns(string value)
    {
        if (RealVolumeGuid().IsMatch(value))
        {
            yield return VolumeGuid;
        }

        if (UserFolderPath().IsMatch(value))
        {
            yield return UserFolder;
        }

        if (SteamPersonalValue().Matches(value).Any(match => match.Groups["value"].Value is not ("" or SteamTextScrubber.Redacted)))
        {
            yield return SteamPersonal;
        }

        if (Sid().IsMatch(value))
        {
            yield return AccountSid;
        }

        if (Mac().IsMatch(value))
        {
            yield return MacAddress;
        }

        if (Email().IsMatch(value))
        {
            yield return EmailAddress;
        }

        if (SteamId64().IsMatch(value))
        {
            yield return SteamAccountId;
        }
    }

    private static string Pointer(FormattableString pointer) => pointer.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex("^adapter-[0-9]+\\z", RegexOptions.CultureInvariant)]
    private static partial Regex AdapterStandIn();

    /// <summary>The pseudonymiser keeps the enumerator and hardware-ID parts and replaces the instance part.</summary>
    [GeneratedRegex(@"(?:^|\\)instance-[0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex InstanceStandIn();

    /// <summary><c>\\?\DISPLAY#MODEL#instance-N#{class}</c>, or the pseudonymiser's whole replacement for a short path.</summary>
    [GeneratedRegex("^(?:[^#]*#[^#]*#instance-[0-9]+#|monitor-instance-[0-9]+\\z)", RegexOptions.CultureInvariant)]
    private static partial Regex MonitorPathStandIn();

    /// <summary>A real volume GUID (hex digits), as opposed to a <c>{volume-N}</c> stand-in.</summary>
    [GeneratedRegex(@"Volume\{[0-9A-Fa-f]{8}-", RegexOptions.CultureInvariant)]
    private static partial Regex RealVolumeGuid();

    /// <summary>A path into a user's folder that the profile redaction should have rewritten (<c>Public</c> is shared, not personal).</summary>
    [GeneratedRegex(@"[A-Za-z]:[\\/]+Users[\\/]+(?!Public\b)[^\\/%""]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UserFolderPath();

    [GeneratedRegex(@"""(?:LastOwner|LastPlayed|contentid|contentstatsid|label)""[ \t]+""(?<value>(?:[^""\\]|\\.)*)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteamPersonalValue();

    /// <summary>A Windows account (or domain) SID; well-known SIDs such as <c>S-1-5-18</c> are shorter.</summary>
    [GeneratedRegex(@"\bS-1-5-21-[0-9]+-[0-9]+-[0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex Sid();

    /// <summary>Six hex octets joined by colons or hyphens.</summary>
    [GeneratedRegex("(?<![0-9A-Fa-f:-])(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}(?![0-9A-Fa-f:-])", RegexOptions.CultureInvariant)]
    private static partial Regex Mac();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    /// <summary>A 17-digit individual Steam account id (the 7656119… range).</summary>
    [GeneratedRegex("(?<![0-9])7656119[0-9]{10}(?![0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex SteamId64();
}
