using Mesura.Platform.Windows.Windows;
using Mesura.Setup.Windows.Windows;

namespace Mesura.Setup.Tests;

/// <summary>
/// The independent readings of the 0.5 effect checks on the machine running the tests. Read-only, and they assert invariants only (a
/// word the engine understands, or no answer), never this PC's hardware.
/// </summary>
public sealed class RealReadersTests
{
    [Fact]
    public void Gpu_scheduling_is_read_from_the_graphics_kernel_as_a_known_word_or_not_at_all()
    {
        var reader = new SystemStateReader(new SystemTools());

        var word = reader.ReadEffect("gpuScheduling");

        Assert.True(word is null or "enabled" or "disabled" or "unsupported", word);
    }

    [Fact]
    public void The_sessions_accessibility_hotkeys_and_pointer_acceleration_are_read_from_windows_as_known_words()
    {
        var reader = new SystemStateReader(new SystemTools());

        Assert.True(reader.ReadEffect("accessibilityHotkeys") is "enabled" or "disabled", reader.ReadEffect("accessibilityHotkeys"));
        Assert.True(reader.ReadEffect("pointerAcceleration") is "enabled" or "disabled", reader.ReadEffect("pointerAcceleration"));
    }

    [Fact]
    public void Ntfs_last_access_is_read_through_fsutil_as_enabled_or_disabled()
    {
        var tools = new SystemTools();

        var word = tools.ReadEffect("ntfsLastAccess");

        Assert.True(word is "enabled" or "disabled", word); // fsutil answers without administrator rights
        Assert.Null(tools.ReadEffect("somethingElse"));
    }

    [Fact]
    public void Every_hardware_graphics_adapter_is_listed_with_its_maker_not_only_the_nvidia_ones()
    {
        var adapters = SystemSummaryReader.ReadAdapters();

        Assert.NotNull(adapters);
        Assert.NotEmpty(adapters); // a PC with a screen has at least one
        Assert.All(adapters, a =>
        {
            Assert.False(string.IsNullOrWhiteSpace(a.Name));
            Assert.True(a.VendorId > 0, a.Name);
        });
        Assert.Equal(adapters.Count(a => a.VendorId == Mesura.Core.Windows.SystemSummary.NvidiaVendorId), SystemSummaryReader.ReadNvidia()!.Count);
    }

    [Fact]
    public void The_network_adapters_are_read_with_a_stable_id_and_only_physical_ones_carry_settings()
    {
        var adapters = new SystemStateReader().ReadAdapters();

        Assert.NotNull(adapters); // the class exists on every supported Windows, even on a PC with no card at all
        Assert.All(adapters, a =>
        {
            Assert.Matches(@"^\{[0-9A-F]{8}-([0-9A-F]{4}-){3}[0-9A-F]{12}\}$", a.InterfaceGuid);
            Assert.False(string.IsNullOrWhiteSpace(a.Name));
            Assert.Matches(@"^(\d{4}-\d{2}-\d{2})?$", a.DriverDate);
            if (!a.Physical)
            {
                Assert.Empty(a.Properties); // a virtual adapter, a tunnel or a VPN is never read for tuning
                Assert.Empty(a.Bindings);
                Assert.Null(a.AllowTurnOff);
            }
        });
        Assert.All(adapters.Where(a => a.Kind is Mesura.Core.Windows.AdapterKind.Virtual or Mesura.Core.Windows.AdapterKind.Vpn), a => Assert.False(a.Physical));
    }

    [Fact]
    public void The_battery_value_of_a_power_setting_is_read_beside_the_mains_value()
    {
        var reader = new SystemStateReader();
        var scheme = reader.ReadPower().ActiveScheme!;

        // USB selective suspend exists on every supported Windows; its two sides are each 0 or 1.
        var ac = reader.ReadPowerSetting(scheme, "2a737441-1930-4402-8d77-b2bebba308a3", "48e6b7a6-50f5-4782-a5d4-53bb8f07e226");
        var dc = reader.ReadPowerSettingDc(scheme, "2a737441-1930-4402-8d77-b2bebba308a3", "48e6b7a6-50f5-4782-a5d4-53bb8f07e226");

        Assert.True(ac is 0 or 1, ac?.ToString());
        Assert.True(dc is 0 or 1, dc?.ToString());
        Assert.Null(reader.ReadPowerSettingDc(scheme, "2a737441-1930-4402-8d77-b2bebba308a3", "00000000-0000-0000-0000-000000000001")); // not registered
    }
}
