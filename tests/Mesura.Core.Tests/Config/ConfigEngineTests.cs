using Mesura.Core.Config;

namespace Mesura.Core.Tests.Config;

/// <summary>
/// Export reads only allowlisted keys and lists exclusions; import writes only allowlisted keys, preserves unknown
/// data, honours scope, and is idempotent. Uses the real <see cref="ConfigCatalog"/> definitions.
/// </summary>
public sealed class ConfigEngineTests
{
    private static readonly ConfigPortDefinition Steam = ConfigCatalog.ById("steam")!;
    private static readonly ConfigPortDefinition Vencord = ConfigCatalog.ById("vencord")!;
    private static readonly ConfigPortDefinition Spotify = ConfigCatalog.ById("spotify")!;

    private const string SteamLocalConfig = """
        "UserLocalConfigStore"
        {
        	"system"
        	{
        		"EnableGameOverlay"		"1"
        		"PushToTalkKey"		"0"
        	}
        	"LibraryLowBandwidthMode"		"1"
        	"friends"
        	{
        		"PersonaName"		"private-name"
        	}
        	"Offline"
        	{
        		"Ticket"		"SECRET-SHOULD-NEVER-MOVE"
        	}
        }
        """;

    [Fact]
    public void Steam_export_captures_allowlisted_values_and_ignores_secrets()
    {
        var bundle = ConfigEngine.Export(Steam, new VdfConfigDocument(SteamLocalConfig));

        Assert.Equal("1", bundle.Values["steam.overlay.enabled"]);
        Assert.Equal("0", bundle.Values["steam.overlay.pushToTalkKey"]);
        Assert.Equal("1", bundle.Values["steam.library.lowBandwidth"]);
        // No login/friends value is anywhere in the bundle.
        Assert.DoesNotContain(bundle.Values.Values, v => v.Contains("SECRET", StringComparison.Ordinal));
        Assert.DoesNotContain(bundle.Values.Values, v => v.Contains("private-name", StringComparison.Ordinal));
        // Exclusions are shown in the preview.
        Assert.Contains(bundle.Preview, p => p.Status == CaptureStatus.Excluded && p.DisplayName.Contains("Login", StringComparison.Ordinal));
        // config.vdf-only settings are absent here, reported as not present (not an error).
        Assert.Contains(bundle.Preview, p => p.Id == "steam.musicVolume" && p.Status == CaptureStatus.NotPresent);
    }

    [Fact]
    public void Steam_import_preserves_unknown_keys_and_is_idempotent()
    {
        var bundle = new AppConfigBundle("steam", "Steam",
            new Dictionary<string, string> { ["steam.overlay.enabled"] = "0", ["steam.library.lowBandwidth"] = "1" }, []);

        var doc = new VdfConfigDocument(SteamLocalConfig);
        var first = ConfigEngine.Import(Steam, doc, bundle, _ => true);
        var afterOnce = doc.ToText();
        ConfigEngine.Import(Steam, doc, bundle, _ => true);
        var afterTwice = doc.ToText();

        Assert.Contains("steam.overlay.enabled", first.Applied);
        Assert.Equal(afterOnce, afterTwice); // idempotent
        var reparsed = new VdfConfigDocument(afterOnce);
        Assert.Equal("0", reparsed.TryGet("UserLocalConfigStore/system/EnableGameOverlay"));
        // The untouched secret and personal keys are still present verbatim.
        Assert.Contains("SECRET-SHOULD-NEVER-MOVE", afterOnce, StringComparison.Ordinal);
        Assert.Contains("private-name", afterOnce, StringComparison.Ordinal);
    }

    [Fact]
    public void Account_scoped_settings_are_skipped_until_scope_is_satisfied()
    {
        var bundle = new AppConfigBundle("steam", "Steam",
            new Dictionary<string, string> { ["steam.overlay.enabled"] = "1" }, []);
        var doc = new VdfConfigDocument(SteamLocalConfig);

        var result = ConfigEngine.Import(Steam, doc, bundle, scope => scope != ConfigScope.Account);

        Assert.Empty(result.Applied);
        Assert.Contains("steam.overlay.enabled", result.SkippedNotApplicable);
    }

    [Fact]
    public void Vencord_plugins_subtree_round_trips_and_preserves_unknown_top_level_keys()
    {
        const string settings = """
            {
              "autoUpdate": true,
              "plugins": { "NoTrack": { "enabled": true }, "VolumeBooster": { "enabled": true, "multiplier": 3 } },
              "cloud": { "authenticated": true, "url": "https://example" },
              "futureUnknownKey": 123
            }
            """;
        var bundle = ConfigEngine.Export(Vencord, new JsonConfigDocument(settings));

        Assert.Contains("VolumeBooster", bundle.Values["vencord.plugins"], StringComparison.Ordinal);
        // cloud is excluded: no cloud value in the bundle.
        Assert.DoesNotContain(bundle.Values, kv => kv.Value.Contains("authenticated", StringComparison.Ordinal));

        // Import into a fresh settings file; unknown keys and the cloud session survive untouched.
        const string destination = """{ "cloud": { "authenticated": false }, "keepMe": "x" }""";
        var doc = new JsonConfigDocument(destination);
        ConfigEngine.Import(Vencord, doc, bundle, _ => true);
        var outText = doc.ToText();

        Assert.Contains("VolumeBooster", outText, StringComparison.Ordinal);
        Assert.Contains("keepMe", outText, StringComparison.Ordinal);
        Assert.Contains("\"authenticated\":false", outText, StringComparison.Ordinal); // destination's own cloud flag unchanged
    }

    [Theory]
    [InlineData("{\"a\":1,\"b\":{\"c\":\"é <x>\"}}")] // compact, as Chromium writes Preferences
    [InlineData("{\n    \"a\": 1,\n    \"b\": {\n        \"c\": \"é <x>\"\n    }\n}")] // 4-space indent, as Vencord writes
    [InlineData("{\r\n  \"a\": 1,\r\n  \"b\": {\r\n    \"c\": \"é <x>\"\r\n  }\r\n}")] // 2-space indent, CRLF
    public void Json_documents_write_back_in_their_own_layout(string text)
    {
        Assert.Equal(text, new JsonConfigDocument(text).ToText());
    }

    [Fact]
    public void Spotify_prefs_round_trip_keeps_credentials_and_unknown_lines()
    {
        const string prefs = """
            app.autostart-mode=restored
            language=en
            autologin.username=secret-user
            autologin.blob=SECRETBLOB
            core.clock_delta=12
            """;
        var bundle = ConfigEngine.Export(Spotify, new PrefsConfigDocument(prefs));
        Assert.Equal("en", bundle.Values["spotify.language"]);
        Assert.DoesNotContain(bundle.Values.Values, v => v.Contains("secret", StringComparison.OrdinalIgnoreCase));

        var dest = new PrefsConfigDocument("autologin.username=other-user\nlanguage=de\n");
        ConfigEngine.Import(Spotify, dest, bundle, _ => true);
        var outText = dest.ToText();
        Assert.Contains("language=en", outText, StringComparison.Ordinal);       // replaced in place
        Assert.Contains("autologin.username=other-user", outText, StringComparison.Ordinal); // destination login untouched
        Assert.Single(outText.Split('\n'), l => l.StartsWith("language=", StringComparison.Ordinal)); // no duplicate
    }

    [Fact]
    public void Every_catalog_path_is_well_formed_and_ids_are_unique()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var port in ConfigCatalog.All)
        {
            Assert.NotEmpty(port.Settings);
            foreach (var s in port.Settings)
            {
                Assert.True(ids.Add(s.Id), $"duplicate id {s.Id}");
                Assert.False(string.IsNullOrWhiteSpace(s.Path), s.Id);
                Assert.StartsWith(port.Id + ".", s.Id, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void A_value_with_no_matching_allowlist_entry_is_never_written()
    {
        var bundle = new AppConfigBundle("spotify", "Spotify",
            new Dictionary<string, string> { ["spotify.unknown.injected"] = "x", ["spotify.language"] = "fr" }, []);
        var doc = new PrefsConfigDocument("language=en\n");
        var result = ConfigEngine.Import(Spotify, doc, bundle, _ => true);

        Assert.Equal(["spotify.language"], result.Applied);
        Assert.DoesNotContain("injected", doc.ToText(), StringComparison.Ordinal);
    }
}
