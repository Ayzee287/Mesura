namespace FpsUtility.Core.Privacy;

/// <summary>
/// Zeroes the serial fields of a stored EDID before it is kept (architecture §4.4, §13). EDID bytes
/// have the same form on every platform (§4.4 rule 2), so this lives in Core and any collector uses it.
/// It locates fields; it does not interpret the EDID (that is the normaliser's job, step 4).
/// </summary>
/// <remarks>
/// Zeroed: the base block's ID serial number (bytes 12–15); the text of every display descriptor
/// tagged 0xFF (display product serial number); the serial number in DisplayID product-identification
/// data blocks (tags 0x00 and 0x20); the whole payload of manufacturer-defined extension blocks
/// (tag 0xFF), whose content is unknown; and any bytes after the last whole 128-byte block. A DisplayID
/// section that can't be walked has its data blocks zeroed. Every block the scrub changes gets a
/// recomputed checksum; the original validity of every block is returned first.
/// </remarks>
public static class EdidScrubber
{
    public const int BlockLength = 128;

    private const byte DisplayIdExtensionTag = 0x70;
    private const byte ManufacturerExtensionTag = 0xFF;
    private const int DisplayIdDataStart = 5;
    private const int DisplayIdMaxSectionLength = BlockLength - 1 - DisplayIdDataStart - 1; // leaves the section and block checksums

    private static readonly int[] DescriptorOffsets = [54, 72, 90, 108];

    /// <param name="Bytes">The scrubbed buffer, the same length as the stored one.</param>
    /// <param name="ChecksumValidBeforeScrub">Per whole 128-byte block: whether its checksum was valid as stored.</param>
    /// <param name="FieldsScrubbed">How many fields held non-zero data and were zeroed.</param>
    public sealed record Result(byte[] Bytes, IReadOnlyList<bool> ChecksumValidBeforeScrub, int FieldsScrubbed);

    /// <exception cref="FormatException">The buffer doesn't start with an EDID base block, so its serial fields can't be located.</exception>
    public static Result Scrub(ReadOnlySpan<byte> stored)
    {
        ReadOnlySpan<byte> header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];
        if (stored.Length < BlockLength || !stored[..header.Length].SequenceEqual(header))
        {
            throw new FormatException($"A {stored.Length}-byte buffer without the EDID header is not an EDID base block.");
        }

        var bytes = stored.ToArray();
        var blockCount = bytes.Length / BlockLength;
        var checksumValid = new bool[blockCount];
        var modified = new bool[blockCount];
        var scrubbed = 0;

        for (var i = 0; i < blockCount; i++)
        {
            checksumValid[i] = ByteSum(Block(bytes, i)) == 0;
        }

        var baseBlock = Block(bytes, 0);
        if (Zero(baseBlock.Slice(12, 4)))
        {
            scrubbed++;
            modified[0] = true;
        }

        foreach (var offset in DescriptorOffsets)
        {
            var descriptor = baseBlock.Slice(offset, 18);
            var isSerialDescriptor = descriptor[0] == 0 && descriptor[1] == 0 && descriptor[3] == 0xFF;
            if (isSerialDescriptor && Zero(descriptor[5..]))
            {
                scrubbed++;
                modified[0] = true;
            }
        }

        for (var i = 1; i < blockCount; i++)
        {
            var block = Block(bytes, i);
            var count = block[0] switch
            {
                DisplayIdExtensionTag => ScrubDisplayId(block),
                ManufacturerExtensionTag => Zero(block[1..^1]) ? 1 : 0,
                _ => 0,
            };

            if (count > 0)
            {
                scrubbed += count;
                modified[i] = true;
            }
        }

        if (Zero(bytes.AsSpan(blockCount * BlockLength)))
        {
            scrubbed++;
        }

        for (var i = 0; i < blockCount; i++)
        {
            if (modified[i])
            {
                var block = Block(bytes, i);
                block[^1] = ChecksumFor(block[..^1]);
            }
        }

        return new Result(bytes, checksumValid, scrubbed);
    }

    /// <summary>Walks a DisplayID section inside an EDID extension block and zeroes product serial numbers.</summary>
    private static int ScrubDisplayId(Span<byte> block)
    {
        int sectionLength = block[2];
        if (sectionLength > DisplayIdMaxSectionLength)
        {
            // The section claims more bytes than the block holds: zero everything after the tag.
            return Zero(block[1..^1]) ? 1 : 0;
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
                count += Zero(block[position..end]) ? 1 : 0;
                break;
            }

            // Product identification: manufacturer (3), product code (2), serial number (4), ...
            var isProductId = tag is 0x00 or 0x20;
            if (isProductId && payloadLength >= 9 && Zero(block.Slice(payloadStart + 5, 4)))
            {
                count++;
            }

            position = payloadStart + payloadLength;
        }

        if (count > 0)
        {
            block[end] = ChecksumFor(block[1..end]);
        }

        return count;
    }

    private static Span<byte> Block(byte[] bytes, int index) => bytes.AsSpan(index * BlockLength, BlockLength);

    /// <summary>Zeroes the span; true if it held any non-zero byte.</summary>
    private static bool Zero(Span<byte> field)
    {
        if (!field.ContainsAnyExcept((byte)0))
        {
            return false;
        }

        field.Clear();
        return true;
    }

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
}
