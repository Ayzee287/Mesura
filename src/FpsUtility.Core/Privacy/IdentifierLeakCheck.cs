using System.Globalization;
using System.Text;

namespace FpsUtility.Core.Privacy;

/// <summary>How an identifier is matched, which follows from where it comes from.</summary>
public enum IdentifierKind
{
    /// <summary>
    /// A value firmware or a device reports (serial numbers, UUIDs, MAC addresses). Firmware writes placeholders
    /// where it has no value ("To Be Filled By O.E.M.", "Default"), and those can equal kept model strings, so
    /// placeholders are skipped (the rules on <see cref="IdentifierLeakCheck"/>).
    /// </summary>
    Hardware,

    /// <summary>
    /// A value about the person or the installation (user and computer names, the profile path, the registered
    /// owner). Always checked, as a whole phrase, spaces included, because "C:\Users\John Smith" is a normal
    /// profile path.
    /// </summary>
    Personal,

    /// <summary>Bytes, such as an EDID serial number: matched byte for byte inside the decoded byte payloads.</summary>
    Binary,
}

/// <param name="Category">What the identifier is, e.g. <c>system serial number</c>. Reports name only this.</param>
/// <param name="Value">The identifier itself (hex digits for a binary one). Held in memory for the comparison; never written or printed.</param>
/// <param name="Kind">How it is matched.</param>
public sealed record LocalIdentifier(string Category, string Value, IdentifierKind Kind = IdentifierKind.Hardware)
{
    public static LocalIdentifier Personal(string category, string value) => new(category, value, IdentifierKind.Personal);

    public static LocalIdentifier Binary(string category, ReadOnlySpan<byte> value) =>
        new(category, Convert.ToHexString(value), IdentifierKind.Binary);

    /// <summary>The value is kept out of <see cref="ToString"/> so it can't reach a log or console by accident.</summary>
    public override string ToString() => Category;
}

/// <summary>
/// Proves a fixture holds none of the collecting machine's own identifiers before it is written
/// (architecture §13). The check covers every string in the snapshot and the decoded EDID and SMBIOS
/// bytes. Text matches case-insensitively, as a whole token: not inside a longer word, so a user called
/// "User" doesn't match <c>%USERPROFILE%</c>.
/// </summary>
/// <remarks>
/// <para>What is skipped, by kind:</para>
/// <list type="bullet">
/// <item><see cref="IdentifierKind.Hardware"/>: values that can't identify a machine, being shorter than 4
/// characters, one repeated character (<c>00000000</c>), containing whitespace (firmware placeholders such as
/// "To Be Filled By O.E.M."), or a known single-word firmware placeholder (<c>NULL</c>, <c>Default</c>, …).</item>
/// <item><see cref="IdentifierKind.Personal"/>: only values shorter than 4 characters. A 3-letter user name
/// alone is too common a word to test for; the profile path that contains it is still checked.</item>
/// <item><see cref="IdentifierKind.Binary"/>: values shorter than 4 bytes or one repeated byte.</item>
/// </list>
/// </remarks>
public static class IdentifierLeakCheck
{
    private static readonly HashSet<string> BytePayloadProperties = new(StringComparer.Ordinal) { "bytes", "table" };

    /// <returns>The categories found in the snapshot, each once, in the order given. Empty means clean.</returns>
    public static IReadOnlyList<string> FindLeaks(ReadOnlySpan<byte> snapshotJson, IEnumerable<LocalIdentifier> identifiers)
    {
        ArgumentNullException.ThrowIfNull(identifiers);

        var list = identifiers.ToList();
        var locations = FindLeakLocations(snapshotJson, list);
        return [.. list.Select(identifier => identifier.Category).Distinct().Where(category => locations.Any(location => location.Category == category))];
    }

    /// <summary>
    /// Where each identifier occurs in a JSON document: every string, and the decoded bytes of the byte payloads, under
    /// the same rules as <see cref="FindLeaks"/>. A location names the category and the JSON Pointer of the string or
    /// payload that holds it, never the value, so it can be reported (a Track A capture's failure report).
    /// </summary>
    /// <returns>Each category once per location, in document order. Empty means clean.</returns>
    public static IReadOnlyList<LeakLocation> FindLeakLocations(ReadOnlySpan<byte> json, IEnumerable<LocalIdentifier> identifiers)
    {
        ArgumentNullException.ThrowIfNull(identifiers);

        var checkable = identifiers.Where(IsCheckable).ToList();
        var found = new List<LeakLocation>();

        foreach (var text in JsonStrings.Of(json))
        {
            var payload = text.Property is not null && BytePayloadProperties.Contains(text.Property) ? DecodeBase64(text.Value) : null;
            // A payload is matched as bytes and as its Latin-1 text (SMBIOS strings), never as its base64 spelling.
            var candidate = payload is null ? text.Value : Encoding.Latin1.GetString(payload);

            foreach (var identifier in checkable)
            {
                var leaked = identifier.Kind == IdentifierKind.Binary
                    ? payload is not null && payload.AsSpan().IndexOf(Convert.FromHexString(identifier.Value)) >= 0
                    : ContainsToken(candidate, identifier.Value.Trim());

                if (leaked && !found.Contains(new LeakLocation(identifier.Category, text.Pointer)))
                {
                    found.Add(new LeakLocation(identifier.Category, text.Pointer));
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Where each text identifier (personal or hardware) occurs in plain text, by line (<c>line 12</c>). Binary
    /// identifiers are matched only in byte payloads, so plain text never has them.
    /// </summary>
    public static IReadOnlyList<LeakLocation> FindLeakLocationsInText(string text, IEnumerable<LocalIdentifier> identifiers)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(identifiers);

        var checkable = identifiers.Where(identifier => identifier.Kind != IdentifierKind.Binary && IsCheckable(identifier)).ToList();
        var found = new List<LeakLocation>();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (var identifier in checkable)
            {
                var location = new LeakLocation(identifier.Category, string.Create(CultureInfo.InvariantCulture, $"line {i + 1}"));
                if (ContainsToken(lines[i], identifier.Value.Trim()) && !found.Contains(location))
                {
                    found.Add(location);
                }
            }
        }

        return found;
    }

    /// <summary>Whether the identifier can identify anything, so a match would mean a leak (the rules in the remarks).</summary>
    public static bool IsCheckable(LocalIdentifier identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        switch (identifier.Kind)
        {
            case IdentifierKind.Binary:
                var bytes = Convert.FromHexString(identifier.Value);
                return bytes.Length >= 4 && bytes.AsSpan().ContainsAnyExcept(bytes[0]);
            case IdentifierKind.Personal:
                return identifier.Value.Trim().Length >= 4;
            default:
                var value = identifier.Value.Trim();
                return value.Length >= 4
                    && value.Distinct().Skip(1).Any()
                    && !value.Any(char.IsWhiteSpace)
                    && !FirmwarePlaceholders.Contains(value);
        }
    }

    /// <summary>
    /// Single-word values firmware writes where it has no serial. PC-000's processor "serial" is one of
    /// them, and the same word is its (kept) processor part number, so it must not count as a leak.
    /// </summary>
    private static readonly HashSet<string> FirmwarePlaceholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "NULL", "None", "Unknown", "Default", "N/A", "NA", "Empty", "OEM", "Serial", "SerialNumber",
        "0123456789", "123456789", "1234567890", "9876543210", "ABCDEFGHIJ",
    };

    /// <summary>A byte payload's bytes; a value that isn't base64 is checked as text only.</summary>
    private static byte[]? DecodeBase64(string value)
    {
        var buffer = new byte[value.Length];
        return Convert.TryFromBase64String(value, buffer, out var written) ? buffer[..written] : null;
    }

    private static bool ContainsToken(string text, string value)
    {
        for (var index = text.IndexOf(value, StringComparison.OrdinalIgnoreCase); index >= 0; index = text.IndexOf(value, index + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = index + value.Length;
            var startsToken = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            var endsToken = end == text.Length || !char.IsLetterOrDigit(text[end]);
            if (startsToken && endsToken)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Where an identifier was found: its category and the JSON Pointer (or <c>line N</c>) that holds it. Never the value.</summary>
public sealed record LeakLocation(string Category, string Pointer);
