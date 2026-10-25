using System.Text;
using Mesura.Core.Privacy;
using Mesura.Platform.Windows;

namespace Mesura.Platform.Tests;

/// <summary>The EDID identifiers capture-fixture proves absent from a fixture (architecture §4.4 Milestone 1.5, step 3).</summary>
public sealed class LocalIdentifierTests
{
    private static readonly byte[] Serial = [0x12, 0x34, 0x56, 0x78];

    [Fact]
    public void Each_edid_field_is_its_own_identifier_even_beside_the_manufacture_date()
    {
        // Bytes 12–15 (serial) and 16–17 (week, year) are both zeroed; merged into one run, the serial alone
        // would never be searched for.
        var fields = LocalIdentifiers.EdidFields(BaseBlock(week: 23, year: 33)).ToList();

        Assert.Contains(fields, field => field.SequenceEqual(Serial));
        Assert.Contains(fields, field => field.SequenceEqual(new byte[] { 23, 33 }));
    }

    [Fact]
    public void A_copy_of_the_serial_left_anywhere_in_a_fixture_is_found()
    {
        var identifiers = LocalIdentifiers.EdidFields(BaseBlock(week: 23, year: 33)).Select(field => LocalIdentifier.Binary("monitor EDID identifier", field));
        byte[] leftover = [0x70, 0x20, 0x16, .. new byte[5], .. Serial, 0x00];
        var json = Encoding.UTF8.GetBytes($$"""{"bytes": "{{Convert.ToBase64String(leftover)}}"}""");

        Assert.Equal(["monitor EDID identifier"], IdentifierLeakCheck.FindLeaks(json, identifiers));
    }

    [Fact]
    public void A_buffer_that_is_not_an_edid_has_no_fields()
    {
        Assert.Empty(LocalIdentifiers.EdidFields(new byte[128]));
    }

    private static byte[] BaseBlock(byte week, byte year)
    {
        var block = new byte[128];
        byte[] header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];
        header.CopyTo(block, 0);
        Serial.CopyTo(block, 12);
        block[16] = week;
        block[17] = year;
        var sum = block.Take(127).Sum(value => value);
        block[127] = (byte)((0x100 - (sum & 0xFF)) & 0xFF);
        return block;
    }
}
