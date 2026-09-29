using System.Text;
using FpsUtility.Core.Raw;

namespace FpsUtility.Core.Tests.Normalize;

/// <summary>Raw snapshots for normaliser tests: the synthetic fixture with one section replaced. Made-up data, no real machine.</summary>
internal static class RawBuilder
{
    public static RawSnapshot With(Func<SnapshotSources, SnapshotSources> change)
    {
        var raw = SnapshotFixture.Create();
        return new RawSnapshot(raw.SchemaVersion, raw.CapturedAtUtc, raw.App, raw.Os, raw.Elevated, change(raw.Sources));
    }

    public static SourceEntry<T> Unavailable<T>(MissingReason reason, string detail = "test failure")
        where T : class => SourceEntry.Unavailable<T>("test", 1, reason, detail);

    public static SourceEntry<T> Ok<T>(T data)
        where T : class => SourceEntry.Ok("test", 1, data);

    /// <summary>An EDID base block whose manufacturer is <c>TST</c> and product code 0x1234, with a valid checksum.</summary>
    public static byte[] EdidBaseBlock(char a = 'T', char b = 'S', char c = 'T')
    {
        var block = new byte[128];
        byte[] header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];
        header.CopyTo(block, 0);
        var packed = ((a - 'A' + 1) << 10) | ((b - 'A' + 1) << 5) | (c - 'A' + 1);
        block[8] = (byte)(packed >> 8);
        block[9] = (byte)packed;
        block[10] = 0x34;
        block[11] = 0x12;
        block[127] = (byte)((0x100 - (block.Take(127).Sum(value => value) & 0xFF)) & 0xFF);
        return block;
    }

    /// <summary>An SMBIOS structure: header (type, length, handle), the formatted fields after it, and its strings.</summary>
    public static byte[] Structure(byte type, byte[] fields, params string[] strings)
    {
        var formatted = new byte[4 + fields.Length];
        formatted[0] = type;
        formatted[1] = (byte)formatted.Length;
        fields.CopyTo(formatted, 4);
        byte[] stringSet = strings.Length == 0 ? [0, 0] : Encoding.ASCII.GetBytes(string.Join('\0', strings) + "\0\0");
        return [.. formatted, .. stringSet];
    }

    public static byte[] EndOfTable() => Structure(127, []);

    public static SmbiosData Smbios(params byte[][] structures) =>
        new(3, 8, 0, [.. structures.SelectMany(structure => structure)], 0, null);

    /// <summary>Little-endian word.</summary>
    public static byte[] Word(int value) => [(byte)value, (byte)(value >> 8)];

    public static byte[] Dword(uint value) => [(byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24)];
}
