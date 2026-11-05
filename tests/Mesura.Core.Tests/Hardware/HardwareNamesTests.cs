using Mesura.Core.Hardware;

namespace Mesura.Core.Tests.Hardware;

public sealed class HardwareNamesTests
{
    [Theory]
    [InlineData("11th Gen Intel(R) Core(TM) i7-1165G7 @ 2.80GHz", "Intel Core i7-1165G7")]
    [InlineData("AMD Ryzen 7 7700 8-Core Processor", "AMD Ryzen 7 7700 8-Core Processor")]
    [InlineData("AMD Ryzen 9 5900X 12-Core Processor            ", "AMD Ryzen 9 5900X 12-Core Processor")]
    [InlineData("Intel(R) Iris(R) Xe Graphics", "Intel Iris Xe Graphics")]
    [InlineData("NVIDIA GeForce RTX 3080", "NVIDIA GeForce RTX 3080")]
    [InlineData("Intel(R) Core(TM) i9-14900K CPU @ 3.20GHz", "Intel Core i9-14900K")]
    [InlineData("", "")]
    public void A_name_loses_the_marks_the_generation_prefix_and_the_clock_and_nothing_else(string name, string expected) =>
        Assert.Equal(expected, HardwareNames.Short(name));
}
