using Mesura.Core.Config;

namespace Mesura.Core.Tests.Config;

public sealed class ConfigBundleFileTests
{
    private static AppConfigBundle Bundle(string id, string name, params (string, string)[] values) =>
        new(id, name, values.ToDictionary(v => v.Item1, v => v.Item2, StringComparer.Ordinal), []);

    [Fact]
    public void Save_then_load_round_trips_values_and_drops_empty_apps()
    {
        var json = ConfigBundleFile.Save(
            [Bundle("steam", "Steam", ("steam.overlay.enabled", "1")), Bundle("brave", "Brave")],
            "2026-10-18T00:00:00Z");
        var file = ConfigBundleFile.Load(json);

        Assert.Equal("2026-10-18T00:00:00Z", file.CreatedUtc);
        Assert.True(file.Apps.ContainsKey("steam"));
        Assert.False(file.Apps.ContainsKey("brave")); // nothing captured -> not written
        Assert.Equal("1", file.ForApp("steam")!.Values["steam.overlay.enabled"]);
        Assert.Null(file.ForApp("brave"));
    }

    [Fact]
    public void Load_rejects_a_non_bundle_file()
    {
        Assert.Throws<FormatException>(() => ConfigBundleFile.Load("""{"kind":"something-else","schemaVersion":1}"""));
        Assert.Throws<FormatException>(() => ConfigBundleFile.Load("not json at all"));
    }

    [Fact]
    public void Load_rejects_a_newer_schema()
    {
        var future = "{\"kind\":\"" + ConfigBundleFile.FileKind + "\",\"schemaVersion\":99,\"createdUtc\":\"x\",\"apps\":{}}";
        Assert.Throws<FormatException>(() => ConfigBundleFile.Load(future));
    }

    [Fact]
    public void Bundle_contains_no_obvious_secret_shaped_keys()
    {
        // A captured bundle's keys are allowlist ids; confirm the file has no credential-shaped content.
        var json = ConfigBundleFile.Save([Bundle("spotify", "Spotify", ("spotify.language", "en"))], "2026-10-18T00:00:00Z");
        foreach (var word in new[] { "autologin", "password", "token", "credential", "blob" })
        {
            Assert.DoesNotContain(word, json, StringComparison.OrdinalIgnoreCase);
        }
    }
}
