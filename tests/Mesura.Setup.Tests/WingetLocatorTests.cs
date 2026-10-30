using Mesura.Setup.Windows.Apps;

namespace Mesura.Setup.Tests;

/// <summary>An elevated Mesura starts a winget it can trust, not whichever one the user's PATH finds first (docs/security.md).</summary>
public sealed class WingetLocatorTests
{
    private const string Local = @"C:\Profiles\me\AppData\Local";
    private const string Aliases = @"C:\Profiles\me\AppData\Local\Microsoft\WindowsApps";

    private static WingetLocator.Located Locate(string path, string[] files, string[] aliases, Func<string, string?>? writable = null) =>
        WingetLocator.ForElevatedLaunch(path, Local, f => files.Contains(f, StringComparer.OrdinalIgnoreCase), f => aliases.Contains(f, StringComparer.OrdinalIgnoreCase),
            writable ?? (folder => folder.StartsWith(@"C:\Profiles", StringComparison.OrdinalIgnoreCase) ? "users may write here" : null));

    [Fact]
    public void The_genuine_app_execution_alias_in_the_users_WindowsApps_folder_is_accepted()
    {
        var found = Locate($@"C:\Windows\system32;{Aliases}", [$@"{Aliases}\winget.exe"], [$@"{Aliases}\winget.exe"]);

        Assert.Equal($@"{Aliases}\winget.exe", found.Path);
        Assert.Null(found.Refusal);
    }

    [Fact]
    public void A_plain_file_planted_in_the_WindowsApps_folder_is_not_an_alias_and_is_refused()
    {
        var found = Locate(Aliases, [$@"{Aliases}\winget.exe"], []);

        Assert.Null(found.Path);
        Assert.Contains("plain file", found.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_winget_planted_in_a_user_folder_that_comes_first_on_the_path_is_refused_not_skipped()
    {
        var found = Locate($@"C:\Profiles\me\bin;{Aliases}", [@"C:\Profiles\me\bin\winget.exe", $@"{Aliases}\winget.exe"], [$@"{Aliases}\winget.exe"]);

        Assert.Null(found.Path);
        Assert.NotNull(found.Refusal);
    }

    [Fact]
    public void A_winget_in_a_protected_folder_is_accepted()
    {
        var found = Locate(@"C:\Program Files\Tools", [@"C:\Program Files\Tools\winget.exe"], []);

        Assert.Equal(@"C:\Program Files\Tools\winget.exe", found.Path);
    }

    [Fact]
    public void A_relative_path_entry_is_never_searched()
    {
        var found = Locate(@".;bin;C:\Program Files\Tools", [@"C:\Program Files\Tools\winget.exe", @".\winget.exe", @"bin\winget.exe"], []);

        Assert.Equal(@"C:\Program Files\Tools\winget.exe", found.Path);
    }

    [Fact]
    public void No_winget_at_all_is_not_a_refusal_it_is_the_old_not_installed_path()
    {
        var found = Locate(@"C:\Windows\system32", [], []);

        Assert.Null(found.Path);
        Assert.Null(found.Refusal);
    }
}
