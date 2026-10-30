using Mesura.Core.Apps;
using Mesura.Setup.Windows.Apps;

namespace Mesura.Setup.Tests;

/// <summary>Which folders Mesura passes to an installer, and how AyuGram is found without winget.</summary>
public sealed class InstallFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mesura-folder-" + Guid.NewGuid().ToString("N"));

    public InstallFolderTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_full_local_folder_is_accepted_and_normalised()
    {
        var chosen = Path.Combine(_root, "Apps", "..", "Games") + @"\";
        Assert.Equal(Path.Combine(_root, "Games"), InstallFolders.Valid(chosen));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"relative\folder")]
    [InlineData(@"\\server\share\apps")]
    [InlineData(@"C:\")]
    [InlineData("C:\\Apps\\\"quoted\"")]
    public void A_folder_that_is_missing_relative_remote_a_drive_root_or_quoted_is_refused(string? folder) => Assert.Null(InstallFolders.Valid(folder));

    [Fact]
    public void Windows_own_folder_is_refused()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.Null(InstallFolders.Valid(windows));
        Assert.Null(InstallFolders.Valid(Path.Combine(windows, "System32", "Apps")));

        // A sibling whose name merely starts with "Windows" is an ordinary folder.
        Assert.Equal(windows + " Games", InstallFolders.Valid(windows + " Games"));
    }

    [Fact]
    public void A_drive_that_isnt_there_is_refused()
    {
        var free = Enumerable.Range('A', 26).Select(c => (char)c).FirstOrDefault(c => !Directory.Exists(c + @":\"));
        Assert.NotEqual('\0', free);
        Assert.Null(InstallFolders.Valid(free + @":\Apps"));
    }

    [Fact]
    public void Ayugram_is_found_only_by_a_file_that_says_it_is_ayugram()
    {
        var programFiles = Path.Combine(_root, "Program Files");
        var local = Path.Combine(_root, "Local");
        Directory.CreateDirectory(Path.Combine(programFiles, "AyuGram"));
        Directory.CreateDirectory(local);
        Assert.Equal(InstallState.NotInstalled, AyuGramDetector.Detect(local, programFiles));

        // A file of that name that is some other program's doesn't count: its product name isn't AyuGram's.
        File.WriteAllBytes(Path.Combine(programFiles, "AyuGram", "AyuGram.exe"), [0x4D, 0x5A, 0, 0]);
        Assert.Equal(InstallState.NotInstalled, AyuGramDetector.Detect(local, programFiles));
    }
}
