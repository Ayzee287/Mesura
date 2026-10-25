using System.Globalization;
using Mesura.Core.Facts;
using Mesura.Core.Privacy;
using Mesura.Core.Raw;

namespace Mesura.Core.Normalize;

/// <summary>
/// What the firmware declares about the machine, its processors and its memory (SMBIOS types 1–4 and 17, DSP0134).
/// Every value is <see cref="Basis.Declared"/> and exactly as declared: no rated RAM speed, no XMP or EXPO profile,
/// no guess at what a placeholder means (D004). The field offsets are in the <see cref="Rules"/> each fact names.
/// </summary>
internal static class SmbiosNormalizer
{
    private const string Table = "/sources/smbios/data/table";

    public static (MachineFacts Machine, Fact<IReadOnlyList<ProcessorFacts>> Processors, Fact<IReadOnlyList<MemoryDeviceFacts>> Memory) Normalize(SourceEntry<SmbiosData> source)
    {
        if (!Evidence.TryData(source, "smbios", out var smbios, out var unavailable))
        {
            var missing = unavailable.Fact<string>();
            return (
                new MachineFacts(missing, missing, missing, missing, unavailable.Fact<ChassisType>()),
                unavailable.Fact<IReadOnlyList<ProcessorFacts>>(),
                unavailable.Fact<IReadOnlyList<MemoryDeviceFacts>>());
        }

        var walk = SmbiosTable.Read(smbios.Table);
        var structures = walk.Structures;

        // Bytes the scrubber zeroed after the end-of-table structure don't make the table incomplete; bytes it zeroed
        // because it couldn't walk them do, and the walk stops at them too.
        var complete = walk.Complete;
        var system = structures.FirstOrDefault(structure => structure.Type == 1);
        var board = structures.FirstOrDefault(structure => structure.Type == 2);
        var chassis = structures.FirstOrDefault(structure => structure.Type == 3);

        // The first structure of each type, as DSP0134 has one system and a main board and chassis first.
        var machine = new MachineFacts(
            SystemManufacturer: system is null ? Absent<string>(1, complete) : Text(system, 1, 0x04, Rules.SmbiosSystem),
            SystemProduct: system is null ? Absent<string>(1, complete) : Text(system, 1, 0x05, Rules.SmbiosSystem),
            BoardManufacturer: board is null ? Absent<string>(2, complete) : Text(board, 2, 0x04, Rules.SmbiosBoard),
            BoardProduct: board is null ? Absent<string>(2, complete) : Text(board, 2, 0x05, Rules.SmbiosBoard),
            ChassisType: chassis is null ? Absent<ChassisType>(3, complete) : Chassis(chassis));

        // A list is known only when the whole table was walked: a partial list would read as fewer modules.
        if (!complete)
        {
            const string incomplete = "The table couldn't be walked to its end, so the list may be incomplete";
            return (
                machine,
                Fact.Missing<IReadOnlyList<ProcessorFacts>>(FactGap.ParseFailed, incomplete, Table),
                Fact.Missing<IReadOnlyList<MemoryDeviceFacts>>(FactGap.ParseFailed, incomplete, Table));
        }

        var processors = structures.Where(structure => structure.Type == 4).Select(Processor);
        var memory = structures.Where(structure => structure.Type == 17).Select(MemoryDevice);

        return (
            machine,
            Fact.Derived<IReadOnlyList<ProcessorFacts>>([.. processors], Basis.Declared, Rules.SmbiosProcessor, Table),
            Fact.Derived<IReadOnlyList<MemoryDeviceFacts>>([.. memory], Basis.Declared, Rules.SmbiosMemoryDevice, Table));
    }

    private static ProcessorFacts Processor(SmbiosStructure cpu, int index)
    {
        const string rule = Rules.SmbiosProcessor;
        return new ProcessorFacts(
            Id: string.Create(CultureInfo.InvariantCulture, $"cpu-{index + 1}"),
            Socket: Text(cpu, 4, 0x04, rule),
            Populated: cpu.Has(0x18, 1)
                ? Declared((cpu.Byte(0x18) & 0x40) != 0, rule)
                : TooShort<bool>(4, 0x18),
            Manufacturer: Text(cpu, 4, 0x07, rule),
            Version: Text(cpu, 4, 0x10, rule),
            MaxSpeedMhz: cpu.Has(0x14, 2) ? Positive(cpu.Word(0x14), "maximum speed", rule) : TooShort<int>(4, 0x14),
            CoreCount: Count(cpu, 0x23, 0x2A, "core count"),
            CoresEnabled: Count(cpu, 0x24, 0x2C, "enabled core count"),
            ThreadCount: Count(cpu, 0x25, 0x2E, "thread count"));
    }

    /// <summary>A type 4 count: the byte, or the word at <paramref name="wordOffset"/> when the byte is FFh; 0 is unknown.</summary>
    private static Fact<int> Count(SmbiosStructure cpu, int byteOffset, int wordOffset, string what)
    {
        if (!cpu.Has(byteOffset, 1))
        {
            return TooShort<int>(4, byteOffset);
        }

        int value = cpu.Byte(byteOffset);
        if (value == 0xFF && cpu.Has(wordOffset, 2))
        {
            value = cpu.Word(wordOffset);
        }

        return Positive(value, what, Rules.SmbiosProcessor);
    }

    private static MemoryDeviceFacts MemoryDevice(SmbiosStructure slot, int index)
    {
        const string rule = Rules.SmbiosMemoryDevice;
        var sizeCode = slot.Has(0x0C, 2) ? slot.Word(0x0C) : (int?)null;
        var empty = sizeCode == 0;

        Fact<T> Module<T>(Func<Fact<T>> read) =>
            empty ? Fact.Missing<T>(FactGap.NotPresent, "No module is installed in this slot", Table) : read();

        return new MemoryDeviceFacts(
            Id: string.Create(CultureInfo.InvariantCulture, $"memory-{index + 1}"),
            Locator: Text(slot, 17, 0x10, rule),
            BankLocator: Text(slot, 17, 0x11, rule),
            Populated: sizeCode is { } code ? Declared(code != 0, rule) : TooShort<bool>(17, 0x0C),
            SizeBytes: Module(() => Size(slot, sizeCode)),
            FormFactor: Code<MemoryFormFactor>(slot, 0x0E, "form factor"),
            Type: Code<MemoryType>(slot, 0x12, "memory type"),
            MaxSpeedMts: Module(() => Speed(slot, 0x15, 0x54, "speed")),
            ConfiguredSpeedMts: Module(() => Speed(slot, 0x20, 0x58, "configured speed")),
            Manufacturer: Module(() => Text(slot, 17, 0x17, rule)),
            PartNumber: Module(() => Text(slot, 17, 0x1A, rule)));
    }

    /// <summary>Type 17 size: FFFFh unknown; 7FFFh means the extended size (MiB) at 1Ch; bit 15 set means KiB, clear MiB.</summary>
    private static Fact<ulong> Size(SmbiosStructure slot, int? code)
    {
        switch (code)
        {
            case null:
                return TooShort<ulong>(17, 0x0C);
            case 0xFFFF:
                return Fact.Missing<ulong>(FactGap.NotPresent, "The firmware declares the module's size unknown", Table);
            case 0x7FFF when !slot.Has(0x1C, 4):
                return Fact.Missing<ulong>(FactGap.Implausible, "The size points to an extended size the structure doesn't hold", Table);
            case 0x7FFF:
                // DSP0134 uses the extended size only for 32 GB - 1 MB (32767 MiB) or more.
                var mebibytes = slot.Dword(0x1C) & 0x7FFF_FFFF;
                return mebibytes >= 0x7FFF
                    ? Declared((ulong)mebibytes * 1024 * 1024, Rules.SmbiosMemoryDevice)
                    : Fact.Missing<ulong>(FactGap.Implausible, string.Create(CultureInfo.InvariantCulture, $"The extended size ({mebibytes} MiB) is below what the extended field is for"), Table);
            case { } value when (value & 0x7FFF) == 0:
                return Fact.Missing<ulong>(FactGap.Implausible, "A populated slot declares a size of 0", Table);
            case { } value:
                var units = (ulong)(value & 0x7FFF);
                return Declared((value & 0x8000) != 0 ? units * 1024 : units * 1024 * 1024, Rules.SmbiosMemoryDevice);
        }
    }

    /// <summary>A type 17 speed in MT/s: the word, or the extended dword when the word is FFFFh; 0 is unknown.</summary>
    private static Fact<int> Speed(SmbiosStructure slot, int wordOffset, int extendedOffset, string what)
    {
        if (!slot.Has(wordOffset, 2))
        {
            return TooShort<int>(17, wordOffset);
        }

        var value = slot.Word(wordOffset);
        if (value == 0xFFFF)
        {
            if (!slot.Has(extendedOffset, 4))
            {
                return Fact.Missing<int>(FactGap.Implausible, $"The {what} points to an extended value the structure doesn't hold", Table);
            }

            value = (int)(slot.Dword(extendedOffset) & 0x7FFF_FFFF);
        }

        return Positive(value, what, Rules.SmbiosMemoryDevice);
    }

    /// <summary>A structure type the table doesn't hold: declared absent when the whole table was walked, else unknown.</summary>
    private static Fact<T> Absent<T>(int type, bool complete) =>
        complete
            ? Fact.Missing<T>(FactGap.NotPresent, string.Create(CultureInfo.InvariantCulture, $"The table has no type {type} structure"), Table)
            : Fact.Missing<T>(FactGap.ParseFailed, string.Create(CultureInfo.InvariantCulture, $"No type {type} structure was found before the table stopped being walkable"), Table);

    private static Fact<ChassisType> Chassis(SmbiosStructure chassis)
    {
        if (!chassis.Has(0x05, 1))
        {
            return TooShort<ChassisType>(3, 0x05);
        }

        var code = chassis.Byte(0x05) & 0x7F; // bit 7 is the chassis lock
        return Enum.IsDefined((ChassisType)code)
            ? Declared((ChassisType)code, Rules.SmbiosChassis)
            : Fact.Missing<ChassisType>(FactGap.Implausible, string.Create(CultureInfo.InvariantCulture, $"Chassis type {code:X2}h is not defined in DSP0134"), Table);
    }

    /// <summary>A byte field holding one of an enumeration's DSP0134 codes.</summary>
    private static Fact<TEnum> Code<TEnum>(SmbiosStructure slot, int offset, string what)
        where TEnum : struct, Enum
    {
        if (!slot.Has(offset, 1))
        {
            return TooShort<TEnum>(17, offset);
        }

        int code = slot.Byte(offset);
        var value = (TEnum)Enum.ToObject(typeof(TEnum), code);
        return Enum.IsDefined(value)
            ? Declared(value, Rules.SmbiosMemoryDevice)
            : Fact.Missing<TEnum>(FactGap.Implausible, string.Create(CultureInfo.InvariantCulture, $"The {what} code {code:X2}h is not defined in DSP0134"), Table);
    }

    /// <summary>
    /// A string field: its string, exactly as declared. Number 0 is "no string"; a string the privacy scrub
    /// overwrote (only <c>*</c>) is withheld, never shown as a value.
    /// </summary>
    private static Fact<string> Text(SmbiosStructure structure, int type, int offset, string rule)
    {
        if (!structure.Has(offset, 1))
        {
            return TooShort<string>(type, offset);
        }

        int number = structure.Byte(offset);
        return structure.String(number) switch
        {
            _ when number == 0 => Fact.Missing<string>(FactGap.NotPresent, "The firmware declares no string for this field", Table),
            null => Fact.Missing<string>(FactGap.ParseFailed, string.Create(CultureInfo.InvariantCulture, $"The field names string {number}, which the structure doesn't have"), Table),
            var text when text.All(c => c == (char)SmbiosScrubber.RedactionCharacter) => Fact.Missing<string>(FactGap.Withheld, "The privacy scrub overwrote this string at collection", Table),
            var text => Declared(text, rule),
        };
    }

    private static Fact<int> Positive(int value, string what, string rule) =>
        value > 0 ? Declared(value, rule) : Fact.Missing<int>(FactGap.NotPresent, $"The firmware declares the {what} unknown (0)", Table);

    private static Fact<T> Declared<T>(T value, string rule) => Fact.Derived(value, Basis.Declared, rule, Table);

    private static Fact<T> TooShort<T>(int type, int offset) =>
        Fact.Missing<T>(FactGap.NotSupported, string.Create(CultureInfo.InvariantCulture, $"The type {type} structure is too short to hold the field at {offset:X2}h (an older SMBIOS version)"), Table);
}
