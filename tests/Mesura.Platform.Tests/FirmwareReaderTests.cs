using Mesura.Platform.Windows.Windows;

namespace Mesura.Platform.Tests;

/// <summary>The parts of the firmware readings that are pure parsing, and one real read that asserts only what must hold on any Windows PC.</summary>
public sealed class FirmwareReaderTests
{
    [Fact]
    public void A_microcode_value_is_read_from_the_upper_dword_of_eight_bytes_or_the_whole_of_four()
    {
        Assert.Equal(0x12Bu, FirmwareReader.ParseMicrocode([0, 0, 0, 0, 0x2B, 0x01, 0, 0]));
        Assert.Equal(0x8Au, FirmwareReader.ParseMicrocode([0x8A, 0, 0, 0]));
        Assert.Equal(0x8Au, FirmwareReader.ParseMicrocode([0x8A, 0, 0, 0, 0, 0, 0, 0])); // nothing in the upper dword: the lower one
        Assert.Null(FirmwareReader.ParseMicrocode([0, 0, 0, 0]));
        Assert.Null(FirmwareReader.ParseMicrocode([1, 2, 3])); // not a shape this reads: no guess
    }

    [Theory]
    [InlineData("\\\\HOST\\root\\cimv2:Win32_DeviceMemoryAddress.StartingAddress=\"274877906944\"", 274877906944UL)]
    [InlineData("Win32_DeviceMemoryAddress.StartingAddress=\"0\"", 0UL)]
    public void A_memory_range_is_identified_by_the_start_address_in_its_reference(string reference, ulong expected) => Assert.Equal(expected, FirmwareReader.StartOf(reference));

    [Theory]
    [InlineData("Win32_IRQResource.IRQNumber=\"5\"")]
    [InlineData("Win32_DeviceMemoryAddress.StartingAddress=\"abc\"")]
    [InlineData("Win32_DeviceMemoryAddress.StartingAddress=\"")]
    public void A_reference_without_a_readable_start_gives_none(string reference) => Assert.Null(FirmwareReader.StartOf(reference));

    [Fact]
    public void A_real_read_never_throws_and_gives_sane_values()
    {
        var extras = FirmwareReader.Read();
        Assert.All(extras.GpuWindows, w => Assert.True(w.LargestBarBytes > 0));
        Assert.True(extras.CpuVendor is null or "GenuineIntel" or "AuthenticAMD" || extras.CpuVendor.Length > 0);
    }
}
