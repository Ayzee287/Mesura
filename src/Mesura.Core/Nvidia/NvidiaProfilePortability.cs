using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Mesura.Core.Nvidia;

/// <summary>
/// One setting of an NVIDIA profile that was left out to fit this PC before the import (D032, D036): where it is, its
/// name, the value in the profile, the value imported (<c>null</c> when it was left out, so the PC's own value stays) and
/// why.
/// </summary>
public sealed record ProfileAdjustment(string Profile, uint SettingId, string Setting, string Original, string? Imported, string Reason)
{
    /// <summary>The profile's value in short.</summary>
    public string OriginalShort => Original;
}

/// <summary>
/// An NVIDIA profile as it is imported on this PC: the file's bytes (the original's, byte for byte, when nothing had to
/// change), its profiles for the check after the import, and what was changed from the original.
/// </summary>
public sealed record PortableProfile(byte[] Content, IReadOnlyList<NipProfile> Profiles, IReadOnlyList<ProfileAdjustment> Adjustments)
{
    public bool Adjusted => Adjustments.Count > 0;
}

/// <summary>
/// Makes Mesura's NVIDIA profile fit the PC it is imported on (D036). The profile holds one setting that depends on the
/// kind of PC: "Power management mode: Prefer maximum performance" (<see cref="MesuraNvidiaProfile.PowerManagementMode"/>)
/// keeps the GPU at full power, which costs a laptop battery life, so it is imported on a PC known to have no battery
/// and left out otherwise: the PC's own value then stays. Everything else is imported as the profile has it.
/// Pure: the battery is read by the caller.
/// </summary>
public static class NvidiaProfilePortability
{
    /// <summary>The profile to import on this PC.</summary>
    /// <param name="original">The original file, as rendered (it stays unchanged; a copy is changed).</param>
    /// <param name="hasBattery">Whether the PC has a system battery, or <c>null</c> when that couldn't be read.</param>
    /// <exception cref="FormatException">The original isn't a profile file.</exception>
    public static PortableProfile Adapt(byte[] original, bool? hasBattery)
    {
        ArgumentNullException.ThrowIfNull(original);
        var text = NipFile.Decode(original);
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(text.TrimStart('﻿')), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException e)
        {
            throw new FormatException($"Not an NVIDIA profile file: {e.Message}", e);
        }

        var desktopOnly = MesuraNvidiaProfile.Settings.Where(s => s.DesktopOnly).Select(s => s.Id).ToHashSet();
        var adjustments = new List<ProfileAdjustment>();
        if (hasBattery != false)
        {
            foreach (var profile in document.Root?.Elements("Profile") ?? [])
            {
                var profileName = (string?)profile.Element("ProfileName") ?? "";
                foreach (var row in (profile.Element("Settings")?.Elements("ProfileSetting") ?? []).ToList())
                {
                    if (!uint.TryParse((string?)row.Element("SettingID"), out var id) || !desktopOnly.Contains(id))
                    {
                        continue;
                    }

                    var name = ((string?)row.Element("SettingNameInfo"))?.Trim() is { Length: > 0 } info ? info : $"0x{id:X8}";
                    var value = (string?)row.Element("SettingValue") ?? "";
                    var why = hasBattery == true
                        ? "This PC has a battery, and keeping the GPU at full power costs battery life, so the setting is left out and your own value stays."
                        : "Couldn't tell whether this PC has a battery, and keeping the GPU at full power would cost a laptop battery life, so the setting is left out and your own value stays.";
                    adjustments.Add(new ProfileAdjustment(profileName, id, name, value, null, why));
                    if (row.PreviousNode is XText { Value: var space } gap && string.IsNullOrWhiteSpace(space))
                    {
                        gap.Remove();
                    }

                    row.Remove();
                }
            }
        }

        if (adjustments.Count == 0)
        {
            return new PortableProfile(original, NipFile.Parse(text), []);
        }

        // Written as NVIDIA Profile Inspector writes its own exports: UTF-16 with a byte-order mark.
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        document.Save(writer, SaveOptions.DisableFormatting);
        var xml = writer.ToString();
        return new PortableProfile([0xFF, 0xFE, .. System.Text.Encoding.Unicode.GetBytes(xml)], NipFile.Parse(xml), adjustments);
    }
}
