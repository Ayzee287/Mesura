using Mesura.Platform.Windows.Collectors;

namespace Mesura.Platform.Tests;

/// <summary>
/// Which folders the Steam collector reads as libraries, and why (architecture §4.4 <c>steam</c>): each folder
/// <c>libraryfolders.vdf</c> names, plus Steam's install folder, once each under Windows path rules.
/// </summary>
public sealed class SteamLibraryFolderTests
{
    private const string Install = @"C:\Program Files (x86)\Steam";

    [Fact]
    public void The_install_folder_listed_in_the_vdf_is_one_library_marked_both_ways()
    {
        var folders = Folders(Install, [Install]);

        Assert.Equal([(Install, true, true)], folders);
    }

    [Fact]
    public void The_install_folder_absent_from_the_vdf_is_still_read_but_not_marked_as_listed()
    {
        var folders = Folders(Install, [@"D:\SteamLibrary"]);

        Assert.Equal([(Install, false, true), (@"D:\SteamLibrary", true, false)], folders);
    }

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Steam\")] // trailing separator
    [InlineData(@"c:/program files (x86)/steam")] // Steam's registry spelling: forward slashes, lower case
    [InlineData(@"C:\PROGRAM FILES (X86)\STEAM")] // letter case
    [InlineData(@"C:\Program Files (x86)\\Steam")] // a doubled separator
    public void Equivalent_spellings_of_the_install_folder_are_one_library(string listed)
    {
        var folders = Folders(Install, [listed]);

        Assert.Equal([(Install, true, true)], folders);
    }

    [Fact]
    public void A_library_listed_twice_in_equivalent_spellings_is_read_once()
    {
        var folders = Folders(Install, [@"D:\SteamLibrary", @"d:/steamlibrary/", @"E:\Games\Steam"]);

        Assert.Equal([(Install, false, true), (@"D:\SteamLibrary", true, false), (@"E:\Games\Steam", true, false)], folders);
    }

    [Fact]
    public void An_empty_list_leaves_only_the_install_folder()
    {
        var folders = Folders(Install, []);

        Assert.Equal([(Install, false, true)], folders);
    }

    [Fact]
    public void A_drive_root_library_keeps_its_root_separator()
    {
        var folders = Folders(Install, [@"F:\"]);

        Assert.Equal([(Install, false, true), (@"F:\", true, false)], folders);
    }

    private static List<(string Path, bool Listed, bool IsInstallFolder)> Folders(string install, string[] listed) =>
        [.. SteamCollector.LibraryFolders(install, listed)];
}
