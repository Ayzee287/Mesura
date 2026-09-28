namespace FpsUtility.Core.Privacy;

/// <summary>
/// Overwrites the identifying fields of an SMBIOS structure table before it is kept (architecture
/// §4.4, §13). The table has the same form on every platform (§4.4 rule 2), so this lives in Core.
/// It walks the structures only to locate fields; parsing is the normaliser's job (step 3).
/// </summary>
/// <remarks>
/// <para>
/// In the structure types that carry identifiers, strings are allow-listed: only the descriptive
/// strings named below survive. Every other string in those structures is overwritten, including
/// strings no field references (firmware sometimes leaves a placeholder asset tag in the string set).
/// A string that an identifying field also references is overwritten even if a kept field shares it.
/// Offsets are DMTF DSP0134's and apply only where the structure is long enough.
/// </para>
/// <list type="bullet">
/// <item>Type 1 System: keeps manufacturer, product name, version, SKU, family. Overwrites the serial
/// number; zeroes the UUID (16 bytes at 0x08).</item>
/// <item>Type 2 Baseboard: keeps manufacturer, product, version, location in chassis. Overwrites the
/// serial number and asset tag.</item>
/// <item>Type 3 Chassis: keeps manufacturer, version, SKU. Overwrites the serial number and asset tag.</item>
/// <item>Type 4 Processor: keeps socket, manufacturer, version, part number. Overwrites the serial
/// number and asset tag.</item>
/// <item>Type 11 OEM strings: overwrites every string (free-form vendor text).</item>
/// <item>Type 17 Memory device: keeps device and bank locator, manufacturer, part number (D004),
/// firmware version. Overwrites the serial number and asset tag.</item>
/// <item>Type 22 Portable battery: keeps location, manufacturer, manufacture date, device name, SBDS
/// version, SBDS chemistry. Overwrites the serial number; zeroes the SBDS serial number (word at 0x10).</item>
/// <item>Type 39 Power supply: keeps location, device name, manufacturer, model part number, revision.
/// Overwrites the serial number and asset tag.</item>
/// </list>
/// <para>
/// Other structure types are left as they are. A string is overwritten in place with <c>*</c>
/// characters of the same length, so the structure stays walkable. Bytes that can't be walked (a
/// malformed structure, or data after the end-of-table structure) are zeroed, and the offset where
/// that starts is reported.
/// </para>
/// </remarks>
public static class SmbiosScrubber
{
    public const byte RedactionCharacter = (byte)'*';

    private const byte EndOfTableType = 127;
    private const byte ChassisType = 3;
    private const int HeaderLength = 4;

    /// <summary>Per structure type: kept string fields, identifying string fields, identifying binary fields.</summary>
    private static readonly Dictionary<byte, Rule> Rules = new()
    {
        [1] = new([0x04, 0x05, 0x06, 0x19, 0x1A], [0x07], [(0x08, 16)]),
        [2] = new([0x04, 0x05, 0x06, 0x0A], [0x07, 0x08], []),
        [3] = new([0x04, 0x06], [0x07, 0x08], []), // plus the SKU, whose offset depends on the contained elements
        [4] = new([0x04, 0x07, 0x10, 0x22], [0x20, 0x21], []),
        [11] = new([], [], []),
        [17] = new([0x10, 0x11, 0x17, 0x1A, 0x2B], [0x18, 0x19], []),
        [22] = new([0x04, 0x05, 0x06, 0x08, 0x0E, 0x14], [0x07], [(0x10, 2)]),
        [39] = new([0x05, 0x06, 0x07, 0x0A, 0x0B], [0x08, 0x09], []),
    };

    /// <param name="Table">The scrubbed table, the same length as the input.</param>
    /// <param name="FieldsScrubbed">How many strings and binary fields held data and were overwritten.</param>
    /// <param name="ZeroedFromOffset">Where unwalkable bytes start being zeroed; <c>null</c> if every non-zero byte was walked.</param>
    public sealed record Result(byte[] Table, int FieldsScrubbed, int? ZeroedFromOffset);

    public static Result Scrub(ReadOnlySpan<byte> table)
    {
        var bytes = table.ToArray();
        var scrubbed = 0;
        var offset = 0;

        while (offset < bytes.Length)
        {
            if (!TryMeasure(bytes, offset, out var formattedLength, out var stringsEnd))
            {
                return ZeroTail(bytes, offset, scrubbed);
            }

            var structure = bytes.AsSpan(offset, stringsEnd - offset);
            if (Rules.TryGetValue(structure[0], out var rule))
            {
                scrubbed += ScrubStructure(structure, formattedLength, rule);
            }

            offset = stringsEnd;
            if (structure[0] == EndOfTableType)
            {
                return ZeroTail(bytes, offset, scrubbed);
            }
        }

        return new Result(bytes, scrubbed, null);
    }

    /// <summary>
    /// Checks one structure's bounds: a formatted area of at least 4 bytes inside the table, followed by
    /// a string set that ends with a double NUL.
    /// </summary>
    private static bool TryMeasure(byte[] bytes, int offset, out int formattedLength, out int stringsEnd)
    {
        formattedLength = 0;
        stringsEnd = 0;

        if (offset + HeaderLength > bytes.Length)
        {
            return false;
        }

        formattedLength = bytes[offset + 1];
        if (formattedLength < HeaderLength || offset + formattedLength > bytes.Length)
        {
            return false;
        }

        for (var i = offset + formattedLength; i + 1 < bytes.Length; i++)
        {
            if (bytes[i] == 0 && bytes[i + 1] == 0)
            {
                stringsEnd = i + 2;
                return true;
            }
        }

        return false;
    }

    private static int ScrubStructure(Span<byte> structure, int formattedLength, Rule rule)
    {
        var strings = LocateStrings(structure, formattedLength);
        var formatted = structure[..formattedLength];

        var kept = new HashSet<int>(ReferencedStrings(formatted, KeptFields(formatted, rule)));
        kept.ExceptWith(ReferencedStrings(formatted, rule.IdentifyingStrings));

        var count = 0;
        for (var number = 1; number <= strings.Count; number++)
        {
            if (!kept.Contains(number))
            {
                var (start, length) = strings[number - 1];
                count += Redact(structure.Slice(start, length)) ? 1 : 0;
            }
        }

        foreach (var (fieldOffset, fieldLength) in rule.IdentifyingBinary)
        {
            if (fieldOffset + fieldLength <= formattedLength)
            {
                var field = structure.Slice(fieldOffset, fieldLength);
                if (field.ContainsAnyExcept((byte)0))
                {
                    field.Clear();
                    count++;
                }
            }
        }

        return count;
    }

    private static IEnumerable<int> KeptFields(ReadOnlySpan<byte> formatted, Rule rule)
    {
        var fields = new List<int>(rule.KeptStrings);

        // Type 3's SKU follows the contained elements: offset 0x15 + count (0x13) × record length (0x14).
        if (formatted[0] == ChassisType && formatted.Length > 0x15)
        {
            fields.Add(0x15 + (formatted[0x13] * formatted[0x14]));
        }

        return fields;
    }

    /// <summary>The string numbers the given fields reference, for fields inside the formatted area.</summary>
    private static List<int> ReferencedStrings(ReadOnlySpan<byte> formatted, IEnumerable<int> fields)
    {
        var numbers = new List<int>();
        foreach (var field in fields)
        {
            if (field < formatted.Length && formatted[field] != 0)
            {
                numbers.Add(formatted[field]);
            }
        }

        return numbers;
    }

    /// <summary>The (start, length) of each string in the structure's string set; string n is at index n - 1.</summary>
    private static List<(int Start, int Length)> LocateStrings(ReadOnlySpan<byte> structure, int formattedLength)
    {
        var strings = new List<(int Start, int Length)>();
        var position = formattedLength;

        // An empty string set is just the double NUL.
        while (position < structure.Length && structure[position] != 0)
        {
            var length = structure[position..].IndexOf((byte)0);
            strings.Add((position, length));
            position += length + 1;
        }

        return strings;
    }

    /// <summary>Overwrites a string's characters; true if it wasn't already redacted.</summary>
    private static bool Redact(Span<byte> text)
    {
        if (!text.ContainsAnyExcept(RedactionCharacter))
        {
            return false;
        }

        text.Fill(RedactionCharacter);
        return true;
    }

    private static Result ZeroTail(byte[] bytes, int offset, int scrubbed)
    {
        var tail = bytes.AsSpan(offset);
        if (!tail.ContainsAnyExcept((byte)0))
        {
            return new Result(bytes, scrubbed, null);
        }

        tail.Clear();
        return new Result(bytes, scrubbed, offset);
    }

    private sealed record Rule(int[] KeptStrings, int[] IdentifyingStrings, (int Offset, int Length)[] IdentifyingBinary);
}
