using Mesura.Updating;

namespace Mesura.Updating.Tests;

public sealed class InstallRegistrationTests
{
    [Theory]
    [InlineData(@"C:\Profiles\me\AppData\Local\Programs\Mesura\", @"C:\Profiles\me\AppData\Local\Programs\Mesura")]
    [InlineData(@"C:\Program Files\Mesura", @"C:\Program Files\Mesura")]
    [InlineData(@"D:\Программы Mesura\", @"D:\Программы Mesura")]
    public void The_install_folder_has_no_trailing_separator_so_it_survives_a_quoted_command_line(string stored, string expected) =>
        Assert.Equal(expected, InstallRegistration.NormalizeFolder(stored));

    [Fact]
    public void The_updater_passes_the_folder_without_a_trailing_backslash()
    {
        var info = new InstallInfo(InstallScope.User, InstallRegistration.NormalizeFolder(@"D:\Программы Mesura\"), "0.3.0");

        Assert.False(info.Folder.EndsWith('\\'));
        Assert.Equal(@"D:\Программы Mesura\Mesura.exe", info.Executable);
        Assert.Equal(@"D:\Программы Mesura\cli\mesura.exe", info.CommandLine);
    }
}
