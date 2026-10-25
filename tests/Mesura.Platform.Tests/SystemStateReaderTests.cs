using Mesura.Core.Windows;
using Mesura.Platform.Windows.Windows;
using Microsoft.Win32;

namespace Mesura.Platform.Tests;

/// <summary>
/// The configuration layer's reader on the machine running the tests. Read-only, and it asserts invariants only, never
/// this machine's hardware, so it passes on any supported PC.
/// </summary>
public sealed class SystemStateReaderTests
{
    // The Power mode overlays (best power efficiency, better battery, best performance) sit beside the plans under the
    // PowerSchemes key but are never plans; Windows doesn't list them.
    private static readonly string[] Overlays =
        ["961cc777-2547-4f9d-8174-7d86181b8a7a", "3af9b8d9-7c97-431d-ad78-34a8bfea439f", "ded574b5-45a0-4f42-8737-46345c09c238"];

    [Fact]
    public void Device_conditions_are_read_and_the_plans_are_the_ones_Windows_lists()
    {
        var reader = new SystemStateReader();

        Assert.NotNull(reader.HasBattery);    // the power capabilities always say whether a system battery is present
        Assert.NotNull(reader.ModernStandby); // and whether S0 low power idle is supported
        var power = reader.ReadPower();
        Assert.NotNull(power.ActiveScheme);
        Assert.Contains(power.ActiveScheme, power.Schemes); // enumerated as powercfg /l does: the active plan is always listed
        Assert.All(power.Schemes, s => Assert.True(Guid.TryParse(s, out _), s));
        Assert.All(Overlays, overlay => Assert.DoesNotContain(overlay, power.Schemes)); // the registry key's subkeys include them
    }

    [Fact]
    public void Global_tcp_settings_read_in_netshs_words_or_not_at_all()
    {
        var reader = new SystemStateReader();

        // Read-only (root\StandardCimv2); this PC's values aren't asserted, only that each is a word netsh takes back.
        foreach (var name in new[] { "autotuninglevel", "rss", "rsc" })
        {
            var value = reader.ReadTcpGlobal(name);
            Assert.True(value is null || TcpGlobalSetting.IsAllowed(name, value), $"{name} = {value}");
        }

        Assert.Null(reader.ReadTcpGlobal("congestionprovider")); // only the settings Mesura writes are read
        Assert.Equal(["disabled", "highlyrestricted", "restricted", "normal", "experimental"],
            Enumerable.Range(0, 5).Select(level => SystemStateReader.AutoTuningWord((uint)level)));
    }

    [Theory]
    [InlineData(RegistryValueKind.DWord, 8L)]                   // a REG_DWORD written with 8 bytes comes back as a long
    [InlineData(RegistryValueKind.DWord, new byte[] { 1, 2 })] // or, longer still, as bytes
    [InlineData(RegistryValueKind.String, 5)]                  // a type the kind doesn't promise
    public void A_value_whose_data_doesnt_match_its_kind_is_read_as_a_type_Mesura_doesnt_write(RegistryValueKind kind, object value)
    {
        var reading = SystemStateReader.ToReading(kind, value);

        Assert.True(reading.ValueExists);
        Assert.Null(reading.Kind); // apply leaves it alone instead of casting
    }

    [Fact]
    public void A_dword_reads_as_its_unsigned_value()
    {
        Assert.Equal(new RegistryReading(true, true, RegistryKind.DWord, "4294967295"), SystemStateReader.ToReading(RegistryValueKind.DWord, -1));
    }
}
