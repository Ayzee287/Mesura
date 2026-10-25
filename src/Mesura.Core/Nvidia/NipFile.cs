using System.Xml;
using System.Xml.Linq;

namespace Mesura.Core.Nvidia;

/// <summary>One setting of an NVIDIA driver profile in a <c>.nip</c> file: its numeric id, value and value type.</summary>
public sealed record NipSetting(uint Id, string Value, string ValueType);

/// <summary>One profile in a <c>.nip</c> file: the Base Profile (global) or an application profile with its executables.</summary>
public sealed record NipProfile(string Name, IReadOnlyList<string> Executables, IReadOnlyList<NipSetting> Settings)
{
    public const string BaseProfile = "Base Profile";

    public bool IsGlobal => Name.Equals(BaseProfile, StringComparison.Ordinal);
}

/// <summary>
/// Reads NVIDIA Profile Inspector's <c>.nip</c> files (an <c>ArrayOfProfile</c> of <c>Profile</c> elements, each with a
/// name, <c>Executeables</c> — NVIDIA Profile Inspector's spelling — and <c>ProfileSetting</c> rows of
/// <c>SettingID</c>, <c>SettingValue</c> and <c>ValueType</c>), so a backup can be checked after it was put back. The one
/// <c>.nip</c> Mesura writes is a shipped profile's copy adjusted for the PC (<see cref="NvidiaProfilePortability"/>).
/// </summary>
public static class NipFile
{
    /// <exception cref="FormatException">Not a profile file.</exception>
    public static IReadOnlyList<NipProfile> Parse(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        XDocument document;
        try
        {
            // DTDs are refused: a profile file never needs one.
            using var reader = XmlReader.Create(new StringReader(xml.TrimStart('﻿')), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            document = XDocument.Load(reader);
        }
        catch (XmlException e)
        {
            throw new FormatException($"Not an NVIDIA profile file: {e.Message}", e);
        }

        if (document.Root?.Name.LocalName != "ArrayOfProfile")
        {
            throw new FormatException("Not an NVIDIA profile file: it has no ArrayOfProfile.");
        }

        return [.. document.Root.Elements("Profile").Select(p => new NipProfile(
            (string?)p.Element("ProfileName") ?? "",
            [.. (p.Element("Executeables")?.Elements() ?? []).Select(e => e.Value)],
            [.. (p.Element("Settings")?.Elements("ProfileSetting") ?? [])
                .Select(s => uint.TryParse((string?)s.Element("SettingID"), out var id)
                    ? new NipSetting(id, (string?)s.Element("SettingValue") ?? "", (string?)s.Element("ValueType") ?? "Dword")
                    : null)
                .OfType<NipSetting>()]))];
    }

    /// <summary>
    /// A profile file's text from its bytes: NVIDIA Profile Inspector writes UTF-16 with a byte-order mark; a file
    /// re-saved by an editor may be UTF-8 (with or without a mark) while still declaring UTF-16.
    /// </summary>
    public static string Decode(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return content switch
        {
            [0xFF, 0xFE, ..] => System.Text.Encoding.Unicode.GetString(content, 2, content.Length - 2),
            [0xFE, 0xFF, ..] => System.Text.Encoding.BigEndianUnicode.GetString(content, 2, content.Length - 2),
            [0xEF, 0xBB, 0xBF, ..] => System.Text.Encoding.UTF8.GetString(content, 3, content.Length - 3),
            [_, 0x00, ..] => System.Text.Encoding.Unicode.GetString(content),
            _ => System.Text.Encoding.UTF8.GetString(content),
        };
    }

    /// <summary>How many settings the profiles hold in all.</summary>
    public static int SettingCount(IReadOnlyList<NipProfile> profiles) => profiles.Sum(p => p.Settings.Count);

    /// <summary>
    /// The settings of <paramref name="backup"/> that <paramref name="readback"/> doesn't hold with the same value, as
    /// "profile: setting id" lines. Empty when everything backed up is in place again.
    /// </summary>
    public static IReadOnlyList<string> Missing(IReadOnlyList<NipProfile> backup, IReadOnlyList<NipProfile> readback)
    {
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentNullException.ThrowIfNull(readback);
        var now = readback.GroupBy(p => p.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.SelectMany(p => p.Settings).GroupBy(s => s.Id).ToDictionary(s => s.Key, s => s.First().Value), StringComparer.Ordinal);
        return [.. backup.SelectMany(p => p.Settings
            .Where(s => !(now.TryGetValue(p.Name, out var settings) && settings.TryGetValue(s.Id, out var value) && value == s.Value))
            .Select(s => $"{p.Name}: 0x{s.Id:X8}"))];
    }
}
