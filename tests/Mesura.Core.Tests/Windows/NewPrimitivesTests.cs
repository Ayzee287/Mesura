using Mesura.Core.Windows;

namespace Mesura.Core.Tests.Windows;

/// <summary>
/// The 0.2.0 primitives: binary values, settings applied to every matching subkey (network adapters), settings changed
/// through a Windows tool, a stated level and sources. Catalogs here are small and written for the test, so each primitive
/// is exercised without depending on the built-in entries.
/// </summary>
public sealed class NewPrimitivesTests
{
    private const string Interfaces = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";

    private static WindowsCatalog Catalog(string operations) =>
        WindowsCatalog.Parse($$"""{ "kind": "Mesura.WindowsCatalog", "schemaVersion": 4, "operations": [ {{operations}} ], "presets": [] }""");

    private static string Op(string body) =>
        $$"""{ "id": "test.op", "name": "Test", "category": "System", "description": "d", "risk": "systemConfiguration", {{body}} }""";

    [Fact]
    public void A_binary_value_is_written_read_back_normalised_and_restored()
    {
        var op = Catalog(Op("""
            "registry": [ { "hive": "HKCU", "path": "Control Panel\\Desktop", "name": "Mask", "type": "binary", "value": "90,12,03,80" } ]
            """)).Operations.Single();
        var pc = new FakeWindows();
        pc.Registry[@"HKCU\Control Panel\Desktop\Mask"] = (RegistryKind.Binary, "9e,3E,07,80");

        var outcome = new WindowsConfigurator(pc, pc).Apply([op], TestContext.Current.CancellationToken).Single();

        Assert.Equal(WindowsOpStatus.Applied, outcome.Status);
        Assert.Equal((RegistryKind.Binary, "90,12,03,80"), pc.Registry[@"HKCU\Control Panel\Desktop\Mask"]);
        var again = new WindowsConfigurator(pc, pc).Apply([op], TestContext.Current.CancellationToken).Single();
        Assert.Equal(WindowsOpStatus.AlreadyApplied, again.Status); // 90,12,03,80 equals itself however it is spelled

        new WindowsConfigurator(pc, pc).Revert([outcome]);
        Assert.Equal((RegistryKind.Binary, "9e,3E,07,80"), pc.Registry[@"HKCU\Control Panel\Desktop\Mask"]);
    }

    [Theory]
    [InlineData("9e,3")]
    [InlineData("zz")]
    [InlineData("")]
    [InlineData("9e;3e")]
    public void A_malformed_binary_value_is_refused(string value)
    {
        var error = Assert.Throws<FormatException>(() => Catalog(Op($$"""
            "registry": [ { "hive": "HKCU", "path": "A", "name": "B", "type": "binary", "value": "{{value}}" } ]
            """)));
        Assert.Contains("binary", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_per_adapter_setting_is_applied_to_each_subkey_that_declares_it_and_to_no_other()
    {
        var op = Catalog(Op($$"""
            "registryEach": [ { "hive": "HKLM", "pathPattern": "{{Interfaces.Replace("\\", "\\\\", StringComparison.Ordinal)}}\\*", "name": "TcpAckFrequency", "type": "dword", "value": "1",
                                "absentMatches": false, "where": { "subkeyExists": "Sub", "valueNotEquals": { "name": "Skip", "value": "yes" } } } ]
            """)).Operations.Single();
        var pc = new FakeWindows();
        pc.Keys.Add($@"HKLM\{Interfaces}\{{a}}\Sub");
        pc.Keys.Add($@"HKLM\{Interfaces}\{{b}}\Sub");
        pc.Registry[$@"HKLM\{Interfaces}\{{b}}\Skip"] = (RegistryKind.String, "yes"); // excluded by the filter
        pc.Keys.Add($@"HKLM\{Interfaces}\{{c}}");                                     // doesn't declare the keyword

        var outcome = new WindowsConfigurator(pc, pc).Apply([op], TestContext.Current.CancellationToken).Single();

        Assert.Equal(WindowsOpStatus.Applied, outcome.Status);
        var change = Assert.Single(outcome.Changes);
        Assert.Contains("{a}", change.Target, StringComparison.Ordinal);
        Assert.Equal("1", pc.Registry[$@"HKLM\{Interfaces}\{{a}}\TcpAckFrequency"].Value);
        Assert.DoesNotContain(pc.Registry.Keys, k => k.Contains("{b}\\TcpAckFrequency", StringComparison.Ordinal) || k.Contains("{c}\\TcpAckFrequency", StringComparison.Ordinal));

        new WindowsConfigurator(pc, pc).Revert([outcome]);
        Assert.DoesNotContain(pc.Registry.Keys, k => k.EndsWith("TcpAckFrequency", StringComparison.Ordinal));
    }

    [Fact]
    public void A_per_adapter_setting_on_a_pc_without_matching_subkeys_is_not_applicable()
    {
        var op = Catalog(Op($$"""
            "registryEach": [ { "hive": "HKLM", "pathPattern": "{{Interfaces.Replace("\\", "\\\\", StringComparison.Ordinal)}}\\*", "name": "X", "type": "dword", "value": "1" } ]
            """)).Operations.Single();

        var evaluation = WindowsEvaluator.Evaluate(op, new FakeWindows());

        Assert.Equal(OperationState.Inapplicable, evaluation.State);
    }

    [Theory]
    [InlineData("A\\\\*\\\\B\\\\*")]   // two stars
    [InlineData("*\\\\A")]            // nothing fixed before the star
    [InlineData("A\\\\x*")]           // a star inside a segment
    [InlineData("A\\\\B")]            // no star
    public void A_pattern_without_exactly_one_star_segment_is_refused(string pattern)
    {
        Assert.Throws<FormatException>(() => Catalog(Op($$"""
            "registryEach": [ { "hive": "HKLM", "pathPattern": "{{pattern}}", "name": "X", "type": "dword", "value": "1" } ]
            """)));
    }

    [Fact]
    public void A_tool_backed_setting_is_changed_recorded_and_put_back_and_an_unreadable_one_is_left_alone()
    {
        var op = Catalog(Op("""
            "system": [ { "kind": "bcd", "name": "useplatformclock", "value": "absent" } ]
            """)).Operations.Single();
        var pc = new FakeWindows();
        pc.System["bcd:useplatformclock"] = "yes";

        var outcome = new WindowsConfigurator(pc, pc).Apply([op], TestContext.Current.CancellationToken).Single();

        Assert.Equal(WindowsOpStatus.Applied, outcome.Status);
        Assert.Equal("absent", pc.System["bcd:useplatformclock"]);
        Assert.Equal("yes", Assert.Single(outcome.Changes).Before);
        new WindowsConfigurator(pc, pc).Revert([outcome]);
        Assert.Equal("yes", pc.System["bcd:useplatformclock"]);

        pc.System.Remove("bcd:useplatformclock"); // the tool couldn't be read (not elevated): nothing is guessed
        var unread = new WindowsConfigurator(pc, pc).Apply([op], TestContext.Current.CancellationToken).Single();
        Assert.Equal(WindowsOpStatus.Unknown, unread.Status);
    }

    [Fact]
    public void A_tool_setting_that_reads_a_word_it_cannot_restore_is_left_alone()
    {
        var op = Catalog(Op("""
            "system": [ { "kind": "bcd", "name": "useplatformclock", "value": "absent" } ]
            """)).Operations.Single();
        var pc = new FakeWindows();
        pc.System["bcd:useplatformclock"] = "да"; // a boolean bcdedit printed in another language

        var outcome = new WindowsConfigurator(pc, pc).Apply([op], TestContext.Current.CancellationToken).Single();

        Assert.Equal(WindowsOpStatus.Failed, outcome.Status);
        Assert.Contains("can't restore", outcome.Detail, StringComparison.Ordinal);
        Assert.Empty(pc.Writes);
    }

    [Theory]
    [InlineData("bcd", "useplatformclock", "maybe")]
    [InlineData("bcd", "bootmenupolicy", "legacy")]
    [InlineData("shell", "cmd", "x")]
    public void Only_listed_tool_settings_exist(string kind, string name, string value)
    {
        Assert.Throws<FormatException>(() => Catalog(Op($$"""
            "system": [ { "kind": "{{kind}}", "name": "{{name}}", "value": "{{value}}" } ]
            """)));
    }

    [Fact]
    public void A_stated_level_and_sources_are_read_and_bad_ones_are_refused()
    {
        var op = Catalog(Op("""
            "level": "high", "sources": [ "https://learn.microsoft.com/windows/" ],
            "registry": [ { "hive": "HKCU", "path": "A", "name": "B", "type": "dword", "value": "1" } ]
            """)).Operations.Single();

        Assert.Equal(OperationLevel.High, op.Level);
        Assert.Equal("https://learn.microsoft.com/windows/", Assert.Single(op.SourceLinks));

        Assert.Throws<FormatException>(() => Catalog(Op("""
            "level": "extreme", "registry": [ { "hive": "HKCU", "path": "A", "name": "B", "type": "dword", "value": "1" } ]
            """)));
        Assert.Throws<FormatException>(() => Catalog(Op("""
            "sources": [ "file:///c:/secret" ], "registry": [ { "hive": "HKCU", "path": "A", "name": "B", "type": "dword", "value": "1" } ]
            """)));
    }

    private const string News = "Microsoft.BingNews_4.55.62231.0_x64__8wekyb3d8bbwe";

    [Fact]
    public void A_packaged_app_is_removed_recorded_and_registered_again_on_revert()
    {
        var op = Catalog(Op("""
            "appx": [ { "name": "Microsoft.BingNews" } ]
            """)).Operations.Single();
        var pc = new FakeWindows();
        pc.Apps.Add(News);

        Assert.Equal(OperationState.NotApplied, WindowsEvaluator.Evaluate(op, pc).State);
        var outcome = new WindowsConfigurator(pc, pc).Apply([op], TestContext.Current.CancellationToken).Single();

        Assert.Equal(WindowsOpStatus.Applied, outcome.Status);
        Assert.Empty(pc.Apps);
        Assert.Equal(News, Assert.Single(outcome.Changes).Path);
        Assert.Equal(OperationState.Applied, WindowsEvaluator.Evaluate(op, pc).State);
        Assert.Equal(WindowsOpStatus.AlreadyApplied, new WindowsConfigurator(pc, pc).Apply([op], TestContext.Current.CancellationToken).Single().Status);

        var reverted = new WindowsConfigurator(pc, pc).Revert([outcome]).Single();
        Assert.Equal(WindowsOpStatus.Reverted, reverted.Status);
        Assert.Contains("restore Microsoft.BingNews_8wekyb3d8bbwe", pc.Writes);
        Assert.Single(pc.ReadAppxPackages("Microsoft.BingNews")!);
    }

    [Fact]
    public void A_revert_that_finds_no_staged_copy_says_so_instead_of_claiming_success()
    {
        var op = Catalog(Op("""
            "appx": [ { "name": "Microsoft.BingNews" } ]
            """)).Operations.Single();
        var pc = new FakeWindows();
        pc.Apps.Add(News);
        var outcome = new WindowsConfigurator(pc, pc).Apply([op], TestContext.Current.CancellationToken).Single();
        pc.StagedFamilies.Clear(); // the package files were cleaned up

        var reverted = new WindowsConfigurator(pc, pc).Revert([outcome]).Single();

        Assert.Equal(WindowsOpStatus.Failed, reverted.Status);
        Assert.Contains("no staged copy", reverted.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_app_that_is_not_installed_counts_as_done_and_nothing_is_written()
    {
        var op = Catalog(Op("""
            "appx": [ { "name": "Microsoft.BingNews" } ]
            """)).Operations.Single();
        var pc = new FakeWindows();

        var outcome = new WindowsConfigurator(pc, pc).Apply([op], TestContext.Current.CancellationToken).Single();

        Assert.Equal(WindowsOpStatus.AlreadyApplied, outcome.Status);
        Assert.Empty(pc.Writes);
    }

    [Theory]
    [InlineData("Microsoft.WindowsStore")]
    [InlineData("Microsoft.DesktopAppInstaller")]
    [InlineData("Microsoft.UI.Xaml.2.8")]
    [InlineData("MicrosoftWindows.Client.CBS")]
    [InlineData("Microsoft.Windows.ShellExperienceHost")]
    [InlineData("Microsoft.SecHealthUI")]
    [InlineData("Bad Name")]
    [InlineData("x")]
    public void Windows_own_packages_and_malformed_names_can_never_be_listed_for_removal(string name)
    {
        Assert.Throws<FormatException>(() => Catalog(Op($$"""
            "appx": [ { "name": "{{name}}" } ]
            """)));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("02,00,00,00,00,00,00,00,00,00,00,00", false)]
    [InlineData("06,00,00,00,00,00,00,00,00,00,00,00", false)]
    [InlineData("03,00,00,00,a1,b2,c3,d4,e5,f6,07,08", true)]
    [InlineData("07,00,00,00,00,00,00,00,00,00,00,00", true)]
    public void Windows_treats_an_odd_first_byte_of_the_startup_flag_as_turned_off(string? flag, bool disabled) =>
        Assert.Equal(disabled, StartupItem.IsDisabledFlag(flag));

    [Fact]
    public void Turning_a_startup_program_off_writes_the_flag_Windows_uses_and_undo_restores_the_earlier_one()
    {
        var item = new StartupItem(StartupSource.UserRun, "Spotify", @"C:\Spotify\Spotify.exe --autostart", Enabled: true);
        var op = item.ToOperation("Turn off Spotify", item.Command, "d", "l");
        var pc = new FakeWindows();
        var flag = $@"HKCU\{item.ApprovedPath}\Spotify";
        pc.Registry[flag] = (RegistryKind.Binary, "02,00,00,00,00,00,00,00,00,00,00,00"); // Task Manager had it on

        var outcome = new WindowsConfigurator(pc, pc).Apply([op], TestContext.Current.CancellationToken).Single();

        Assert.Equal(WindowsOpStatus.Applied, outcome.Status);
        Assert.True(StartupItem.IsDisabledFlag(pc.Registry[flag].Value));
        new WindowsConfigurator(pc, pc).Revert([outcome]);
        Assert.Equal("02,00,00,00,00,00,00,00,00,00,00,00", pc.Registry[flag].Value);
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", item.ApprovedPath);
    }

    [Fact]
    public void A_machine_startup_entry_is_flagged_under_the_machine_hive_and_needs_administrator_rights()
    {
        var item = new StartupItem(StartupSource.MachineRun32, "OldTool", "x", true);
        var op = item.ToOperation("Turn off OldTool", "x", "d", "l");

        Assert.Equal("HKLM", item.Hive);
        Assert.EndsWith(@"StartupApproved\Run32", item.ApprovedPath, StringComparison.Ordinal);
        Assert.True(op.NeedsElevation);
    }

    [Fact]
    public void Two_settings_that_set_the_same_tool_setting_differently_conflict()
    {
        var catalog = WindowsCatalog.Parse("""
            { "kind": "Mesura.WindowsCatalog", "schemaVersion": 4, "operations": [
              { "id": "a", "name": "A", "category": "System", "description": "d", "risk": "systemConfiguration", "system": [ { "kind": "hibernate", "name": "enabled", "value": "off" } ] },
              { "id": "b", "name": "B", "category": "System", "description": "d", "risk": "systemConfiguration", "system": [ { "kind": "hibernate", "name": "enabled", "value": "on" } ] } ],
              "presets": [] }
            """);
        var pc = new FakeWindows();
        pc.System["hibernate:enabled"] = "on";

        Assert.NotEmpty(WindowsConfigurator.Conflicts(catalog.Operations, pc));
    }
}
