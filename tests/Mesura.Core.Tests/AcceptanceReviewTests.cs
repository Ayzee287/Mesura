using Mesura.Core.Apps;
using Mesura.Core.Config;
using Mesura.Core.Setup;
using Mesura.Core.Windows;

namespace Mesura.Core.Tests;

/// <summary>
/// The Core side of the fixes from the 2026-10-23 acceptance review: secrets never carried in a settings block, an
/// import that reports only what changes, saved profiles that carry choices rather than Windows defaults, the catalog
/// schema that keeps old copies and old builds apart, and the record of what an undo left alone.
/// </summary>
public sealed class AcceptanceReviewTests
{
    [Theory]
    [InlineData("kagiSession", true)]
    [InlineData("deeplApiKey", true)]
    [InlineData("apiKey", true)]
    [InlineData("api_key", true)]
    [InlineData("APIKey", true)]
    [InlineData("token", true)]
    [InlineData("authToken", true)]
    [InlineData("password", true)]
    [InlineData("webhookUrl", true)]
    [InlineData("sessionId", true)]
    [InlineData("enabled", false)]
    [InlineData("service", false)]
    [InlineData("username", false)]
    [InlineData("hotkey", false)]
    [InlineData("keybind", false)]
    [InlineData("showAuthor", false)]
    [InlineData("toggleKey", false)]
    [InlineData("hotKey", false)]
    [InlineData("key", false)]
    [InlineData("privateKey", true)]
    [InlineData("accessKey", true)]
    public void A_property_is_secret_when_a_word_of_its_name_says_so(string name, bool secret) =>
        Assert.Equal(secret, SensitiveValues.IsSecret(name));

    [Fact]
    public void A_plugin_named_like_a_secret_is_carried_and_only_its_secrets_dropped()
    {
        // The block's own top-level names are plugin names (two such plugins are enabled on PC-000), never settings.
        var stripped = SensitiveValues.Strip("""{"BetterSessions":{"enabled":true,"sessionToken":"x"},"SecretRingToneEnabler":{"enabled":true,"toggleKey":"F9"}}""");
        Assert.Equal("""{"BetterSessions":{"enabled":true},"SecretRingToneEnabler":{"enabled":true,"toggleKey":"F9"}}""", stripped);
    }

    [Fact]
    public void Importing_a_block_back_where_it_came_from_changes_nothing()
    {
        // The destination's secret must neither be lost nor make the block look changed (it would move when merged).
        var doc = new JsonConfigDocument("""{"plugins":{"LastFMRichPresence":{"enabled":true,"apiKey":"SECRET","username":"me"}}}""");
        var bundle = ConfigEngine.Export(Vencord, doc);
        var before = doc.ToText();

        var result = ConfigEngine.Import(Vencord, doc, bundle, _ => true);

        Assert.Empty(result.Applied);
        Assert.Equal(before, doc.ToText());
    }

    private static readonly ConfigPortDefinition Vencord = ConfigCatalog.ById("vencord")!;

    [Fact]
    public void Vencord_plugin_secrets_are_never_exported_and_a_destination_keeps_its_own()
    {
        var source = new JsonConfigDocument("""
            {
              "plugins": {
                "Translate": { "enabled": true, "service": "kagi", "kagiSession": "SOURCE-KAGI", "deeplApiKey": "SOURCE-DEEPL" },
                "MusicRichPresence": { "enabled": true, "apiKey": "SOURCE-LASTFM", "username": "me" }
              }
            }
            """);
        var bundle = ConfigEngine.Export(Vencord, source);
        var plugins = bundle.Values["vencord.plugins"];
        Assert.DoesNotContain("SOURCE-", plugins, StringComparison.Ordinal);
        Assert.Contains("\"service\":\"kagi\"", plugins, StringComparison.Ordinal);
        Assert.Contains("\"username\":\"me\"", plugins, StringComparison.Ordinal);

        // A bundle saved before this fix may still hold secrets: they are dropped on import too, and the destination's
        // own secrets stay where they were.
        var old = bundle with { Values = new Dictionary<string, string> { ["vencord.plugins"] = """{"Translate":{"enabled":true,"kagiSession":"OLD-BUNDLE"}}""" } };
        var destination = new JsonConfigDocument("""{"plugins":{"Translate":{"enabled":false,"kagiSession":"DEST-KAGI"},"Other":{"token":"DEST-OTHER"}}}""");
        var result = ConfigEngine.Import(Vencord, destination, old, _ => true);
        Assert.Equal(["vencord.plugins"], result.Applied);
        var written = destination.ToText();
        Assert.DoesNotContain("OLD-BUNDLE", written, StringComparison.Ordinal);
        Assert.Contains("DEST-KAGI", written, StringComparison.Ordinal);
        Assert.Contains("DEST-OTHER", written, StringComparison.Ordinal); // a plugin the bundle doesn't name keeps its secret
        Assert.Contains("\"enabled\":true", written.Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void An_import_reports_only_the_settings_that_change()
    {
        var brave = ConfigCatalog.ById("brave")!;
        var doc = new JsonConfigDocument("""{"brave":{"brave_ads":{"sponsored":{"enabled":false}},"new_tab_page":{"show_rewards":true}}}""");
        var defaults = ConfigDefaults.Bundle("2026-10-23T00:00:00Z").ForApp("brave")!;

        var result = ConfigEngine.Import(brave, doc, defaults, _ => true);

        Assert.DoesNotContain("brave.ads.sponsored", result.Applied); // already off: nothing to write, nothing to claim
        Assert.Contains("brave.ntp.showRewards", result.Applied);
        Assert.Empty(ConfigEngine.Import(brave, doc, defaults, _ => true).Applied); // a second import changes nothing
    }

    [Fact]
    public void A_saved_profile_leaves_out_what_is_only_windows_as_it_ships()
    {
        var catalog = WindowsCatalog.Default;
        OperationEvaluation Applied(string id, string current)
        {
            var op = catalog.ById(id)!;
            return new OperationEvaluation(op, OperationState.Applied, "", [.. op.Registry.Select(r => new ItemState(r.Target, current, r.Value, true, true))]);
        }

        var windows = new[]
        {
            Applied("repair.threaded-dpc", WindowsEvaluator.NotSet),           // puts a Windows default back
            Applied("repair.prefetcher", "3"),                                  // the default, read as a value
            Applied("gaming.background-recording-off", WindowsEvaluator.NotSet), // absent, which counts as its default
            Applied("gaming.background-recording-off", "0"),                    // the same entry written on purpose
        };

        Assert.True(SetupProfile.AtWindowsDefault(windows[0]));
        Assert.True(SetupProfile.AtWindowsDefault(windows[1]));
        Assert.True(SetupProfile.AtWindowsDefault(windows[2]));
        Assert.False(SetupProfile.AtWindowsDefault(windows[3]));
        Assert.Empty(SetupProfile.FromMachine("pc", "", [], windows[..3], null).Windows);
        Assert.Equal(["gaming.background-recording-off"], SetupProfile.FromMachine("pc", "", [], [windows[3]], null).Windows);
    }

    [Fact]
    public void The_audited_catalogs_carry_their_schema_so_old_builds_refuse_them_and_old_copies_are_told_apart()
    {
        // Windows catalog schema 3 since the 2026-10-24 quality pass (TCP settings, network impact, benefit); 4 since 0.2.0
        // (binary values, per-adapter settings, tool-backed settings, a stated level, sources).
        Assert.Equal(4, WindowsCatalog.CurrentSchema);
        Assert.Equal(2, AppCatalog.CurrentSchema);
        Assert.Contains("\"schemaVersion\": 4", WindowsCatalog.BuiltInJson(), StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\": 2", AppCatalog.BuiltInJson(), StringComparison.Ordinal);
        Assert.True(WindowsCatalog.Default.Operations.Count >= 99);

        // A schema 1 copy still parses here (CatalogFiles decides to set it aside before that).
        Assert.Empty(WindowsCatalog.Parse("""{"kind":"Mesura.WindowsCatalog","schemaVersion":1,"operations":[],"presets":[]}""").Operations);
    }

    [Fact]
    public void Hardware_scheduling_isnt_tagged_as_a_performance_gain()
    {
        // Its own text says it isn't an FPS setting; inside "Maximum performance" a Performance tag would read as one.
        Assert.Equal(OperationImpact.System, WindowsCatalog.Default.ById("gpu.hardware-scheduling-on")!.Impact);
        Assert.DoesNotContain("Modern Standby PCs the plan and PCI Express", WindowsCatalog.Default.PresetById("maximum-performance")!.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void An_undo_that_left_a_changed_file_alone_reads_back_and_counts_as_done()
    {
        var left = new ConfigImportOutcome("brave", "Brave", ConfigImportStatus.ChangedSince, [], [], [], [], "changed since");
        var run = SetupRun.Create("20261023T120000Z-config-restore", "config-restore", null, "a", "b", [], [left]);

        var back = SetupRun.FromJson(run.ToJson());
        Assert.Equal(ConfigImportStatus.ChangedSince, back.Config.Single().Status);
        Assert.True(back.Ok);
        Assert.True(back.ChangedNothing);
        Assert.Equal("left as is", StepWords.Config(left));
        Assert.Equal(RunStatus.Succeeded, RunFollowUp.Status(back));
    }

    [Fact]
    public void A_record_written_before_fingerprints_still_reads()
    {
        const string Old = """
            {"kind":"Mesura.SetupRun","schemaVersion":1,"id":"20261020T100000Z-config-import","operation":"config-import","startedUtc":"a","finishedUtc":"b",
             "apps":[],"config":[{"portId":"brave","appName":"Brave","status":"Applied","applied":["brave.ads.sponsored"],"waitingOnScope":[],
             "destinationMissing":[],"written":[{"sourceId":"main","backupName":"brave-main"}],"detail":""}]}
            """;
        var written = SetupRun.FromJson(Old).Config.Single().Written.Single();
        Assert.Equal("brave-main", written.BackupName);
        Assert.Null(written.Sha256);
    }

    [Fact]
    public void An_empty_settings_file_is_a_bad_file_not_a_crash() =>
        Assert.Throws<FormatException>(() => ConfigBundleFile.Load("  \n"));

    [Fact]
    public void Winget_saying_there_is_no_newer_version_means_already_installed() =>
        Assert.Equal(InstallStatus.AlreadyInstalled, WingetOutput.DescribeInstall(unchecked((int)0x8A15002B)).Status);
}
