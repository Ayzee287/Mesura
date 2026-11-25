using System.Buffers.Binary;
using Mesura.Core.Hardware;
using Mesura.Core.Normalize;
using Mesura.Core.Profiles;

namespace Mesura.Core.Tests.Hardware;

/// <summary>
/// The dual-CCD Ryzen X3D fact (0.8): told from the L3 layout Windows reports (built here as the bytes of
/// <c>GetLogicalProcessorInformationEx(RelationCache)</c>), from the name only when that can't be read, and never from a guess.
/// </summary>
public sealed class CpuTopologyTests
{
    private const long Mb = 1024 * 1024;

    /// <summary>One <c>SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX</c> cache record (x64): header, CACHE_RELATIONSHIP, then the group masks.</summary>
    private static byte[] Cache(int level, long size, params ulong[] masks)
    {
        var groups = Math.Max(1, masks.Length);
        var record = new byte[40 + (16 * groups)];
        BinaryPrimitives.WriteInt32LittleEndian(record, 2);                       // RelationCache
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4), record.Length);
        record[8] = (byte)level;
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(12), (uint)size);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(38), (ushort)masks.Length);
        for (var g = 0; g < masks.Length; g++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(40 + (16 * g)), masks[g]);
        }

        return record;
    }

    private static byte[] Buffer(params byte[][] records) => [.. records.SelectMany(r => r)];

    private static CpuFact Fact(CpuMaker maker, string? name, int? cores, int? threads, params byte[][] records) =>
        CpuTopology.DualCcdX3d(maker, name, cores, threads, CpuTopology.L3(CpuTopology.ParseCaches(Buffer(records))));

    [Fact]
    public void Two_dies_with_one_stacked_cache_are_a_dual_ccd_x3d_from_the_layout_whatever_the_name()
    {
        // 7950X3D: L2 per core, then 96 MB on the first die (16 threads) and 32 MB on the second.
        var fact = Fact(CpuMaker.Amd, "AMD Ryzen 9 Processor", 16, 32,
            Cache(2, 1 * Mb, 0b11), Cache(3, 96 * Mb, 0x0000_FFFF), Cache(3, 32 * Mb, 0xFFFF_0000));

        Assert.True(fact.Value);
        Assert.Contains("different sizes", fact.Basis, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AMD Ryzen 7 7800X3D 8-Core Processor", 8, 16, new long[] { 96 }, false)]          // one die with the stacked cache
    [InlineData("AMD Ryzen 9 7950X 16-Core Processor", 16, 32, new long[] { 32, 32 }, false)]      // two ordinary dies
    [InlineData("AMD Ryzen 9 5900X 12-Core Processor", 12, 24, new long[] { 32, 32 }, false)]
    [InlineData("AMD Ryzen 9 3900X 12-Core Processor", 12, 24, new long[] { 16, 16, 16, 16 }, false)] // Zen 2: an L3 per CCX
    [InlineData("AMD Ryzen 9 9950X3D2", 16, 32, new long[] { 96, 96 }, false)]                      // both dies stacked: nothing to park
    [InlineData("AMD Ryzen 9 9900X3D 12-Core Processor", 12, 24, new long[] { 96, 32 }, true)]
    public void The_layout_decides(string name, int cores, int threads, long[] l3Mb, bool expected)
    {
        var perCache = threads / l3Mb.Length;
        var records = l3Mb.Select((size, i) => Cache(3, size * Mb, ((1UL << perCache) - 1) << (i * perCache))).ToArray();

        Assert.Equal(expected, Fact(CpuMaker.Amd, name, cores, threads, records).Value);
    }

    [Fact]
    public void An_intel_processor_is_never_one_even_with_an_uneven_cache()
    {
        var fact = Fact(CpuMaker.Intel, "Intel Core i9-14900K", 24, 32, Cache(3, 96 * Mb, 0xFFFF), Cache(3, 32 * Mb, 0xFFFF_0000));

        Assert.False(fact.Value);
    }

    [Theory]
    [InlineData(CpuMaker.Amd, "AMD Ryzen 9 7950X3D 16-Core Processor", 16, true)]
    [InlineData(CpuMaker.Amd, "AMD Ryzen 7 9800X3D 8-Core Processor", 8, false)]
    [InlineData(CpuMaker.Amd, "AMD Ryzen 7 7700 8-Core Processor", 8, false)]
    [InlineData(CpuMaker.Unknown, "AMD Ryzen 9 9950X3D 16-Core Processor", 16, true)]
    [InlineData(CpuMaker.Unknown, null, 16, null)]
    [InlineData(CpuMaker.Amd, "AMD Ryzen 9 7950X3D", null, null)]
    public void Without_a_readable_layout_the_name_decides_only_for_an_x3d_model_and_never_guesses(CpuMaker maker, string? name, int? cores, bool? expected)
    {
        var fact = CpuTopology.DualCcdX3d(maker, name, cores, null, null);

        Assert.Equal(expected, fact.Value);
    }

    [Fact]
    public void A_layout_that_contradicts_the_thread_count_is_not_believed_and_the_name_decides()
    {
        // A virtual machine that shows one 32 MB L3 over 4 threads of a 32-thread host processor named X3D.
        var fact = Fact(CpuMaker.Amd, "AMD Ryzen 9 7950X3D 16-Core Processor", 16, 32, Cache(3, 32 * Mb, 0xF));

        Assert.True(fact.Value);
        Assert.Contains("by name", fact.Basis, StringComparison.Ordinal);
    }

    [Fact]
    public void An_uneven_cache_on_a_processor_whose_maker_was_not_read_and_not_named_x3d_cannot_be_told()
    {
        var fact = Fact(CpuMaker.Unknown, "Some processor", 16, 32, Cache(3, 96 * Mb, 0xFFFF), Cache(3, 32 * Mb, 0xFFFF_0000));

        Assert.Null(fact.Value);
    }

    [Fact]
    public void Parsing_skips_other_relationships_stops_at_a_cut_off_record_and_counts_every_group()
    {
        var other = new byte[48];
        BinaryPrimitives.WriteInt32LittleEndian(other, 0); // RelationProcessorCore
        BinaryPrimitives.WriteInt32LittleEndian(other.AsSpan(4), other.Length);
        var twoGroups = Cache(3, 32 * Mb, 0xFF, 0xF);       // 8 + 4 logical processors in two processor groups
        var olderWindows = Cache(3, 16 * Mb, 0xF0);         // GroupCount 0 on older Windows: one mask
        BinaryPrimitives.WriteUInt16LittleEndian(olderWindows.AsSpan(38), 0);
        var cut = Cache(3, 8 * Mb, 0xF)[..30];

        var caches = CpuTopology.ParseCaches(Buffer(other, twoGroups, olderWindows, cut))!;

        Assert.Equal([new CacheGroup(3, 32 * Mb, 12), new CacheGroup(3, 16 * Mb, 4)], caches);
        Assert.Null(CpuTopology.ParseCaches([]));
        Assert.Null(CpuTopology.L3([new CacheGroup(2, Mb, 2)]));
    }

    [Fact]
    public void The_builder_keeps_only_the_l3_caches_and_the_profile_derives_the_fact()
    {
        var facts = Normalizer.Normalize(SnapshotFixture.Create());
        var caches = new[] { new CacheGroup(2, Mb, 2), new CacheGroup(3, 96 * Mb, 16), new CacheGroup(3, 32 * Mb, 16) };

        var profile = HardwareProfileBuilder.Build(facts, new HardwareExtras { WindowsBuild = 26200, CpuVendorId = "AuthenticAMD", CpuName = "AMD Ryzen 9 7950X3D 16-Core Processor", Caches = caches });

        Assert.Equal(2, profile.Cpu!.L3Caches!.Count);
        Assert.All(profile.Cpu.L3Caches, c => Assert.Equal(3, c.Level));
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(null, null, null)]
    public void Profile_conditions_can_ask_for_a_dual_ccd_x3d_or_for_anything_else(bool? x3d, bool? holdsX3d, bool? holdsOther)
    {
        CpuProfile cpu = x3d switch
        {
            true => new("AMD Ryzen 9 7950X3D", CpuMaker.Amd, 16, 32, false, [new CacheGroup(3, 96 * Mb, 16), new CacheGroup(3, 32 * Mb, 16)]),
            false => new("AMD Ryzen 7 7700", CpuMaker.Amd, 8, 16, false, [new CacheGroup(3, 32 * Mb, 16)]),
            _ => new(null, CpuMaker.Unknown, null, null, null),
        };
        var hardware = new HardwareProfile(new WindowsPlatform(26200, "Professional", "25H2", true), DeviceForm.Desktop, new PowerPlatform(false, false, true), cpu, null, [], [], []);

        Assert.Equal(holdsX3d, new HardwareCondition(Cpu: "x3d-dual-ccd").Holds(hardware, out _));
        Assert.Equal(holdsOther, new HardwareCondition(Cpu: "not-x3d-dual-ccd").Holds(hardware, out var failed));
        if (holdsOther == false)
        {
            Assert.Equal("cpu.not-x3d-dual-ccd", failed);
        }

        Assert.Contains("x3d-dual-ccd", HardwareCondition.CpuValues);
        Assert.Contains("not-x3d-dual-ccd", HardwareCondition.CpuValues);
    }
}
