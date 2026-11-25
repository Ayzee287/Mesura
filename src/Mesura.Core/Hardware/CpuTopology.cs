using System.Buffers.Binary;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Mesura.Core.Hardware;

/// <summary>One cache as Windows reports it: its level, its size and how many logical processors share it.</summary>
public sealed record CacheGroup(int Level, long SizeBytes, int LogicalProcessors);

/// <summary>
/// A hardware fact derived from the processor (0.8): its answer (<c>null</c> when it couldn't be told) and what it was told from, so a plan, the
/// command line and a test can say why.
/// </summary>
public sealed record CpuFact(bool? Value, string Basis);

/// <summary>
/// The processor's cache layout and what follows from it (0.8). The first fact is the <b>dual-CCD Ryzen X3D</b>: a Ryzen with two core dies of
/// which only one carries the stacked 3D V-Cache (7900X3D, 7950X3D, 9900X3D, 9950X3D). On these, AMD's chipset driver parks the other die while a
/// game runs, so settings that forbid core parking or remove its game detection work against it; single-die X3D parts have nothing to park.
/// <para>
/// It is told from the cache topology Windows reports (<c>GetLogicalProcessorInformationEx</c>, <c>RelationCache</c>), not from a model list:
/// two L3 caches of clearly different sizes are two dies of which one has the extra cache. That needs no update for a new model, and a part whose
/// dies both carry V-Cache (equal L3s) is correctly not one. Only when the topology can't be read, or contradicts the thread count (a virtual
/// machine), does the brand string decide, and then only for an AMD processor named X3D with more than eight cores.
/// </para>
/// </summary>
public static partial class CpuTopology
{
    /// <summary>One L3 at least this many times the size of another counts as a stacked-cache die (96 MB against 32 MB on the parts this is for).</summary>
    public const int AsymmetryFactor = 2;

    private const int RelationCache = 2;
    private const int CacheRecordHeader = 8;       // Relationship (4), Size (4)
    private const int GroupMaskOffset = CacheRecordHeader + 32; // Level, Associativity, LineSize, CacheSize, Type, Reserved[18], GroupCount
    private const int GroupAffinitySize = 16;       // KAFFINITY Mask (8 on x64), WORD Group, WORD Reserved[3]

    /// <summary>
    /// The caches of a <c>GetLogicalProcessorInformationEx(RelationCache)</c> buffer (x64 layout). Pure: the platform layer passes the bytes. A record
    /// that is cut off or names another relationship is skipped; <c>null</c> when nothing whole could be read.
    /// </summary>
    public static IReadOnlyList<CacheGroup>? ParseCaches(ReadOnlySpan<byte> buffer)
    {
        var caches = new List<CacheGroup>();
        for (var offset = 0; offset + CacheRecordHeader <= buffer.Length;)
        {
            var relationship = BinaryPrimitives.ReadInt32LittleEndian(buffer[offset..]);
            var size = BinaryPrimitives.ReadInt32LittleEndian(buffer[(offset + 4)..]);
            if (size <= 0 || offset + size > buffer.Length)
            {
                break;
            }

            if (relationship == RelationCache && size >= GroupMaskOffset + GroupAffinitySize)
            {
                var record = buffer.Slice(offset, size);
                var level = record[CacheRecordHeader];
                var bytes = BinaryPrimitives.ReadUInt32LittleEndian(record[(CacheRecordHeader + 4)..]);
                // GroupCount (Windows 11 / Server 2022 and later); older Windows has a reserved zero there and exactly one group mask.
                var groups = Math.Max(1, (int)BinaryPrimitives.ReadUInt16LittleEndian(record[(GroupMaskOffset - 2)..]));
                var processors = 0;
                for (var g = 0; g < groups && GroupMaskOffset + ((g + 1) * GroupAffinitySize) <= size; g++)
                {
                    processors += BitOperations.PopCount(BinaryPrimitives.ReadUInt64LittleEndian(record[(GroupMaskOffset + (g * GroupAffinitySize))..]));
                }

                caches.Add(new CacheGroup(level, bytes, processors));
            }

            offset += size;
        }

        return caches.Count > 0 ? caches : null;
    }

    /// <summary>The L3 caches among <paramref name="caches"/>, or <c>null</c> when there are none.</summary>
    public static IReadOnlyList<CacheGroup>? L3(IReadOnlyList<CacheGroup>? caches) =>
        caches?.Where(c => c.Level == 3).ToList() is { Count: > 0 } l3 ? l3 : null;

    /// <summary>Whether the processor is a dual-CCD Ryzen X3D, and from what (see the class remarks).</summary>
    public static CpuFact DualCcdX3d(CpuMaker maker, string? name, int? cores, int? threads, IReadOnlyList<CacheGroup>? l3)
    {
        var named = name is not null && X3dName().IsMatch(name);
        if (maker is CpuMaker.Intel or CpuMaker.Other)
        {
            return new CpuFact(false, "not an AMD processor");
        }

        if (Usable(l3, threads))
        {
            if (l3!.Count == 1)
            {
                return new CpuFact(false, "one L3 cache (a single core die)");
            }

            var largest = l3.Max(c => c.SizeBytes);
            var smallest = l3.Min(c => c.SizeBytes);
            if (largest < AsymmetryFactor * smallest)
            {
                return new CpuFact(false, $"{l3.Count} L3 caches of the same size");
            }

            // A cache this uneven is the stacked-cache layout; on a processor whose maker wasn't read the name has to agree.
            return maker == CpuMaker.Amd || named
                ? new CpuFact(true, $"{l3.Count} L3 caches of different sizes ({Mb(largest)} and {Mb(smallest)})")
                : new CpuFact(null, "L3 caches of different sizes, but the processor's maker wasn't read");
        }

        if (maker == CpuMaker.Unknown && !named)
        {
            return new CpuFact(null, "the processor couldn't be read");
        }

        if (!named)
        {
            return new CpuFact(false, "not an X3D model (by name; the cache layout couldn't be read)");
        }

        var count = cores ?? (threads is { } t ? t / 2 : null);
        return count is { } c
            ? new CpuFact(c > 8, c > 8
                ? $"an X3D model with {c} cores (by name; the cache layout couldn't be read)"
                : $"an X3D model with {c} cores, one core die (by name; the cache layout couldn't be read)")
            : new CpuFact(null, "an X3D model whose core count couldn't be read");
    }

    /// <summary>A topology that can be believed: every L3 has a size and processors, and together they cover the processor's threads when those are known.</summary>
    private static bool Usable(IReadOnlyList<CacheGroup>? l3, int? threads) =>
        l3 is { Count: > 0 }
        && l3.All(c => c.SizeBytes > 0 && c.LogicalProcessors > 0)
        && (threads is null || l3.Sum(c => c.LogicalProcessors) == threads);

    private static string Mb(long bytes) => $"{bytes / (1024 * 1024)} MB";

    [GeneratedRegex(@"X3D(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex X3dName();
}
