using System.Text.Json.Serialization;
using Mesura.Core.Raw.Serialization;

namespace Mesura.Core.Facts;

/// <summary>
/// A processor socket as the firmware declares it (SMBIOS type 4). Declared values, not measurements: a live
/// clock, a boost clock or a core's state need other sources.
/// </summary>
/// <param name="Id">Snapshot-scoped, in the order of the SMBIOS table.</param>
/// <param name="Socket">The socket designation, e.g. <c>CPU0</c>.</param>
/// <param name="Populated">Whether the firmware declares a processor in the socket.</param>
/// <param name="Version">The processor's model string, e.g. <c>11th Gen Intel(R) Core(TM) i7-1165G7 @ 2.80GHz</c>.</param>
/// <param name="MaxSpeedMhz">The maximum speed the firmware declares the system supports for this socket, in MHz; not the processor's own rating.</param>
public sealed record ProcessorFacts(
    string Id,
    Fact<string> Socket,
    Fact<bool> Populated,
    Fact<string> Manufacturer,
    Fact<string> Version,
    Fact<int> MaxSpeedMhz,
    Fact<int> CoreCount,
    Fact<int> CoresEnabled,
    Fact<int> ThreadCount);

/// <summary>
/// A memory slot as the firmware declares it (SMBIOS type 17). An empty slot is a known fact
/// (<see cref="Populated"/> false); its module values are then missing, because there is no module.
/// </summary>
/// <param name="Id">Snapshot-scoped, in the order of the SMBIOS table.</param>
/// <param name="Locator">The slot as the firmware names it, e.g. <c>Controller0-ChannelA</c>. Platform-specific text; checks interpret it (§6.5 check 5).</param>
/// <param name="SizeBytes">The module's size in bytes.</param>
/// <param name="MaxSpeedMts">
/// The module's maximum capable speed as the firmware declares it (DSP0134 "Speed"), in MT/s. Usually the JEDEC
/// speed. It is never the XMP or EXPO rating (D004): that is not in SMBIOS.
/// </param>
/// <param name="ConfiguredSpeedMts">The speed the firmware declares the memory is configured to run at, in MT/s.</param>
/// <param name="PartNumber">The module's part number exactly as declared, padding included.</param>
public sealed record MemoryDeviceFacts(
    string Id,
    Fact<string> Locator,
    Fact<string> BankLocator,
    Fact<bool> Populated,
    Fact<ulong> SizeBytes,
    Fact<MemoryFormFactor> FormFactor,
    Fact<MemoryType> Type,
    Fact<int> MaxSpeedMts,
    Fact<int> ConfiguredSpeedMts,
    Fact<string> Manufacturer,
    Fact<string> PartNumber);

/// <summary>A memory device's form factor as SMBIOS declares it (DSP0134 §7.18.1). The value is the specification's code.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<MemoryFormFactor>))]
public enum MemoryFormFactor
{
    Other = 0x01,
    Unknown = 0x02,
    Simm = 0x03,
    Sip = 0x04,
    Chip = 0x05,
    Dip = 0x06,
    Zip = 0x07,
    ProprietaryCard = 0x08,
    Dimm = 0x09,
    Tsop = 0x0A,
    RowOfChips = 0x0B,
    Rimm = 0x0C,
    Sodimm = 0x0D,
    Srimm = 0x0E,
    FbDimm = 0x0F,
    Die = 0x10,
    Camm = 0x11,
    CuDimm = 0x12,
    CsoDimm = 0x13,
}

/// <summary>A memory device's type as SMBIOS declares it (DSP0134 §7.18.2). The value is the specification's code; 15h–17h are reserved.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<MemoryType>))]
public enum MemoryType
{
    Other = 0x01,
    Unknown = 0x02,
    Dram = 0x03,
    Edram = 0x04,
    Vram = 0x05,
    Sram = 0x06,
    Ram = 0x07,
    Rom = 0x08,
    Flash = 0x09,
    Eeprom = 0x0A,
    Feprom = 0x0B,
    Eprom = 0x0C,
    Cdram = 0x0D,
    ThreeDram = 0x0E,
    Sdram = 0x0F,
    Sgram = 0x10,
    Rdram = 0x11,
    Ddr = 0x12,
    Ddr2 = 0x13,
    Ddr2FbDimm = 0x14,
    Ddr3 = 0x18,
    Fbd2 = 0x19,
    Ddr4 = 0x1A,
    Lpddr = 0x1B,
    Lpddr2 = 0x1C,
    Lpddr3 = 0x1D,
    Lpddr4 = 0x1E,
    LogicalNonVolatileDevice = 0x1F,
    Hbm = 0x20,
    Hbm2 = 0x21,
    Ddr5 = 0x22,
    Lpddr5 = 0x23,
    Hbm3 = 0x24,
    Mrdimm = 0x25,
}
