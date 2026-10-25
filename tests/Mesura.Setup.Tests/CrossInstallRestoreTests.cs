using System.Text.Json.Nodes;
using Mesura.Core.Config;
using Mesura.Core.Setup;
using Mesura.Setup.Windows;
using Mesura.Setup.Windows.Config;

namespace Mesura.Setup.Tests;

/// <summary>
/// A genuine cross-installation restore, end to end through the bundle file: settings exported from one machine's
/// config files ("A") are imported into a different machine's fresh files ("B"), where every value differs and each
/// file holds keys of its own. The same-machine round trip can't show this; this test does, without touching a real app.
/// </summary>
public sealed class CrossInstallRestoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mesura-cross-install-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string machine, string name, string text)
    {
        var folder = Path.Combine(_dir, machine);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static IConfigDocument Open(Format format, string path) => format switch
    {
        Format.Vdf => new VdfConfigDocument(File.ReadAllText(path)),
        Format.Json => new JsonConfigDocument(File.ReadAllText(path)),
        _ => new PrefsConfigDocument(File.ReadAllText(path)),
    };

    /// <summary>Each port's files on one machine, by source id.</summary>
    private Dictionary<string, Dictionary<string, (Format Format, string Path)>> Machine(string m, bool steamAccount, bool source)
    {
        var v = source ? "1" : "0";
        var files = new Dictionary<string, Dictionary<string, (Format, string)>>
        {
            ["steam"] = new()
            {
                ["install"] = (Format.Vdf, Write(m, "config.vdf",
                    "\"InstallConfigStore\"\n{\n\t\"Software\"\n\t{\n\t\t\"Valve\"\n\t\t{\n\t\t\t\"Steam\"\n\t\t\t{\n" +
                    $"\t\t\t\t\"AutoUpdateWindowEnabled\"\t\t\"{v}\"\n\t\t\t\t\"{(source ? "SourceOnlyKey" : "TargetOnlyKey")}\"\t\t\"x\"\n" +
                    "\t\t\t}\n\t\t}\n\t}\n}\n")),
            },
            ["brave"] = new()
            {
                ["main"] = (Format.Json, Write(m, "Preferences",
                    $"{{\"brave\":{{\"new_tab_page\":{{\"show_clock\":{(source ? "true" : "false")}}}}},\"profile\":{{\"name\":\"{(source ? "Alice" : "Bob")}\"}}}}")),
            },
            ["vencord"] = new()
            {
                ["main"] = (Format.Json, Write(m, "vencord-settings.json",
                    source
                        ? "{\n    \"plugins\": { \"NoTrack\": { \"enabled\": true } },\n    \"useQuickCss\": true,\n    \"cloud\": { \"authenticated\": true }\n}"
                        : "{\n    \"plugins\": {},\n    \"useQuickCss\": false,\n    \"cloud\": { \"authenticated\": false },\n    \"targetOnly\": 1\n}")),
            },
            ["discord"] = new()
            {
                ["main"] = (Format.Json, Write(m, "discord-settings.json",
                    $"{{\n  \"MINIMIZE_TO_TRAY\": {(source ? "true" : "false")},\n  \"WINDOW_BOUNDS\": {{ \"x\": {(source ? 10 : 99)} }}\n}}")),
            },
            ["spotify"] = new()
            {
                ["main"] = (Format.Prefs, Write(m, "prefs",
                    $"language=\"{(source ? "fr" : "en")}\"\nautologin.username=\"{(source ? "alice" : "bob")}\"\n")),
            },
        };

        if (steamAccount)
        {
            files["steam"]["account"] = (Format.Vdf, Write(m, "localconfig.vdf",
                $"\"UserLocalConfigStore\"\n{{\n\t\"system\"\n\t{{\n\t\t\"EnableGameOverlay\"\t\t\"{v}\"\n\t}}\n}}\n"));
        }

        return files;
    }

    private static Func<ConfigPortDefinition, IReadOnlyList<LocatedConfigFile>> Locator(
        Dictionary<string, Dictionary<string, (Format Format, string Path)>> machine) =>
        port => [.. port.Sources.Select(s => machine[port.Id].TryGetValue(s.Id, out var f)
            ? new LocatedConfigFile(s.Id, f.Format, f.Path, File.Exists(f.Path))
            : new LocatedConfigFile(s.Id, s.Format, Path.Combine(Path.GetTempPath(), "absent-" + s.Id), false))];

    private static ConfigBundleFile ExportFrom(Dictionary<string, Dictionary<string, (Format Format, string Path)>> machine)
    {
        var bundles = ConfigCatalog.All.Select(port => ConfigEngine.Export(port,
            sourceId => machine[port.Id].TryGetValue(sourceId, out var f) ? Open(f.Format, f.Path) : null));
        return ConfigBundleFile.Load(ConfigBundleFile.Save(bundles, "2026-10-19T00:00:00Z"));
    }

    [Fact]
    public void Settings_from_one_machine_restore_onto_another_and_keep_the_targets_own_keys()
    {
        var a = Machine("A", steamAccount: true, source: true);
        var b = Machine("B", steamAccount: true, source: false);
        var bundle = ExportFrom(a);
        var carried = string.Join("|", bundle.Apps.SelectMany(x => x.Value.Values.Values));
        Assert.DoesNotContain("Alice", carried, StringComparison.OrdinalIgnoreCase); // profile name, Spotify login: never exported

        var outcomes = ConfigService.Import(bundle, ConfigCatalog.All, new SetupStore(Path.Combine(_dir, "store")),
            "20261019T000000Z-cross", Locator(b), _ => false);

        Assert.All(outcomes, o => Assert.Equal(ConfigImportStatus.Applied, o.Status));

        // Values came across...
        Assert.Contains("\"AutoUpdateWindowEnabled\"\t\t\"1\"", File.ReadAllText(b["steam"]["install"].Path), StringComparison.Ordinal);
        Assert.Contains("\"EnableGameOverlay\"\t\t\"1\"", File.ReadAllText(b["steam"]["account"].Path), StringComparison.Ordinal);
        var brave = JsonNode.Parse(File.ReadAllText(b["brave"]["main"].Path))!;
        Assert.True(brave["brave"]!["new_tab_page"]!["show_clock"]!.GetValue<bool>());
        var vencord = JsonNode.Parse(File.ReadAllText(b["vencord"]["main"].Path))!;
        Assert.True(vencord["plugins"]!["NoTrack"]!["enabled"]!.GetValue<bool>());
        Assert.True(JsonNode.Parse(File.ReadAllText(b["discord"]["main"].Path))!["MINIMIZE_TO_TRAY"]!.GetValue<bool>());
        Assert.Contains("language=\"fr\"", File.ReadAllText(b["spotify"]["main"].Path), StringComparison.Ordinal);

        // ...and nothing of the source machine's own state did, while the target's own keys survived.
        Assert.Contains("TargetOnlyKey", File.ReadAllText(b["steam"]["install"].Path), StringComparison.Ordinal);
        Assert.DoesNotContain("SourceOnlyKey", File.ReadAllText(b["steam"]["install"].Path), StringComparison.Ordinal);
        Assert.Equal("Bob", brave["profile"]!["name"]!.GetValue<string>());
        Assert.False(vencord["cloud"]!["authenticated"]!.GetValue<bool>());
        Assert.Equal(1, vencord["targetOnly"]!.GetValue<int>());
        Assert.Equal(99, JsonNode.Parse(File.ReadAllText(b["discord"]["main"].Path))!["WINDOW_BOUNDS"]!["x"]!.GetValue<int>());
        Assert.Contains("autologin.username=\"bob\"", File.ReadAllText(b["spotify"]["main"].Path), StringComparison.Ordinal);
    }

    [Fact]
    public void On_a_fresh_install_Steam_account_settings_wait_for_the_first_sign_in_and_a_rerun_then_applies_them()
    {
        var a = Machine("A", steamAccount: true, source: true);
        var b = Machine("B", steamAccount: false, source: false); // Steam installed, never signed in
        var bundle = ExportFrom(a);
        var store = new SetupStore(Path.Combine(_dir, "store"));

        var first = ConfigService.Import(bundle, [ConfigCatalog.ById("steam")!], store, "20261019T000000Z-first", Locator(b), _ => false).Single();
        Assert.Contains("steam.overlay.enabled", first.WaitingOnScope.Concat(first.DestinationMissing));

        // The user signs in once: Steam creates the account's localconfig.vdf.
        b["steam"]["account"] = (Format.Vdf, Write("B", "localconfig.vdf", "\"UserLocalConfigStore\"\n{\n\t\"system\"\n\t{\n\t\t\"EnableGameOverlay\"\t\t\"0\"\n\t}\n}\n"));
        var second = ConfigService.Import(bundle, [ConfigCatalog.ById("steam")!], store, "20261019T000001Z-second", Locator(b), _ => false).Single();

        Assert.Equal(ConfigImportStatus.Applied, second.Status);
        Assert.Contains("\"EnableGameOverlay\"\t\t\"1\"", File.ReadAllText(b["steam"]["account"].Path), StringComparison.Ordinal);

        var third = ConfigService.Import(bundle, [ConfigCatalog.ById("steam")!], store, "20261019T000002Z-third", Locator(b), _ => false).Single();
        Assert.Equal(ConfigImportStatus.Unchanged, third.Status); // idempotent once applied
    }
}
