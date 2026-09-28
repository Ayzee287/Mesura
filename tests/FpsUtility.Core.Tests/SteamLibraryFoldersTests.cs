using FpsUtility.Core.Raw;

namespace FpsUtility.Core.Tests;

/// <summary>Which folders the steam collector reads: both libraryfolders.vdf layouts, and nothing guessed from a broken file.</summary>
public sealed class SteamLibraryFoldersTests
{
    [Fact]
    public void Current_layout_yields_each_library_path()
    {
        const string vdf = """
            "libraryfolders"
            {
            	"0"
            	{
            		"path"		"C:\\Program Files (x86)\\Steam"
            		"label"		""
            		"contentid"		"123"
            		"apps"
            		{
            			"228980"		"456"
            		}
            	}
            	"1"
            	{
            		"path"		"D:\\SteamLibrary"
            	}
            }
            """;

        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], SteamLibraryFolders.ParsePaths(vdf));
    }

    [Fact]
    public void Older_layout_yields_numbered_paths_and_skips_other_keys()
    {
        const string vdf = """
            "LibraryFolders"
            {
            	"TimeNextStatsReport"		"1600000000"
            	"ContentStatsID"		"-1"
            	"1"		"D:\\SteamLibrary"
            }
            """;

        Assert.Equal([@"D:\SteamLibrary"], SteamLibraryFolders.ParsePaths(vdf));
    }

    [Theory]
    [InlineData("\"libraryfolders\" { \"0\" { \"path\" \"C:\\\\Steam\" }")] // unclosed block
    [InlineData("\"libraryfolders\" { \"0\" { \"path\" \"C:\\\\Steam }")] // unclosed string
    [InlineData("\"config\" { }")] // not a library list
    public void A_broken_file_is_an_error_not_a_guess(string vdf)
    {
        Assert.Throws<FormatException>(() => SteamLibraryFolders.ParsePaths(vdf));
    }
}
