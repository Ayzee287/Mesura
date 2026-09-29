using System.Text;

namespace FpsUtility.Core.Normalize;

/// <summary>One SMBIOS structure (DSP0134 §6.1): its formatted area and its strings.</summary>
internal sealed class SmbiosStructure
{
    private readonly byte[] _formatted;
    private readonly IReadOnlyList<string> _strings;

    public SmbiosStructure(byte[] formatted, IReadOnlyList<string> strings)
    {
        _formatted = formatted;
        _strings = strings;
    }

    public byte Type => _formatted[0];

    /// <summary>The formatted area's length, header included; a field exists only below it.</summary>
    public int Length => _formatted.Length;

    public bool Has(int offset, int size) => offset + size <= _formatted.Length;

    public byte Byte(int offset) => _formatted[offset];

    public int Word(int offset) => _formatted[offset] | (_formatted[offset + 1] << 8);

    public uint Dword(int offset) => (uint)(_formatted[offset] | (_formatted[offset + 1] << 8) | (_formatted[offset + 2] << 16) | (_formatted[offset + 3] << 24));

    /// <summary>String number <paramref name="number"/> (1-based, as fields reference them), or <c>null</c> if there is no such string.</summary>
    public string? String(int number) => number >= 1 && number <= _strings.Count ? _strings[number - 1] : null;
}

/// <summary>
/// Walks an SMBIOS structure table into its structures, in table order, up to the end-of-table structure. It reads;
/// it never interprets. The walk is complete when it reaches the end-of-table structure, or when the last structure
/// ends exactly at the table's end (the platform gives the table's length). Anything after the end-of-table structure
/// is not structure data (the scrubber zeroes it). A table that breaks off earlier is incomplete, never guessed at.
/// </summary>
internal static class SmbiosTable
{
    private const byte EndOfTable = 127;
    private const int HeaderLength = 4;

    /// <param name="Complete">Whether every structure was read; if not, structures after the break are unknown.</param>
    public sealed record Walk(IReadOnlyList<SmbiosStructure> Structures, bool Complete);

    public static Walk Read(byte[] table)
    {
        var structures = new List<SmbiosStructure>();
        var offset = 0;

        while (offset + HeaderLength <= table.Length)
        {
            int length = table[offset + 1];
            if (length < HeaderLength || offset + length > table.Length)
            {
                return new Walk(structures, Complete: false);
            }

            var stringsStart = offset + length;
            var end = FindDoubleNul(table, stringsStart);
            if (end < 0)
            {
                return new Walk(structures, Complete: false);
            }

            structures.Add(new SmbiosStructure(table[offset..stringsStart], Strings(table, stringsStart, end)));
            if (table[offset] == EndOfTable)
            {
                return new Walk(structures, Complete: true);
            }

            offset = end + 2;
        }

        return new Walk(structures, Complete: structures.Count > 0 && offset == table.Length);
    }

    /// <summary>The index of the first NUL of the double NUL that ends a string set starting at <paramref name="start"/>, or -1.</summary>
    private static int FindDoubleNul(byte[] table, int start)
    {
        for (var i = start; i + 1 < table.Length; i++)
        {
            if (table[i] == 0 && table[i + 1] == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static List<string> Strings(byte[] table, int start, int end) =>
        start >= end
            ? []
            : [.. Encoding.Latin1.GetString(table, start, end - start).Split('\0')];
}
