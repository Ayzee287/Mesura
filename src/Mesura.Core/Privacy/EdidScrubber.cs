using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Mesura.Core.Privacy;

/// <summary>
/// Removes the unit identifiers from a stored EDID before it is kept (architecture §4.4, §13). EDID
/// bytes have the same form on every platform (§4.4 rule 2), so this lives in Core and any collector
/// uses it. It locates fields; it does not interpret the EDID (that is the normaliser's job).
/// </summary>
/// <remarks>
/// <para>
/// The scrub is <b>default-deny</b>: content the scrubber doesn't understand is zeroed, and content it
/// understands keeps everything except the fields that identify one unit. Timing, capability and model
/// data survive, because the display checks need them. Field positions follow VESA E-EDID 1.4,
/// DisplayID 1.3 and 2.x, CTA-861 and Microsoft's display VSDB, cross-checked with edid-decode's
/// <c>--replace-unique-ids</c> (v4l-utils 91dda32).
/// </para>
/// <list type="bullet">
/// <item><b>Base block:</b> the ID serial number (bytes 12–15); the manufacture week and year (16–17),
/// unless the week is 0xFF, which makes the year a model year; the text of display descriptors other than
/// the public, identifier-free kinds (name 0xFC, range limits 0xFD, colour point 0xFB, standard timings 0xFA,
/// colour management 0xF9, CVT codes 0xF8, established timings 0xF7, dummy 0x10). So the serial number
/// (0xFF), free text (0xFE), manufacturer-specified (0x00–0x0F) and reserved kinds lose their content.</item>
/// <item><b>CTA-861 extension (0x02):</b> the data block collection is walked. Audio, video, speaker,
/// colorimetry, HDR, timing and the other named capability blocks are kept. A vendor-specific block keeps
/// its OUI; its payload is kept only for OUIs whose layout is public and holds no unit identifier (HDMI,
/// HDMI Forum, AMD, HDR10+, Dolby). Microsoft's display VSDB keeps everything except its 16-byte
/// container ID (and loses its whole payload if it is too short to hold one). Any other vendor payload,
/// any reserved or unknown tag, and a collection that can't be walked are zeroed. In the detailed-timing
/// area, display descriptors are treated as in the base block, and padding after the last descriptor is
/// zeroed.</item>
/// <item><b>DisplayID extension (0x70):</b> the section is walked. Timing, parameter and interface
/// blocks are kept. Product identification keeps manufacturer, product code and name, and loses its
/// serial number and manufacture week and year. Tiled topology loses its tile serial; the AR/VR HMD
/// block loses its serial. The serial-number (0x0A), ASCII string (0x0B) and container ID (0x29) blocks
/// are zeroed. Vendor-specific blocks keep their OUI and payload only for VESA. An embedded CTA block
/// (0x81) is walked like a CTA collection. Unknown tags and anything that can't be walked are
/// zeroed.</item>
/// <item><b>Video timing (0x10) and block map (0xF0) extensions</b> hold no identifiers and are kept.
/// <b>Every other extension</b> (localized strings 0x50, which carry serial-number strings; display
/// information 0x40; manufacturer-defined 0xFF; anything unknown) keeps its tag and loses its
/// payload.</item>
/// <item>Bytes after the last whole 128-byte block are zeroed.</item>
/// </list>
/// <para>
/// Every block the scrub changes gets a recomputed checksum (and a DisplayID section its section
/// checksum); the original validity of every block is returned first.
/// </para>
/// </remarks>
public static class EdidScrubber
{
    public const int BlockLength = 128;

    private const byte CtaExtensionTag = 0x02;
    private const byte VideoTimingExtensionTag = 0x10;
    private const byte DisplayIdExtensionTag = 0x70;
    private const byte BlockMapExtensionTag = 0xF0;
    private const int DisplayIdDataStart = 5;
    private const int DisplayIdMaxSectionLength = BlockLength - 1 - DisplayIdDataStart - 1; // leaves the section and block checksums
    private const int DescriptorLength = 18;

    private static readonly int[] BaseDescriptorOffsets = [54, 72, 90, 108];

    /// <summary>Display descriptor kinds with a public layout and no unit identifier (E-EDID 1.4 §3.10.3).</summary>
    private static readonly HashSet<byte> KeptDescriptorTags = [0x10, 0xF7, 0xF8, 0xF9, 0xFA, 0xFB, 0xFC, 0xFD];

    /// <summary>CTA vendor-specific payloads kept after their OUI: public layouts with no unit identifier.</summary>
    private static readonly HashSet<int> KeptCtaVendors =
    [
        0x000C03, // HDMI Licensing (HDMI 1.4 VSDB: source address, TMDS clock, features)
        0xC45DD8, // HDMI Forum (HF-VSDB: TMDS character rate, FRL, VRR range)
        0x00001A, // AMD (FreeSync range)
        0x90848B, // HDR10+ (vendor-specific video block)
        0x00D046, // Dolby (vendor-specific video and audio blocks)
    ];

    /// <summary>DisplayID vendor-specific payloads kept after their OUI: VESA's (DisplayPort and MSO data).</summary>
    private static readonly HashSet<int> KeptDisplayIdVendors = [0x3A0292];

    private const int MicrosoftOui = 0xCA125C;
    private const int MicrosoftVsdbPayloadLength = 21; // OUI (3), version and usage (2), container ID (16)

    /// <summary>CTA extended tags whose blocks hold capability data only (CTA-861-I and HDMI 2.1 tables).</summary>
    private static readonly HashSet<byte> KeptCtaExtendedTags =
    [
        0x00, 0x02, 0x03, 0x05, 0x06, 0x07, 0x08, 0x0D, 0x0E, 0x0F, 0x12, 0x13, 0x14,
        0x20, 0x21, 0x22, 0x23, 0x2A, 0x78, 0x79, 0x7A,
    ];

    private const byte CtaVendorVideoExtendedTag = 0x01;
    private const byte CtaVendorAudioExtendedTag = 0x11;

    /// <summary>DisplayID data blocks that hold timing, parameter or interface data only.</summary>
    private static readonly HashSet<byte> KeptDisplayIdTags =
    [
        0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0C, 0x0D, 0x0E, 0x0F, 0x10, 0x11, 0x13,
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x2A, 0x2B, 0x2D, 0x2E,
    ];

    /// <param name="Bytes">The scrubbed buffer, the same length as the stored one.</param>
    /// <param name="ChecksumValidBeforeScrub">Per whole 128-byte block: whether its checksum was valid as stored.</param>
    /// <param name="FieldsScrubbed">How many fields held non-zero data and were zeroed.</param>
    /// <param name="ZeroedFields">
    /// Where each zeroed field lies in the buffer, one range per field, in the order found. A serial is its own
    /// field even when the manufacture date beside it is zeroed too, so a caller can look for each value
    /// separately (the fixture leak check does). Recomputed checksums are not fields.
    /// </param>
    public sealed record Result(byte[] Bytes, IReadOnlyList<bool> ChecksumValidBeforeScrub, int FieldsScrubbed, IReadOnlyList<Range> ZeroedFields);

    /// <exception cref="FormatException">The buffer doesn't start with an EDID base block, so its serial fields can't be located.</exception>
    public static Result Scrub(ReadOnlySpan<byte> stored)
    {
        ReadOnlySpan<byte> header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];
        if (stored.Length < BlockLength || !stored[..header.Length].SequenceEqual(header))
        {
            throw new FormatException($"A {stored.Length}-byte buffer without the EDID header is not an EDID base block.");
        }

        var bytes = stored.ToArray();
        var fields = new Fields(bytes);
        var blockCount = bytes.Length / BlockLength;
        var checksumValid = new bool[blockCount];

        for (var i = 0; i < blockCount; i++)
        {
            checksumValid[i] = ByteSum(Block(bytes, i)) == 0;
        }

        for (var i = 0; i < blockCount; i++)
        {
            var block = Block(bytes, i);
            var count = i == 0 ? ScrubBaseBlock(block, fields) : ScrubExtension(block, fields);
            if (count > 0)
            {
                block[^1] = ChecksumFor(block[..^1]);
            }
        }

        fields.Zero(bytes.AsSpan(blockCount * BlockLength));

        return new Result(bytes, checksumValid, fields.Found.Count, fields.Found);
    }

    private static int ScrubBaseBlock(Span<byte> block, Fields fields)
    {
        var count = fields.Zero(block.Slice(12, 4));
        count += ScrubManufactureDate(block.Slice(16, 2), fields);

        foreach (var offset in BaseDescriptorOffsets)
        {
            count += ScrubDescriptor(block.Slice(offset, DescriptorLength), fields);
        }

        return count;
    }

    private static int ScrubExtension(Span<byte> block, Fields fields) => block[0] switch
    {
        CtaExtensionTag => ScrubCta(block, fields),
        DisplayIdExtensionTag => ScrubDisplayId(block, fields),
        VideoTimingExtensionTag or BlockMapExtensionTag => 0,
        _ => fields.Zero(block[1..^1]),
    };

    /// <summary>A week and year pair; a week of 0xFF means the year is a model year, which is not unit data.</summary>
    private static int ScrubManufactureDate(Span<byte> weekAndYear, Fields fields) =>
        weekAndYear[0] != 0xFF ? fields.Zero(weekAndYear) : 0;

    /// <summary>
    /// An 18-byte slot that is a display descriptor (its first two bytes are zero) loses its content (bytes
    /// 5–17) unless its kind is public and identifier-free. A detailed timing descriptor is left alone.
    /// </summary>
    private static int ScrubDescriptor(Span<byte> descriptor, Fields fields)
    {
        var isDisplayDescriptor = descriptor[0] == 0 && descriptor[1] == 0;
        return isDisplayDescriptor && !KeptDescriptorTags.Contains(descriptor[3]) ? fields.Zero(descriptor[5..]) : 0;
    }

    /// <summary>
    /// CTA-861 extension: byte 2 is the offset <c>d</c> of the detailed timings; the data block collection
    /// is bytes 4..d-1 (revision 3 and later). <c>d</c> of 0 means neither exists.
    /// </summary>
    private static int ScrubCta(Span<byte> block, Fields fields)
    {
        int timingsOffset = block[2];
        var payloadEnd = BlockLength - 1; // the checksum
        if (timingsOffset < 4 || timingsOffset >= payloadEnd)
        {
            // Nothing is declared (0), or the offset is invalid: nothing here can be located, so all of it goes.
            return fields.Zero(block[4..payloadEnd]);
        }

        // Data blocks exist from revision 3; before that, bytes 4..d-1 have no defined content.
        var collection = block[4..timingsOffset];
        var count = block[1] >= 3 ? ScrubCtaCollection(collection, fields) : fields.Zero(collection);

        var position = timingsOffset;
        while (position + DescriptorLength <= payloadEnd)
        {
            var slot = block.Slice(position, DescriptorLength);
            if (!slot.ContainsAnyExcept((byte)0))
            {
                break;
            }

            count += ScrubDescriptor(slot, fields);
            position += DescriptorLength;
        }

        // Padding after the last descriptor has no meaning; anything there is unexplained.
        return count + fields.Zero(block[position..payloadEnd]);
    }

    /// <summary>Walks a CTA data block collection (used by CTA extensions and DisplayID's embedded CTA block).</summary>
    private static int ScrubCtaCollection(Span<byte> collection, Fields fields)
    {
        var count = 0;
        var position = 0;

        while (position < collection.Length)
        {
            var tag = collection[position] >> 5;
            var length = collection[position] & 0x1F;
            if (position + 1 + length > collection.Length)
            {
                // A block runs past the collection: the rest can't be walked.
                count += fields.Zero(collection[position..]);
                break;
            }

            var payload = collection.Slice(position + 1, length);
            count += tag switch
            {
                1 or 2 or 4 or 5 or 6 => 0, // audio, video, speaker allocation, VESA transfer characteristics, video format
                3 => ScrubVendorPayload(payload, KeptCtaVendors, fields),
                7 => ScrubCtaExtended(payload, fields),
                _ => fields.Zero(payload), // 0: reserved
            };

            position += 1 + length;
        }

        return count;
    }

    private static int ScrubCtaExtended(Span<byte> payload, Fields fields)
    {
        if (payload.IsEmpty)
        {
            return 0;
        }

        var extendedTag = payload[0];
        var body = payload[1..];
        if (extendedTag is CtaVendorVideoExtendedTag or CtaVendorAudioExtendedTag)
        {
            return ScrubVendorPayload(body, KeptCtaVendors, fields);
        }

        return KeptCtaExtendedTags.Contains(extendedTag) ? 0 : fields.Zero(body);
    }

    /// <summary>
    /// A vendor-specific payload: a 3-byte OUI, then the vendor's data. Microsoft's display VSDB keeps all but
    /// its container ID (payload bytes 5–20); a kept vendor keeps everything; any other vendor, and a Microsoft
    /// block too short for its documented layout, keeps only the OUI.
    /// </summary>
    /// <remarks>CTA stores the OUI little-endian and DisplayID big-endian, and firmware gets this wrong both ways, so both orders are compared.</remarks>
    private static int ScrubVendorPayload(Span<byte> payload, HashSet<int> keptVendors, Fields fields)
    {
        if (payload.Length < 3)
        {
            return fields.Zero(payload);
        }

        var littleEndian = (payload[2] << 16) | (payload[1] << 8) | payload[0];
        var bigEndian = (payload[0] << 16) | (payload[1] << 8) | payload[2];

        if ((littleEndian == MicrosoftOui || bigEndian == MicrosoftOui) && payload.Length >= MicrosoftVsdbPayloadLength)
        {
            return fields.Zero(payload.Slice(5, 16));
        }

        if (keptVendors.Contains(littleEndian) || keptVendors.Contains(bigEndian))
        {
            return 0;
        }

        return fields.Zero(payload[3..]);
    }

    /// <summary>Walks a DisplayID section inside an EDID extension block (data blocks from byte 5).</summary>
    private static int ScrubDisplayId(Span<byte> block, Fields fields)
    {
        int sectionLength = block[2];
        if (sectionLength > DisplayIdMaxSectionLength)
        {
            // The section claims more bytes than the block holds: zero everything after the tag.
            return fields.Zero(block[1..^1]);
        }

        var end = DisplayIdDataStart + sectionLength; // index of the section checksum
        var count = 0;
        var position = DisplayIdDataStart;

        while (position + 3 <= end)
        {
            var tag = block[position];
            var payloadLength = block[position + 2];
            var payloadStart = position + 3;

            if (payloadStart + payloadLength > end)
            {
                // A data block runs past its section: the rest can't be walked, so zero it.
                count += fields.Zero(block[position..end]);
                position = end;
                break;
            }

            count += ScrubDisplayIdBlock(tag, block.Slice(payloadStart, payloadLength), fields);
            position = payloadStart + payloadLength;
        }

        // Fewer than 3 bytes left before the checksum can't hold a data block: it is padding.
        count += fields.Zero(block[position..end]);

        // Bytes between the section checksum and the block checksum are outside the section.
        count += fields.Zero(block[(end + 1)..^1]);

        if (count > 0)
        {
            block[end] = ChecksumFor(block[1..end]);
        }

        return count;
    }

    private static int ScrubDisplayIdBlock(byte tag, Span<byte> payload, Fields fields)
    {
        switch (tag)
        {
            case 0x00 or 0x20:
                // Product identification: OUI or manufacturer (3), product code (2), serial number (4),
                // manufacture week (1) and year (1), name length and name.
                var count = payload.Length >= 9 ? fields.Zero(payload.Slice(5, 4)) : 0;
                return count + (payload.Length >= 11 ? ScrubManufactureDate(payload.Slice(9, 2), fields) : 0);
            case 0x12 or 0x28:
                // Tiled display topology: the tile serial number is payload bytes 0x12–0x15.
                return payload.Length >= 0x16 ? fields.Zero(payload.Slice(0x12, 4)) : 0;
            case 0x2C:
                // AR/VR head-mounted display: the HMD serial number is payload bytes 5–8.
                return payload.Length >= 9 ? fields.Zero(payload.Slice(5, 4)) : 0;
            case 0x7E or 0x7F:
                return ScrubVendorPayload(payload, KeptDisplayIdVendors, fields);
            case 0x81:
                // CTA-861 data blocks carried inside DisplayID: the payload is a CTA data block collection.
                return ScrubCtaCollection(payload, fields);
            default:
                // Serial number (0x0A), ASCII string (0x0B), container ID (0x29), and anything unknown.
                return KeptDisplayIdTags.Contains(tag) ? 0 : fields.Zero(payload);
        }
    }

    private static Span<byte> Block(byte[] bytes, int index) => bytes.AsSpan(index * BlockLength, BlockLength);

    private static int ByteSum(ReadOnlySpan<byte> data)
    {
        var sum = 0;
        foreach (var value in data)
        {
            sum += value;
        }

        return sum & 0xFF;
    }

    /// <summary>The byte that makes the sum of <paramref name="data"/> plus itself 0 modulo 256.</summary>
    private static byte ChecksumFor(ReadOnlySpan<byte> data) => (byte)((0x100 - ByteSum(data)) & 0xFF);

    /// <summary>Zeroes fields of one buffer and records where each one was.</summary>
    private sealed class Fields(byte[] buffer)
    {
        public List<Range> Found { get; } = [];

        /// <summary>Zeroes a field of the buffer; 1 if it held any non-zero byte (and is recorded), else 0.</summary>
        public int Zero(Span<byte> field)
        {
            if (!field.ContainsAnyExcept((byte)0))
            {
                return 0;
            }

            var start = (int)Unsafe.ByteOffset(ref MemoryMarshal.GetArrayDataReference(buffer), ref MemoryMarshal.GetReference(field));
            Found.Add(new Range(start, start + field.Length));
            field.Clear();
            return 1;
        }
    }
}
