using Mesura.Core.Config;
using Mesura.Core.Setup;

namespace Mesura.Core.Tests.Setup;

/// <summary>"Open a backup" sends a file to the PC setup screen or the app settings screen by the kind the file carries.</summary>
public sealed class BackupFileTests
{
    [Fact]
    public void A_PC_setup_written_by_this_build_is_a_PC_setup()
    {
        var json = new SetupProfile("my-setup", "", ["steam"], [], null, [], [], []).ToJson();

        Assert.Equal(BackupKind.PcSetup, BackupFile.Detect(json));
    }

    [Fact]
    public void An_app_settings_file_written_by_this_build_is_app_settings()
    {
        var json = ConfigBundleFile.Save([new AppConfigBundle("spotify", "Spotify", new Dictionary<string, string> { ["spotify.language"] = "\"en\"" }, [])], "2026-11-07T00:00:00Z");

        Assert.Equal(BackupKind.AppSettings, BackupFile.Detect(json));
    }

    [Theory]
    [InlineData("""{ "kind": "FpsUtility.SetupProfile", "schemaVersion": 1, "name": "old" }""", BackupKind.PcSetup)]
    [InlineData("""{ "kind": "FpsUtility.ConfigBundle", "schemaVersion": 1, "apps": {} }""", BackupKind.AppSettings)]
    [InlineData("""{ "Kind": "Mesura.SetupProfile" }""", BackupKind.PcSetup)]
    public void Files_from_before_the_rename_still_go_where_they_belong(string json, BackupKind expected) =>
        Assert.Equal(expected, BackupFile.Detect(json));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    [InlineData("""{ "name": "no kind" }""")]
    [InlineData("""{ "kind": 7 }""")]
    [InlineData("""{ "kind": "Mesura.AppCatalog" }""")]
    [InlineData("""{ "kind": "Mesura.SetupRun" }""")]
    [InlineData("""{ "kind": "Other.SetupProfile" }""")]
    [InlineData("""{ "kind": "Mesura.SetupProfile" """)]
    public void Anything_else_is_not_a_backup(string? json) =>
        Assert.Equal(BackupKind.Unknown, BackupFile.Detect(json));
}
