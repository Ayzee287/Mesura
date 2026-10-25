using Mesura.Core.Apps;

namespace Mesura.Core.Tests.Apps;

public sealed class WingetOutputTests
{
    // Real `winget list --exact --id Brave.Brave` output on this machine (2026-10-18, winget 1.29), with the spinner
    // frames winget prints before the table. Brave was installed by its own installer, not winget.
    private const string BraveList = """
           -
           \
        Name  Id          Version     Source
        -------------------------------------
        Brave Brave.Brave 155.1.97.59 winget
        """;

    [Fact]
    public void Exit_zero_with_the_id_in_the_table_is_installed()
    {
        Assert.Equal(InstallState.Installed, WingetOutput.DetectState(new WingetRunResult(true, 0, BraveList), "Brave.Brave"));
        Assert.Equal(InstallState.Installed, WingetOutput.DetectState(new WingetRunResult(true, 0, BraveList), "brave.brave"));
    }

    [Fact]
    public void No_applications_found_is_not_installed_whatever_the_message_language()
    {
        var list = new WingetRunResult(true, WingetOutput.NoApplicationsFound, "Aucun package installé ne correspond.");
        Assert.Equal(InstallState.NotInstalled, WingetOutput.DetectState(list, "Zoom.Zoom"));
    }

    [Theory]
    [InlineData(false, 0, "")] // winget not launched
    [InlineData(true, unchecked((int)0x8A15000F), "Failed when opening source(s)")] // a source error
    [InlineData(true, 0, "Name Id\n-------\nOther Other.App")] // the table lists something else
    public void Anything_else_is_unknown_rather_than_guessed(bool launched, int exitCode, string output)
    {
        Assert.Equal(InstallState.Unknown, WingetOutput.DetectState(new WingetRunResult(launched, exitCode, output), "Brave.Brave"));
    }

    [Theory]
    [InlineData(0, InstallStatus.Succeeded)]
    [InlineData(unchecked((int)0x8A150061), InstallStatus.AlreadyInstalled)]
    [InlineData(unchecked((int)0x8A15010E), InstallStatus.AlreadyInstalled)]
    [InlineData(unchecked((int)0x8A150109), InstallStatus.SucceededRebootRequired)]
    [InlineData(unchecked((int)0x8A15010C), InstallStatus.Cancelled)]
    [InlineData(unchecked((int)0x8A150010), InstallStatus.Failed)]
    [InlineData(unchecked((int)0x8A150069), InstallStatus.Failed)] // "installed package is a stub": not a cancel
    [InlineData(unchecked((int)0x80004005), InstallStatus.Failed)]
    public void Describe_install_maps_documented_exit_codes(int code, InstallStatus expected)
    {
        Assert.Equal(expected, WingetOutput.DescribeInstall(code).Status);
    }

    [Fact]
    public void Unknown_failure_includes_the_hex_code()
    {
        var (status, detail) = WingetOutput.DescribeInstall(unchecked((int)0x80004005));
        Assert.Equal(InstallStatus.Failed, status);
        Assert.Contains("0x80004005", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("-", true)]
    [InlineData("  \\ ", true)]
    [InlineData("  ██████████▒▒▒▒  10.0 MB / 120 MB", true)]
    [InlineData("", true)]
    [InlineData("Successfully installed", false)]
    public void Spinner_and_progress_lines_are_noise(string line, bool noise)
    {
        Assert.Equal(noise, WingetOutput.IsNoise(line));
    }
}
