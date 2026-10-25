using System.Text;
using Mesura.Core.Privacy;

namespace Mesura.Core.Tests;

/// <summary>
/// The EDID scrub beyond the base block (architecture §13, Milestone 1.5 review): serials and container IDs in
/// DisplayID and CTA-861 extensions are removed, and content the scrubber doesn't understand is removed too,
/// while the timing and capability data the display checks need survives byte for byte. Blocks are built from
/// the VESA and CTA layouts, not taken from any real monitor.
/// </summary>
public sealed class EdidScrubberTests
{
    private static readonly byte[] BaseSerial = [0x12, 0x34, 0x56, 0x78];

    [Fact]
    public void A_tile_serial_that_repeats_the_base_serial_survives_nowhere()
    {
        // Tiled monitors (Dell UP3218K, Apple XDR, LG UltraFine 5K) repeat the base serial as the tile serial.
        byte[] tile = [.. Enumerable.Range(1, 0x12).Select(value => (byte)value), .. BaseSerial];
        var edid = Edid(DisplayId(0x20, Block(0x28, tile)));

        var result = EdidScrubber.Scrub(edid);

        Assert.Equal(-1, result.Bytes.AsSpan().IndexOf(BaseSerial));
        var payload = FirstPayload(result.Bytes);
        Assert.Equal(tile[..0x12], payload[..0x12]); // the tile layout is kept
        Assert.Equal([0, 0, 0, 0], payload[0x12..0x16]);
        AssertChecksumsValid(result.Bytes);
    }

    [Fact]
    public void A_displayid_container_id_is_zeroed()
    {
        var containerId = Enumerable.Range(0xA0, 16).Select(value => (byte)value).ToArray();

        var result = EdidScrubber.Scrub(Edid(DisplayId(0x20, Block(0x29, containerId))));

        Assert.All(FirstPayload(result.Bytes), value => Assert.Equal(0, value));
        AssertChecksumsValid(result.Bytes);
    }

    [Theory]
    [InlineData(0x0A)] // DisplayID 1.3 product serial number
    [InlineData(0x0B)] // DisplayID 1.3 general-purpose ASCII string
    [InlineData(0x50)] // a tag DisplayID doesn't define: default-deny
    public void A_displayid_block_that_can_hold_an_identifier_or_is_unknown_is_zeroed(byte tag)
    {
        var text = Encoding.ASCII.GetBytes("SN-7F3K2Q9");

        var result = EdidScrubber.Scrub(Edid(DisplayId(0x12, Block(tag, text))));

        Assert.All(FirstPayload(result.Bytes), value => Assert.Equal(0, value));
    }

    [Fact]
    public void An_hmd_block_loses_its_serial_and_keeps_the_rest()
    {
        byte[] hmd = [0x01, 0x02, 0x03, 0x04, 0x05, .. BaseSerial, 0x09, 0x0A];

        var result = EdidScrubber.Scrub(Edid(DisplayId(0x20, Block(0x2C, hmd))));

        Assert.Equal([0x01, 0x02, 0x03, 0x04, 0x05, 0, 0, 0, 0, 0x09, 0x0A], FirstPayload(result.Bytes));
    }

    [Fact]
    public void Displayid_timing_blocks_and_vesa_vendor_data_are_kept_byte_for_byte()
    {
        var timing = Enumerable.Range(1, 20).Select(value => (byte)value).ToArray(); // Type VII detailed timing
        byte[] vesa = [0x3A, 0x02, 0x92, 0x81, 0x00];

        var edid = Edid(DisplayId(0x20, Block(0x22, timing), Block(0x7E, vesa)));
        var result = EdidScrubber.Scrub(edid);

        Assert.Equal(edid[128..], result.Bytes[128..]);
        Assert.Equal(2, result.FieldsScrubbed); // only the base block's serial and serial descriptor
    }

    [Fact]
    public void An_unknown_vendor_block_keeps_its_oui_and_loses_its_data()
    {
        byte[] apple = [0x00, 0x10, 0xFA, 0x11, 0x22, 0x33];

        var result = EdidScrubber.Scrub(Edid(DisplayId(0x12, Block(0x7F, apple))));

        Assert.Equal([0x00, 0x10, 0xFA, 0, 0, 0], FirstPayload(result.Bytes));
    }

    [Fact]
    public void A_cta_block_keeps_capability_data_and_loses_container_id_unknown_vendor_data_and_serial_text()
    {
        byte[] video = [0x43, 0x10, 0x1F, 0x04]; // video data block: three short video descriptors
        byte[] hdmi = [0x67, 0x03, 0x0C, 0x00, 0x10, 0x00, 0x00, 0x78]; // HDMI 1.4 VSDB: address, max TMDS clock
        byte[] hdr = [0xE3, 0x06, 0x05, 0x01]; // extended: HDR static metadata
        byte[] microsoftContainer = [.. Enumerable.Range(0xC0, 16).Select(value => (byte)value)];
        byte[] microsoft = [0x75, 0x5C, 0x12, 0xCA, 0x03, 0x00, .. microsoftContainer]; // Microsoft display VSDB
        byte[] nvidia = [0x65, 0x4B, 0x04, 0x00, 0xAA, 0xBB]; // a vendor whose payload layout isn't public
        byte[] unknownExtended = [0xE3, 0x40, 0x11, 0x22]; // an extended tag CTA-861-I doesn't define
        byte[] timingDescriptor = [0x02, 0x3A, 0x80, 0x18, 0x71, 0x38, 0x2D, 0x40, 0x58, 0x2C, 0x45, 0x00, 0x50, 0x1D, 0x74, 0x00, 0x00, 0x1E];
        byte[] collection = [.. video, .. hdmi, .. hdr, .. microsoft, .. nvidia, .. unknownExtended];

        var edid = Edid(Cta(collection, timingDescriptor, SerialDescriptor("SN0123456789")));
        var result = EdidScrubber.Scrub(edid);
        var block = result.Bytes[128..256];

        var position = 4;
        Assert.Equal(video, Take(block, ref position, video.Length));
        Assert.Equal(hdmi, Take(block, ref position, hdmi.Length));
        Assert.Equal(hdr, Take(block, ref position, hdr.Length));
        Assert.Equal([0x75, 0x5C, 0x12, 0xCA, 0x03, 0x00, .. new byte[16]], Take(block, ref position, microsoft.Length));
        Assert.Equal([0x65, 0x4B, 0x04, 0x00, 0x00, 0x00], Take(block, ref position, nvidia.Length));
        Assert.Equal([0xE3, 0x40, 0x00, 0x00], Take(block, ref position, unknownExtended.Length));
        Assert.Equal(timingDescriptor, Take(block, ref position, 18));
        Assert.DoesNotContain("SN0123456789", Encoding.ASCII.GetString(result.Bytes), StringComparison.Ordinal);
        Assert.Equal(-1, result.Bytes.AsSpan().IndexOf(microsoftContainer));
        AssertChecksumsValid(result.Bytes);
    }

    [Fact]
    public void A_cta_collection_that_cannot_be_walked_is_zeroed_from_where_it_breaks()
    {
        byte[] video = [0x43, 0x10, 0x1F, 0x04];
        byte[] overrun = [0x7F, 0x5C, 0x12, 0xCA]; // claims 31 bytes; only 3 remain in the collection

        var result = EdidScrubber.Scrub(Edid(Cta([.. video, .. overrun])));
        var block = result.Bytes[128..256];

        Assert.Equal(video, block[4..8]);
        Assert.Equal([0, 0, 0, 0], block[8..12]);
    }

    [Theory]
    [InlineData(0x50)] // localized strings: carry serial-number strings
    [InlineData(0x40)] // display information
    [InlineData(0xFF)] // manufacturer-defined
    [InlineData(0x33)] // no such extension
    public void An_extension_the_scrubber_does_not_walk_keeps_its_tag_and_loses_its_payload(byte tag)
    {
        var result = EdidScrubber.Scrub(Edid(Extension(tag, Encoding.ASCII.GetBytes("SERIAL 12345678"))));

        Assert.Equal(tag, result.Bytes[128]);
        Assert.All(result.Bytes[129..255], value => Assert.Equal(0, value));
        AssertChecksumsValid(result.Bytes);
    }

    [Theory]
    [InlineData(0x10)] // video timing extension
    [InlineData(0xF0)] // block map
    public void Timing_and_block_map_extensions_are_kept(byte tag)
    {
        var edid = Edid(Extension(tag, [0x01, 0x02, 0x03, 0x04]));

        Assert.Equal(edid[128..], EdidScrubber.Scrub(edid).Bytes[128..]);
    }

    [Fact]
    public void A_manufacture_week_and_year_are_zeroed_but_a_model_year_is_kept()
    {
        var manufactured = Edid();
        manufactured[16] = 23; // week 23
        manufactured[17] = 33; // 2023
        var modelYear = Edid();
        modelYear[16] = 0xFF; // "the year is a model year"
        modelYear[17] = 33;

        Assert.Equal([0, 0], EdidScrubber.Scrub(Fix(manufactured)).Bytes[16..18]);
        Assert.Equal([0xFF, 33], EdidScrubber.Scrub(Fix(modelYear)).Bytes[16..18]);
    }

    [Theory]
    [InlineData(0xFE)] // alphanumeric data string: free text
    [InlineData(0x0A)] // manufacturer-specified: the vendor decides the content
    [InlineData(0x42)] // reserved
    public void A_display_descriptor_of_a_kind_that_can_hold_anything_loses_its_content(byte tag)
    {
        var edid = Edid();
        var descriptor = new byte[18];
        descriptor[3] = tag;
        Encoding.ASCII.GetBytes("UNIT-99887766").CopyTo(descriptor, 5);
        descriptor.CopyTo(edid, 108);

        var result = EdidScrubber.Scrub(Fix(edid));

        Assert.All(result.Bytes[(108 + 5)..(108 + 18)], value => Assert.Equal(0, value));
        Assert.Equal(tag, result.Bytes[108 + 3]);
    }

    [Theory]
    [InlineData(0xFC)] // monitor name
    [InlineData(0xFD)] // range limits
    public void A_display_descriptor_with_a_public_identifier_free_layout_is_kept(byte tag)
    {
        var edid = Edid();
        var descriptor = new byte[18];
        descriptor[3] = tag;
        Encoding.ASCII.GetBytes("TESTMON\n     ").CopyTo(descriptor, 5);
        descriptor.CopyTo(edid, 108);

        var result = EdidScrubber.Scrub(Fix(edid));

        Assert.Equal(descriptor, result.Bytes[108..126]);
    }

    [Fact]
    public void A_microsoft_vsdb_too_short_for_its_layout_keeps_only_its_oui()
    {
        byte[] shortMicrosoft = [0x6A, 0x5C, 0x12, 0xCA, 0x03, 0x00, 0xC0, 0xC1, 0xC2, 0xC3, 0xC4]; // length 10, not 21

        var result = EdidScrubber.Scrub(Edid(Cta(shortMicrosoft)));

        Assert.Equal([0x6A, 0x5C, 0x12, 0xCA, 0, 0, 0, 0, 0, 0, 0], result.Bytes[132..143]);
    }

    [Fact]
    public void Each_zeroed_field_is_reported_separately_with_its_position()
    {
        var edid = Edid();
        edid[16] = 23; // manufacture week, right after the serial
        edid[17] = 33;

        var result = EdidScrubber.Scrub(Fix(edid));

        Assert.Contains(new Range(12, 16), result.ZeroedFields); // the serial alone
        Assert.Contains(new Range(16, 18), result.ZeroedFields); // the date alone
        Assert.Equal(result.FieldsScrubbed, result.ZeroedFields.Count);
    }

    [Fact]
    public void Scrubbing_twice_changes_nothing_the_second_time()
    {
        byte[] tile = [.. new byte[0x12], .. BaseSerial];
        var edid = Edid(
            DisplayId(0x20, Block(0x28, tile), Block(0x29, new byte[16] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 })),
            Cta([0x75, 0x5C, 0x12, 0xCA, 0x03, 0x00, .. Enumerable.Range(1, 16).Select(value => (byte)value)], SerialDescriptor("SN42424242")));

        var once = EdidScrubber.Scrub(edid);
        var twice = EdidScrubber.Scrub(once.Bytes);

        Assert.True(once.FieldsScrubbed > 0);
        Assert.Equal(0, twice.FieldsScrubbed);
        Assert.Equal(once.Bytes, twice.Bytes);
    }

    private static byte[] Take(byte[] block, ref int position, int length)
    {
        var slice = block[position..(position + length)];
        position += length;
        return slice;
    }

    /// <summary>A base block with the given extensions: serial, a serial descriptor and a name descriptor.</summary>
    private static byte[] Edid(params byte[][] extensions)
    {
        var block = new byte[128];
        byte[] header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];
        header.CopyTo(block, 0);
        BaseSerial.CopyTo(block, 12);
        SerialDescriptor("SN0123456789").CopyTo(block, 72);
        block[126] = (byte)extensions.Length;
        return Fix([.. block, .. extensions.SelectMany(extension => extension)]);
    }

    /// <summary>Recomputes every block's checksum, as a monitor's firmware would have.</summary>
    private static byte[] Fix(byte[] edid)
    {
        for (var i = 0; i < edid.Length / 128; i++)
        {
            edid[(i * 128) + 127] = Checksum(edid.AsSpan(i * 128, 127));
        }

        return edid;
    }

    private static byte[] Extension(byte tag, byte[] content)
    {
        var block = new byte[128];
        block[0] = tag;
        content.CopyTo(block, 1);
        return block;
    }

    /// <summary>A DisplayID extension: version, section length, product type, extension count, data blocks, section checksum.</summary>
    private static byte[] DisplayId(byte version, params byte[][] dataBlocks)
    {
        var data = dataBlocks.SelectMany(dataBlock => dataBlock).ToArray();
        var block = new byte[128];
        block[0] = 0x70;
        block[1] = version;
        block[2] = (byte)data.Length;
        data.CopyTo(block, 5);
        block[5 + data.Length] = Checksum(block.AsSpan(1, 4 + data.Length));
        return block;
    }

    private static byte[] Block(byte tag, byte[] payload) => [tag, 0x00, (byte)payload.Length, .. payload];

    /// <summary>The payload of the first DisplayID data block in the first extension.</summary>
    private static byte[] FirstPayload(byte[] edid)
    {
        int length = edid[128 + 5 + 2];
        return edid[(128 + 5 + 3)..(128 + 5 + 3 + length)];
    }

    /// <summary>A CTA-861 revision 3 extension: the data block collection, then detailed timing descriptors.</summary>
    private static byte[] Cta(byte[] collection, params byte[][] descriptors)
    {
        var block = new byte[128];
        block[0] = 0x02;
        block[1] = 0x03;
        block[2] = (byte)(4 + collection.Length);
        collection.CopyTo(block, 4);
        var position = 4 + collection.Length;
        foreach (var descriptor in descriptors)
        {
            descriptor.CopyTo(block, position);
            position += descriptor.Length;
        }

        return block;
    }

    private static byte[] SerialDescriptor(string serial)
    {
        var descriptor = new byte[18];
        descriptor[3] = 0xFF;
        Encoding.ASCII.GetBytes((serial + "\n").PadRight(13)).CopyTo(descriptor, 5);
        return descriptor;
    }

    private static void AssertChecksumsValid(byte[] edid)
    {
        for (var i = 0; i < edid.Length / 128; i++)
        {
            Assert.Equal(0, edid.AsSpan(i * 128, 128).ToArray().Sum(value => value) & 0xFF);
        }
    }

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
