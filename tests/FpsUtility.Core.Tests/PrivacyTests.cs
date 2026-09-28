using System.Text;
using FpsUtility.Core.Privacy;

namespace FpsUtility.Core.Tests;

/// <summary>Scrubbing at collection (architecture §4.4, §13): identifiers never reach a snapshot.</summary>
public sealed class PrivacyTests
{
    [Fact]
    public void Edid_serial_number_and_serial_descriptor_are_zeroed_and_checksums_fixed()
    {
        var edid = BaseBlock();

        var result = EdidScrubber.Scrub(edid);

        Assert.Equal(2, result.FieldsScrubbed);
        Assert.Equal([0, 0, 0, 0], result.Bytes[12..16]);
        Assert.All(result.Bytes[(72 + 5)..(72 + 18)], value => Assert.Equal(0, value));
        Assert.Equal("TESTMON", Encoding.ASCII.GetString(result.Bytes, 90 + 5, 7)); // the monitor name is not an identifier
        Assert.Equal([true], result.ChecksumValidBeforeScrub);
        Assert.Equal(0, result.Bytes.Sum(value => value) & 0xFF);
    }

    [Fact]
    public void Edid_displayid_product_serial_and_manufacture_date_are_zeroed_with_both_checksums_fixed()
    {
        var edid = BaseBlock(extensions: 1).Concat(DisplayIdBlock()).ToArray();

        var result = EdidScrubber.Scrub(edid);

        var block = result.Bytes[128..256];
        Assert.Equal(4, result.FieldsScrubbed); // base serial, serial descriptor, DisplayID serial, DisplayID week and year
        Assert.Equal([0, 0, 0, 0], block[(5 + 3 + 5)..(5 + 3 + 9)]);
        Assert.Equal([0, 0], block[(5 + 3 + 9)..(5 + 3 + 11)]);
        Assert.Equal([0xAA, 0xBB, 0xCC, 0x01, 0x02], block[(5 + 3)..(5 + 3 + 5)]); // manufacturer and product code are model data
        Assert.Equal(0, block[1..(5 + 15 + 1)].Sum(value => value) & 0xFF); // section checksum
        Assert.Equal(0, block.Sum(value => value) & 0xFF); // block checksum
        Assert.Equal([true, true], result.ChecksumValidBeforeScrub);
    }

    [Fact]
    public void Edid_original_checksum_validity_is_recorded_before_the_scrub_repairs_it()
    {
        var edid = BaseBlock();
        edid[127] ^= 0x01;

        var result = EdidScrubber.Scrub(edid);

        Assert.Equal([false], result.ChecksumValidBeforeScrub);
    }

    [Fact]
    public void Edid_without_its_header_is_refused_because_its_serials_cannot_be_located()
    {
        var notEdid = new byte[128];

        Assert.Throws<FormatException>(() => EdidScrubber.Scrub(notEdid));
    }

    [Fact]
    public void Smbios_identifiers_are_overwritten_and_descriptive_strings_kept()
    {
        var table = Concat(
            Structure(1, [0x01, 0x02, 0x03, 0x04, .. Uuid(), 0x06, 0x05, 0x00], "Maker", "Model", "1.0", "SN-SYSTEM-1", "SKU-1"),
            Memory(),
            Structure(11, [0x02], "OEM-SECRET-1", "OEM-SECRET-2"),
            Structure(127, []));

        var result = SmbiosScrubber.Scrub(table);
        var text = Encoding.ASCII.GetString(result.Table);

        Assert.DoesNotContain("SN-SYSTEM-1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("DIMM-SERIAL", text, StringComparison.Ordinal);
        Assert.DoesNotContain("DIMM-ASSET", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ORPHAN-TAG", text, StringComparison.Ordinal);
        Assert.DoesNotContain("OEM-SECRET", text, StringComparison.Ordinal);
        Assert.All(result.Table[(4 + 4)..(4 + 4 + 16)], value => Assert.Equal(0, value)); // the UUID
        Assert.Contains("Maker", text, StringComparison.Ordinal);
        Assert.Contains("SKU-1", text, StringComparison.Ordinal);
        Assert.Contains("Controller0-ChannelA", text, StringComparison.Ordinal);
        Assert.Contains("CMK16GX4M2B3600C18", text, StringComparison.Ordinal); // the part number is kept (D004)
        Assert.Equal(table.Length, result.Table.Length);
        Assert.Null(result.ZeroedFromOffset);
        Assert.Equal(7, result.FieldsScrubbed);
    }

    [Fact]
    public void Smbios_bytes_that_cannot_be_walked_are_zeroed_and_reported()
    {
        var good = Structure(2, [0x01, 0x00, 0x00, 0x02], "Board", "SN-BOARD"); // serial number (0x07) is string 2
        var table = Concat(good, [0x11, 0x02, 0x00, 0x00, (byte)'S', (byte)'N', (byte)'X']); // length 2 < 4: malformed

        var result = SmbiosScrubber.Scrub(table);

        Assert.Equal(good.Length, result.ZeroedFromOffset);
        Assert.All(result.Table[good.Length..], value => Assert.Equal(0, value));
        Assert.DoesNotContain("SN-BOARD", Encoding.ASCII.GetString(result.Table), StringComparison.Ordinal);
    }

    [Fact]
    public void Smbios_types_without_a_known_layout_are_default_denied_and_the_table_stays_walkable()
    {
        byte[] mac = [0x00, 0x1B, 0x21, 0xAA, 0xBB, 0xCC];
        var oem = Structure(200, [.. mac, 0x01], "OEM-BOARD-ID-77");
        var tpm = Structure(43, [(byte)'I', (byte)'N', (byte)'T', (byte)'C', 0x02, 0x00, 0x01], "TPM Device");
        var slot = Structure(9, [0x01, 0xA5], "PCIEX16_1");
        var table = Concat(oem, tpm, slot, Structure(127, []));

        var result = SmbiosScrubber.Scrub(table);
        var text = Encoding.ASCII.GetString(result.Table);

        Assert.Equal(-1, result.Table.AsSpan().IndexOf(mac));
        Assert.DoesNotContain("OEM-BOARD-ID-77", text, StringComparison.Ordinal);
        Assert.DoesNotContain("TPM Device", text, StringComparison.Ordinal);
        Assert.Contains("PCIEX16_1", text, StringComparison.Ordinal); // system slots: a known layout, kept
        Assert.Equal([200, (byte)oem[1]], result.Table[0..2]); // type and length survive, so the table still walks
        Assert.Null(result.ZeroedFromOffset);
        Assert.Equal(table.Length, result.Table.Length);
    }

    [Fact]
    public void Smbios_battery_manufacture_date_is_overwritten_like_its_serial()
    {
        var battery = Structure(
            22,
            [
                0x01, // 0x04 location
                0x02, // 0x05 manufacturer
                0x03, // 0x06 manufacture date
                0x04, // 0x07 serial number
                0x05, // 0x08 device name
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // 0x09–0x0E
                0x00, // 0x0F
                0x34, 0x12, // 0x10 SBDS serial number
                0x21, 0x43, // 0x12 SBDS manufacture date
            ],
            "Front", "Sunwoda", "6/18/2021", "BAT-SERIAL-1", "HB4593R1ECW");

        var result = SmbiosScrubber.Scrub(Concat(battery, Structure(127, [])));
        var text = Encoding.ASCII.GetString(result.Table);

        Assert.DoesNotContain("6/18/2021", text, StringComparison.Ordinal);
        Assert.DoesNotContain("BAT-SERIAL-1", text, StringComparison.Ordinal);
        Assert.Contains("HB4593R1ECW", text, StringComparison.Ordinal);
        Assert.Equal([0, 0, 0, 0], result.Table[0x10..0x14]);
    }

    [Fact]
    public void Steam_library_content_ids_and_labels_are_redacted_and_paths_and_apps_kept()
    {
        // The current libraryfolders.vdf layout: one block per library with path, label, contentid, sizes and apps.
        const string vdf = "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n\t\t\"label\"\t\t\"\"\n\t\t\"contentid\"\t\t\"6089302862307036814\"\n\t\t\"totalsize\"\t\t\"0\"\n\t\t\"apps\"\n\t\t{\n\t\t\t\"228980\"\t\t\"123456\"\n\t\t}\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"D:\\\\SteamLibrary\"\n\t\t\"label\"\t\t\"Ann's \\\"fast\\\" SSD\"\n\t\t\"ContentID\"\t\t\"1234567891234567891\"\n\t}\n}\n";
        var scrubber = new SteamTextScrubber(new UserProfileRedactor(@"C:\Users\Ann"));

        var scrubbed = scrubber.Scrub(vdf);

        Assert.DoesNotContain("6089302862307036814", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("1234567891234567891", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("fast", scrubbed, StringComparison.Ordinal); // the whole label, across its escaped quotes
        Assert.Contains("\"label\"\t\t\"\"", scrubbed, StringComparison.Ordinal); // an empty label stays empty
        Assert.Contains("\"label\"\t\t\"redacted\"", scrubbed, StringComparison.Ordinal);
        Assert.Contains("\"ContentID\"\t\t\"redacted\"", scrubbed, StringComparison.Ordinal); // keys are case-insensitive
        Assert.Contains("\"228980\"\t\t\"123456\"", scrubbed, StringComparison.Ordinal);
        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], FpsUtility.Core.Raw.SteamLibraryFolders.ParsePaths(scrubbed));
    }

    [Theory]
    // Transitional layout: a top-level contentstatsid equal to library 0's contentid (public samples show this).
    [InlineData("\"libraryfolders\"\n{\n\t\"contentstatsid\"\t\t\"-6181279185307435581\"\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Steam\"\n\t\t\"contentid\"\t\t\"-6181279185307435581\"\n\t}\n}\n")]
    // Older layout: paths as values, and a ContentStatsID as the only id.
    [InlineData("\"LibraryFolders\"\n{\n\t\"TimeNextStatsReport\"\t\t\"1727000000\"\n\t\"ContentStatsID\"\t\t\"-6181279185307435581\"\n\t\"1\"\t\t\"D:\\\\SteamLibrary\"\n}\n")]
    public void Steam_content_stats_id_is_redacted_in_every_layout(string vdf)
    {
        var scrubbed = new SteamTextScrubber(new UserProfileRedactor(@"C:\Users\Ann")).Scrub(vdf);

        Assert.DoesNotContain("6181279185307435581", scrubbed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"C:\Users\Ann\Steam", @"%USERPROFILE%\Steam")]
    [InlineData(@"""path""		""C:\\Users\\Ann\\SteamLibrary""", @"""path""		""%USERPROFILE%\\SteamLibrary""")]
    [InlineData("c:/users/ann/steam", "%USERPROFILE%/steam")]
    [InlineData(@"C:\Users\Anna\Steam", @"C:\Users\Anna\Steam")] // a different user whose name starts the same
    [InlineData(@"C:\Users\Ann", "%USERPROFILE%")]
    public void The_user_profile_path_is_rewritten_only_where_it_ends(string text, string expected)
    {
        var redactor = new UserProfileRedactor(@"C:\Users\Ann");

        Assert.Equal(expected, redactor.Redact(text));
    }

    [Fact]
    public void Steam_account_and_activity_values_are_redacted()
    {
        var scrubber = new SteamTextScrubber(new UserProfileRedactor(@"C:\Users\Ann"));
        const string manifest = "\"AppState\"\n{\n\t\"appid\"\t\t\"730\"\n\t\"LastOwner\"\t\t\"76561190000000000\"\n\t\"lastplayed\"\t\t\"1727000000\"\n\t\"SizeOnDisk\"\t\t\"123\"\n}\n";

        var scrubbed = scrubber.Scrub(manifest);

        Assert.Contains("\"LastOwner\"\t\t\"redacted\"", scrubbed, StringComparison.Ordinal);
        Assert.Contains("\"lastplayed\"\t\t\"redacted\"", scrubbed, StringComparison.Ordinal);
        Assert.Contains("\"SizeOnDisk\"\t\t\"123\"", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("76561190000000000", scrubbed, StringComparison.Ordinal);
    }

    private static byte[] BaseBlock(byte extensions = 0)
    {
        var block = new byte[128];
        byte[] header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];
        header.CopyTo(block, 0);
        new byte[] { 0x12, 0x34, 0x56, 0x78 }.CopyTo(block, 12);
        Descriptor(0xFF, "SN0123456789\n").CopyTo(block, 72);
        Descriptor(0xFC, "TESTMON\n     ").CopyTo(block, 90);
        block[126] = extensions;
        block[127] = Checksum(block.AsSpan(0, 127));
        return block;
    }

    private static byte[] Descriptor(byte tag, string text)
    {
        var descriptor = new byte[18];
        descriptor[3] = tag;
        Encoding.ASCII.GetBytes(text).CopyTo(descriptor, 5);
        return descriptor;
    }

    /// <summary>A DisplayID 2.0 extension with one product-identification block (tag 0x20, 12-byte payload).</summary>
    private static byte[] DisplayIdBlock()
    {
        var block = new byte[128];
        block[0] = 0x70;
        block[1] = 0x20; // DisplayID 2.0
        block[2] = 15; // section length: one 3-byte block header + 12-byte payload
        byte[] productId = [0x20, 0x00, 12, 0xAA, 0xBB, 0xCC, 0x01, 0x02, 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x20, 0x00];
        productId.CopyTo(block, 5);
        block[5 + 15] = Checksum(block.AsSpan(1, 5 + 15 - 1));
        block[127] = Checksum(block.AsSpan(0, 127));
        return block;
    }

    private static byte[] Memory() => Structure(
        17,
        [
            .. new byte[0x10 - 4], // handles, widths, size, form factor, set
            0x01, // 0x10 device locator
            0x02, // 0x11 bank locator
            0x00, 0x00, 0x00, 0x00, 0x00, // 0x12–0x16 type, detail, speed
            0x03, // 0x17 manufacturer
            0x04, // 0x18 serial number
            0x05, // 0x19 asset tag
            0x06, // 0x1A part number
        ],
        "Controller0-ChannelA", "BANK 0", "Corsair", "DIMM-SERIAL", "DIMM-ASSET", "CMK16GX4M2B3600C18", "ORPHAN-TAG");

    private static byte[] Uuid() => [.. Enumerable.Range(1, 16).Select(value => (byte)value)];

    /// <summary>Header (type, length, handle) + the formatted fields after it + the string set.</summary>
    private static byte[] Structure(byte type, byte[] fields, params string[] strings)
    {
        var formatted = new byte[4 + fields.Length];
        formatted[0] = type;
        formatted[1] = (byte)formatted.Length;
        fields.CopyTo(formatted, 4);

        var stringSet = strings.Length == 0
            ? [0, 0]
            : Encoding.ASCII.GetBytes(string.Join('\0', strings) + "\0\0");

        return Concat(formatted, stringSet);
    }

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(part => part)];

    private static byte Checksum(ReadOnlySpan<byte> data)
    {
        var sum = 0;
        foreach (var value in data)
        {
            sum += value;
        }

        return (byte)((0x100 - (sum & 0xFF)) & 0xFF);
    }
}
