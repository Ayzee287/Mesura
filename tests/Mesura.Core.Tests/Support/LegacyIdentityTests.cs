using Mesura.Core.Apps;
using Mesura.Core.Config;
using Mesura.Core.Setup;
using Mesura.Core.Support;

namespace Mesura.Core.Tests.Support;

/// <summary>Files written before the rename to Mesura (D022) stay readable; new files carry the Mesura names.</summary>
public sealed class LegacyIdentityTests
{
    [Fact]
    public void Kinds_match_their_current_and_pre_rename_spelling_only()
    {
        Assert.True(LegacyIdentity.IsKind("Mesura.ConfigBundle", ConfigBundleFile.FileKind));
        Assert.True(LegacyIdentity.IsKind("FpsUtility.ConfigBundle", ConfigBundleFile.FileKind));
        Assert.False(LegacyIdentity.IsKind("FpsUtility.SetupProfile", ConfigBundleFile.FileKind));
        Assert.False(LegacyIdentity.IsKind("Other.ConfigBundle", ConfigBundleFile.FileKind));
        Assert.False(LegacyIdentity.IsKind(null, ConfigBundleFile.FileKind));
    }

    [Fact]
    public void A_config_bundle_exported_before_the_rename_still_loads_and_a_new_one_is_a_Mesura_bundle()
    {
        var old = ConfigBundleFile.Load("""
            { "kind": "FpsUtility.ConfigBundle", "schemaVersion": 1, "createdUtc": "2026-10-18T00:00:00Z",
              "apps": { "spotify": { "appName": "Spotify", "values": { "spotify.language": "\"en\"" } } } }
            """);
        Assert.Equal("\"en\"", old.ForApp("spotify")!.Values["spotify.language"]);

        var saved = ConfigBundleFile.Save([new AppConfigBundle("spotify", "Spotify", old.ForApp("spotify")!.Values, [])], "2026-10-18T00:00:00Z");
        Assert.Contains("\"kind\": \"Mesura.ConfigBundle\"", saved, StringComparison.Ordinal);
    }

    [Fact]
    public void Pre_rename_catalogs_profiles_and_history_records_still_load()
    {
        var catalog = AppCatalog.Parse("""
            { "kind": "FpsUtility.AppCatalog", "schemaVersion": 1, "apps": [ { "id": "a", "wingetId": "A.A", "name": "A" } ] }
            """);
        Assert.Equal("a", catalog.Packages.Single().Id);

        var profile = SetupProfile.Parse("""
            { "kind": "FpsUtility.SetupProfile", "schemaVersion": 1, "name": "Old", "apps": ["a"] }
            """, catalog);
        Assert.Equal("Old", profile.Name);

        var run = SetupRun.Create("20261018T000000Z-profile", "profile", "Old", "a", "b", [], []);
        var oldJson = run.ToJson().Replace("\"Mesura.SetupRun\"", "\"FpsUtility.SetupRun\"", StringComparison.Ordinal);
        Assert.Equal(run.Id, SetupRun.FromJson(oldJson).Id);
        Assert.Contains("\"Mesura.SetupRun\"", run.ToJson(), StringComparison.Ordinal);
    }
}
