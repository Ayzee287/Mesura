using Mesura.Core.Defaults;
using Mesura.Core.Tests.Windows;
using Mesura.Core.Windows;

namespace Mesura.Core.Tests.Defaults;

public sealed class DefaultBrowserTests
{
    private static string Key => $@"HKCU\{DefaultBrowsers.UserChoiceKey}\ProgId";

    [Theory]
    [InlineData("BraveHTML", "brave")]
    [InlineData("ChromeHTML", "chrome")]
    [InlineData("FirefoxURL-308046B0AF4A39CB", "firefox")]
    [InlineData("MSEdgeHTM", "")]
    [InlineData("braveHTML", "brave")]
    public void A_program_identifier_names_its_browser(string progId, string id)
    {
        var pc = new FakeWindows();
        pc.Registry[Key] = (RegistryKind.String, progId);

        var state = DefaultBrowsers.Read(pc);

        Assert.True(state.Chosen);
        Assert.Equal(progId, state.ProgId);
        Assert.Equal(id, state.Browser!.Id);
    }

    [Fact]
    public void Nothing_chosen_reads_as_windows_own_default_edge()
    {
        var state = DefaultBrowsers.Read(new FakeWindows());

        Assert.False(state.Chosen);
        Assert.Null(state.ProgId);
        Assert.Same(DefaultBrowsers.Edge, state.Browser);
    }

    [Fact]
    public void A_program_Mesura_does_not_know_is_chosen_but_unnamed()
    {
        var pc = new FakeWindows();
        pc.Registry[Key] = (RegistryKind.String, "VivaldiHTM.ABCDEF");

        var state = DefaultBrowsers.Read(pc);

        Assert.True(state.Chosen);
        Assert.Null(state.Browser);
        Assert.False(state.Is("brave"));
    }

    [Fact]
    public void An_unreadable_key_or_an_unknown_user_claims_nothing()
    {
        var pc = new FakeWindows();
        pc.Registry[Key] = (RegistryKind.String, "BraveHTML");
        pc.Unreadable.Add(Key);

        Assert.False(DefaultBrowsers.Read(pc).Chosen);
    }

    [Fact]
    public void The_settings_page_names_the_app_and_stays_inside_ms_settings()
    {
        foreach (var browser in DefaultBrowsers.Known)
        {
            Assert.StartsWith("ms-settings:defaultapps?registeredAppUser=", browser.SettingsUri, StringComparison.Ordinal);
            Assert.Equal(browser.SettingsName, Uri.UnescapeDataString(browser.SettingsUri["ms-settings:defaultapps?registeredAppUser=".Length..]));
        }

        Assert.EndsWith("Google%20Chrome", DefaultBrowsers.ByAppId("chrome")!.SettingsUri, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_catalog_browsers_are_found_by_app_id()
    {
        Assert.Equal("Brave", DefaultBrowsers.ByAppId("brave")!.Name);
        Assert.Null(DefaultBrowsers.ByAppId("discord"));
        Assert.Null(DefaultBrowsers.ByAppId(""));
    }
}
