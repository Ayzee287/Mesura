using Mesura.Updating;

namespace Mesura.Updating.Tests;

/// <summary>
/// msiexec parses its own command line. "INSTALLFOLDER=C:\a b" (what a generic argument list produces for a value with a space) makes it show
/// its usage box and wait for a click that nobody sees; INSTALLFOLDER="C:\a b" is what it understands. Found by the end-to-end update check.
/// </summary>
public sealed class InstallerCommandLineTests
{
    [Fact]
    public void A_property_with_spaces_and_cyrillic_is_quoted_around_its_value_not_around_the_whole_token()
    {
        var line = InstallerLauncher.BuildCommandLine(["/i", @"C:\Profiles\me\updates\Mesura-0.3.1-x64.msi", "/qb!", "/norestart", "/l*v", @"C:\Profiles\me\updates\update-install.log", "ALLUSERS=2", "MSIINSTALLPERUSER=1", @"INSTALLFOLDER=D:\Программы Mesura"]);

        Assert.Equal(@"/i C:\Profiles\me\updates\Mesura-0.3.1-x64.msi /qb! /norestart /l*v C:\Profiles\me\updates\update-install.log ALLUSERS=2 MSIINSTALLPERUSER=1 INSTALLFOLDER=""D:\Программы Mesura""", line);
    }

    [Fact]
    public void A_package_path_with_spaces_is_quoted_whole()
    {
        var line = InstallerLauncher.BuildCommandLine(["/i", @"C:\My Updates\Mesura.msi"]);

        Assert.Equal(@"/i ""C:\My Updates\Mesura.msi""", line);
    }

    [Fact]
    public void A_quote_inside_a_value_is_doubled()
    {
        Assert.Equal(@"X=""a""""b c""", InstallerLauncher.BuildCommandLine([@"X=a""b c"]));
    }

    [Fact]
    public void A_switch_that_contains_an_equals_sign_is_not_a_property()
    {
        Assert.Equal("/l*v=x", InstallerLauncher.BuildCommandLine(["/l*v=x"]));
    }
}
