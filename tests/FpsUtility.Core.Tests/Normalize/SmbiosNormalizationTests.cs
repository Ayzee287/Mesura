using FpsUtility.Core.Facts;
using FpsUtility.Core.Normalize;
using FpsUtility.Core.Raw;
using static FpsUtility.Core.Tests.Normalize.RawBuilder;

namespace FpsUtility.Core.Tests.Normalize;

/// <summary>
/// What the firmware declares (DSP0134 types 1–4 and 17), exactly as declared: sizes and speeds decoded to canonical
/// units, codes to their names, placeholders and unknowns kept apart from values, and no rated speed (D004).
/// </summary>
public sealed class SmbiosNormalizationTests
{
    [Fact]
    public void Machine_facts_are_declared_values_with_the_chassis_lock_bit_ignored()
    {
        var machine = Normalize(Smbios(SystemInfo(), Board(), Chassis(0x83), EndOfTable())).Machine;

        Assert.Equal("Maker", machine.SystemManufacturer.Value);
        Assert.Equal("Model X", machine.SystemProduct.Value);
        Assert.Equal("Board Co", machine.BoardManufacturer.Value);
        Assert.Equal("B-1", machine.BoardProduct.Value);
        Assert.Equal(ChassisType.Desktop, machine.ChassisType.Value);
        Assert.Equal(Basis.Declared, machine.ChassisType.Basis);
        Assert.Equal(Rules.SmbiosChassis, machine.ChassisType.Rule);
        Assert.Equal("/sources/smbios/data/table", machine.ChassisType.From.Single());
    }

    [Fact]
    public void A_chassis_code_dsp0134_does_not_define_is_implausible_and_an_absent_chassis_is_not_present()
    {
        Assert.Equal(FactGap.Implausible, Normalize(Smbios(Chassis(0x40), EndOfTable())).Machine.ChassisType.Missing);
        Assert.Equal(FactGap.NotPresent, Normalize(Smbios(SystemInfo(), EndOfTable())).Machine.ChassisType.Missing);
    }

    [Fact]
    public void A_processor_is_read_with_extended_counts_and_an_unknown_count_stays_unknown()
    {
        var cpu = Normalize(Smbios(Processor(), EndOfTable())).Processors.Value!.Single();

        Assert.Equal("cpu-1", cpu.Id);
        Assert.Equal("CPU0", cpu.Socket.Value);
        Assert.True(cpu.Populated.Value);
        Assert.Equal("Test Processor 9000", cpu.Version.Value);
        Assert.Equal(5000, cpu.MaxSpeedMhz.Value);
        Assert.Equal(300, cpu.CoreCount.Value); // byte FFh: the word at 2Ah holds the count
        Assert.Equal(16, cpu.CoresEnabled.Value);
        Assert.Equal(FactGap.NotPresent, cpu.ThreadCount.Missing); // 0 is "unknown", never zero threads
    }

    [Fact]
    public void A_structure_too_short_for_a_field_leaves_it_not_supported()
    {
        var cpu = Normalize(Smbios(Structure(4, Fields(0x1A, (0x04, [1]), (0x14, Word(3000))), "CPU0"), EndOfTable())).Processors.Value!.Single();

        Assert.Equal(3000, cpu.MaxSpeedMhz.Value);
        Assert.Equal(FactGap.NotSupported, cpu.CoreCount.Missing);
    }

    [Fact]
    public void Memory_sizes_and_speeds_are_decoded_to_bytes_and_mts_including_extended_fields()
    {
        var slot = Normalize(Smbios(Ddr5Module(), EndOfTable())).MemoryDevices.Value!.Single();

        Assert.True(slot.Populated.Value);
        Assert.Equal(32UL * 1024 * 1024 * 1024, slot.SizeBytes.Value); // 7FFFh: the extended size, 32768 MiB
        Assert.Equal(MemoryType.Ddr5, slot.Type.Value);
        Assert.Equal(MemoryFormFactor.Sodimm, slot.FormFactor.Value);
        Assert.Equal(8000, slot.MaxSpeedMts.Value); // FFFFh: the extended speed
        Assert.Equal(5600, slot.ConfiguredSpeedMts.Value);
        Assert.Equal("KF556S40-32    ", slot.PartNumber.Value); // exactly as declared, padding included
        Assert.Equal("DIMM A", slot.Locator.Value);
    }

    [Fact]
    public void Kib_granularity_is_decoded_and_zero_speed_is_unknown_not_zero()
    {
        var slot = Normalize(Smbios(Structure(17, Fields(0x22, (0x0C, Word(0x8000 | 512)), (0x0E, [0x09]), (0x10, [1]), (0x12, [0x1A])), "DIMM K"), EndOfTable())).MemoryDevices.Value!.Single();

        Assert.Equal(512UL * 1024, slot.SizeBytes.Value);
        Assert.Equal(FactGap.NotPresent, slot.MaxSpeedMts.Missing);
        Assert.Equal(FactGap.NotPresent, slot.ConfiguredSpeedMts.Missing);
    }

    [Fact]
    public void An_empty_slot_is_a_known_empty_slot_whose_module_values_are_absent()
    {
        var slot = Normalize(Smbios(Structure(17, Fields(0x28, (0x0C, Word(0)), (0x0E, [0x02]), (0x10, [1]), (0x12, [0x02]), (0x15, Word(3200))), "DIMM B"), EndOfTable())).MemoryDevices.Value!.Single();

        Assert.False(slot.Populated.Value);
        Assert.Equal(FactGap.NotPresent, slot.SizeBytes.Missing);
        Assert.Equal(FactGap.NotPresent, slot.MaxSpeedMts.Missing); // a slot's declared speed isn't a module's
        Assert.Equal(MemoryFormFactor.Unknown, slot.FormFactor.Value); // the firmware's own "unknown", as declared
        Assert.Equal("DIMM B", slot.Locator.Value);
    }

    [Fact]
    public void Unknown_size_and_undefined_codes_stay_missing_with_their_reason()
    {
        var slot = Normalize(Smbios(Structure(17, Fields(0x22, (0x0C, Word(0xFFFF)), (0x0E, [0x7E]), (0x12, [0x16])), []), EndOfTable())).MemoryDevices.Value!.Single();

        Assert.True(slot.Populated.Value);
        Assert.Equal(FactGap.NotPresent, slot.SizeBytes.Missing);
        Assert.Equal(FactGap.Implausible, slot.Type.Missing); // 16h is reserved
        Assert.Equal(FactGap.Implausible, slot.FormFactor.Missing);
        Assert.Equal(FactGap.NotPresent, slot.Locator.Missing); // string number 0: no string
    }

    [Fact]
    public void A_string_the_privacy_scrub_overwrote_is_withheld_and_a_dangling_string_number_is_not_read()
    {
        var system = Structure(1, Fields(0x1B, (0x04, [1]), (0x05, [7])), "*****");

        var machine = Normalize(Smbios(system, EndOfTable())).Machine;

        Assert.Equal(FactGap.Withheld, machine.SystemManufacturer.Missing);
        Assert.Equal(FactGap.ParseFailed, machine.SystemProduct.Missing);
    }

    [Fact]
    public void A_table_that_cannot_be_walked_to_its_end_gives_no_lists_but_keeps_what_was_read()
    {
        // The scrubber zeroes what it can't walk, so the walk breaks at zeros: here, a structure claiming length 2.
        var truncated = Normalize(Smbios(Chassis(0x0A), [0x11, 0x02, 0x00, 0x00, 0x00, 0x00], Ddr5Module()));

        Assert.Equal(FactGap.ParseFailed, truncated.MemoryDevices.Missing); // a partial list would read as fewer modules
        Assert.Equal(FactGap.ParseFailed, truncated.Processors.Missing);
        Assert.Equal(ChassisType.Notebook, truncated.Machine.ChassisType.Value);
        Assert.Equal(FactGap.ParseFailed, truncated.Machine.SystemManufacturer.Missing); // not found before the break: unknown, not absent
    }

    [Fact]
    public void Bytes_zeroed_after_the_end_of_table_or_a_clean_end_without_one_leave_the_lists_known()
    {
        var zeroedAfterEnd = Normalize(Smbios(Chassis(0x0A), Ddr5Module(), EndOfTable(), [0, 0, 0, 0]) with { ZeroedFromOffset = 999 });
        var cleanEnd = Normalize(Smbios(Chassis(0x0A), Ddr5Module())); // no type 127, but the last structure ends the table

        Assert.Single(zeroedAfterEnd.MemoryDevices.Value!);
        Assert.Single(cleanEnd.MemoryDevices.Value!);
        Assert.Equal(FactGap.ParseFailed, Normalize(new SmbiosData(3, 8, 0, [], 0, null)).MemoryDevices.Missing); // an empty table declares nothing
    }

    [Theory]
    [InlineData(0x8000, 0u)] // KiB granularity, count 0
    [InlineData(0x7FFF, 16384u)] // an extended size below the 32767 MiB it is reserved for
    public void A_populated_slot_with_a_size_the_specification_rules_out_is_implausible(int code, uint extendedMebibytes)
    {
        var slot = Normalize(Smbios(Structure(17, Fields(0x22, (0x0C, Word(code)), (0x1C, Dword(extendedMebibytes))), []), EndOfTable())).MemoryDevices.Value!.Single();

        Assert.True(slot.Populated.Value);
        Assert.Equal(FactGap.Implausible, slot.SizeBytes.Missing); // never a known module of 0 bytes
    }

    [Fact]
    public void An_unavailable_smbios_source_leaves_every_firmware_fact_missing_for_its_reason()
    {
        var facts = Normalizer.Normalize(With(sources => sources with { Smbios = Unavailable<SmbiosData>(MissingReason.AccessDenied) }));

        Assert.Equal(FactGap.AccessDenied, facts.Machine.ChassisType.Missing);
        Assert.Equal(FactGap.AccessDenied, facts.MemoryDevices.Missing);
        Assert.Equal(FactGap.AccessDenied, facts.Processors.Missing);
    }

    [Fact]
    public void There_is_no_rated_or_profile_speed_among_the_memory_facts()
    {
        // D004: a rated speed comes only from a reliable part-number decode or the user, never from SMBIOS.
        Assert.DoesNotContain(typeof(MemoryDeviceFacts).GetProperties(), property =>
            property.Name.Contains("Rated", StringComparison.Ordinal) || property.Name.Contains("Xmp", StringComparison.Ordinal) || property.Name.Contains("Expo", StringComparison.Ordinal));
    }

    private static (MachineFacts Machine, Fact<IReadOnlyList<ProcessorFacts>> Processors, Fact<IReadOnlyList<MemoryDeviceFacts>> MemoryDevices) Normalize(SmbiosData smbios)
    {
        var facts = Normalizer.Normalize(With(sources => sources with { Smbios = Ok(smbios) }));
        return (facts.Machine, facts.Processors, facts.MemoryDevices);
    }

    private static byte[] SystemInfo() => Structure(1, Fields(0x1B, (0x04, [1]), (0x05, [2])), "Maker", "Model X");

    private static byte[] Board() => Structure(2, Fields(0x0F, (0x04, [1]), (0x05, [2])), "Board Co", "B-1");

    private static byte[] Chassis(byte type) => Structure(3, Fields(0x15, (0x04, [1]), (0x05, [type])), "Case Co");

    /// <summary>A SMBIOS 3.x processor: max 5000 MHz, populated, 300 cores through the extended field, 16 enabled, thread count unknown.</summary>
    private static byte[] Processor() => Structure(
        4,
        Fields(0x30, (0x04, [1]), (0x07, [2]), (0x10, [3]), (0x14, Word(5000)), (0x18, [0x41]), (0x23, [0xFF, 16, 0]), (0x2A, Word(300))),
        "CPU0", "Test Vendor", "Test Processor 9000");

    /// <summary>A SMBIOS 3.3+ DDR5 SO-DIMM: size and speed in their extended fields.</summary>
    private static byte[] Ddr5Module() => Structure(
        17,
        Fields(0x5C, (0x0C, Word(0x7FFF)), (0x0E, [0x0D]), (0x10, [1, 2, 0x22]), (0x15, Word(0xFFFF)), (0x17, [3, 0, 0, 4]), (0x1C, Dword(32768)), (0x20, Word(5600)), (0x54, Dword(8000))),
        "DIMM A", "BANK 0", "Kingston", "KF556S40-32    ");

    /// <summary>The formatted bytes after the 4-byte header, for a structure of <paramref name="length"/>, with values at their DSP0134 offsets.</summary>
    private static byte[] Fields(int length, params (int Offset, byte[] Bytes)[] values)
    {
        var fields = new byte[length - 4];
        foreach (var (offset, bytes) in values)
        {
            bytes.CopyTo(fields, offset - 4);
        }

        return fields;
    }
}
