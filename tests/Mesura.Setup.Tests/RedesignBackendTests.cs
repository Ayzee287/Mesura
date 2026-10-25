using Mesura.Core.Apps;
using Mesura.Core.Config;
using Mesura.Core.Setup;
using Mesura.Core.Windows;
using Mesura.Setup.Windows;
using Mesura.Setup.Windows.Apps;
using Mesura.Setup.Windows.Config;

namespace Mesura.Setup.Tests;

/// <summary>
/// The redesign's setup pieces against temporary files only: Vencord's detector on a fake Discord folder, undoing a
/// config import from its backups, Mesura's own preferences file, the command line winget is started with, and a saved
/// profile that records the chosen outcome.
/// </summary>
public sealed class RedesignBackendTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mesura-redesign-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Resources(string version)
    {
        var folder = Path.Combine(_dir, "Discord", $"app-{version}", "resources");
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public void Vencord_reads_installed_only_when_the_newest_discord_carries_its_loader()
    {
        Assert.Equal(InstallState.NotInstalled, VencordDetector.Detect(_dir)); // no Discord at all

        var old = Resources("1.0.9100");
        File.WriteAllText(Path.Combine(old, "_app.asar"), "original");
        File.WriteAllText(Path.Combine(old, "app.asar"), """require("C:\\Users\\u\\AppData\\Roaming\\Vencord\\dist\\patcher.js")""");
        Assert.Equal(InstallState.Installed, VencordDetector.Detect(_dir));

        // A Discord update adds a newer version folder without the patch: Vencord needs patching again.
        var updated = Resources("1.0.9265");
        File.WriteAllText(Path.Combine(updated, "app.asar"), "discord's own archive");
        Assert.Equal(InstallState.NotInstalled, VencordDetector.Detect(_dir));

        // Version folders are compared as versions, not as text (9265 is newer than 10000 as text, not as a version).
        var newest = Resources("1.0.10000");
        File.WriteAllText(Path.Combine(newest, "_app.asar"), "original");
        Directory.CreateDirectory(Path.Combine(newest, "app.asar"));
        File.WriteAllText(Path.Combine(newest, "app.asar", "index.js"), "require('../../Vencord/patcher.js')");
        Assert.Equal(InstallState.Installed, VencordDetector.Detect(_dir));
    }

    [Fact]
    public void A_moved_original_without_vencords_loader_is_not_vencord()
    {
        var folder = Resources("1.0.9265");
        File.WriteAllText(Path.Combine(folder, "_app.asar"), "original");
        File.WriteAllText(Path.Combine(folder, "app.asar"), "require('./another-mod/inject.js')");

        Assert.Equal(InstallState.NotInstalled, VencordDetector.Detect(_dir));
    }

    private readonly ConfigPortDefinition _brave = ConfigCatalog.ById("brave")!;

    private string Prefs => Path.Combine(_dir, "Preferences");

    private IReadOnlyList<LocatedConfigFile> Locate(ConfigPortDefinition port) =>
        [new LocatedConfigFile("main", Format.Json, Prefs, File.Exists(Prefs))];

    [Fact]
    public void Undoing_an_import_restores_the_backup_once_and_can_itself_be_undone()
    {
        const string Original = """{"brave":{"brave_ads":{"sponsored":{"enabled":true}}},"profile":{"name":"keep"}}""";
        File.WriteAllText(Prefs, Original);
        var store = new SetupStore(Path.Combine(_dir, "store"));
        var import = ConfigService.Import(ConfigDefaults.Bundle("2026-10-23T00:00:00Z"), [_brave], store, "20261023T000000Z-config-import", Locate, _ => false).Single();
        Assert.Equal(ConfigImportStatus.Applied, import.Status);
        Assert.NotEqual(Original, File.ReadAllText(Prefs));

        var undo = ConfigService.RestoreBackups(_brave, "20261023T000000Z-config-import", import.Written, store, "20261023T000100Z-config-restore", Locate, _ => false);
        Assert.Equal(ConfigImportStatus.Applied, undo.Status);
        Assert.Equal(Original, File.ReadAllText(Prefs));

        // Restoring again finds the file already as in the backup and writes nothing.
        var again = ConfigService.RestoreBackups(_brave, "20261023T000000Z-config-import", import.Written, store, "20261023T000200Z-config-restore", Locate, _ => false);
        Assert.Equal(ConfigImportStatus.Unchanged, again.Status);
        Assert.Empty(again.Written);

        // The restore backed up the imported file, so it can be undone the same way.
        Assert.NotNull(store.ReadBackup("20261023T000100Z-config-restore", undo.Written.Single().BackupName));
    }

    [Fact]
    public void Undo_puts_the_file_back_byte_for_byte()
    {
        // A BOM and CRLF line endings, which an import (UTF-8 without BOM, the document's own layout) doesn't keep.
        byte[] original = [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes("{\r\n  \"brave\": {\"brave_ads\": {\"sponsored\": {\"enabled\": true}}}\r\n}\r\n")];
        File.WriteAllBytes(Prefs, original);
        var store = new SetupStore(Path.Combine(_dir, "store"));
        var import = ConfigService.Import(ConfigDefaults.Bundle("2026-10-23T00:00:00Z"), [_brave], store, "20261023T000000Z-config-import", Locate, _ => false).Single();
        Assert.Equal(ConfigImportStatus.Applied, import.Status);

        ConfigService.RestoreBackups(_brave, "20261023T000000Z-config-import", import.Written, store, "20261023T000100Z-config-restore", Locate, _ => false);

        Assert.Equal(original, File.ReadAllBytes(Prefs));
    }

    [Fact]
    public void Undo_leaves_a_running_app_alone_and_refuses_a_missing_backup()
    {
        File.WriteAllText(Prefs, "{}");
        var store = new SetupStore(Path.Combine(_dir, "store"));
        WrittenConfigFile[] written = [new("main", "brave-main.json")];

        var running = ConfigService.RestoreBackups(_brave, "20261023T000000Z-config-import", written, store, "r1", Locate, _ => true);
        Assert.Equal(ConfigImportStatus.SkippedAppRunning, running.Status);

        var missing = ConfigService.RestoreBackups(_brave, "20261023T000000Z-config-import", written, store, "r2", Locate, _ => false);
        Assert.Equal(ConfigImportStatus.Failed, missing.Status);
        Assert.Equal("{}", File.ReadAllText(Prefs));
    }

    [Fact]
    public void App_settings_are_written_whole_and_read_back()
    {
        var store = new SetupStore(Path.Combine(_dir, "store"));
        Assert.Null(store.ReadAppSettings());

        store.WriteAppSettings("""{"language":"ru"}""");
        store.WriteAppSettings("""{"language":"en"}""");

        Assert.Equal("""{"language":"en"}""", store.ReadAppSettings());
        Assert.DoesNotContain(Directory.EnumerateFiles(store.Root), f => f.EndsWith(".mesura-tmp", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(new[] { "--version" }, @"C:\winget.exe --version")]
    [InlineData(new[] { "install", "--id", "A.B" }, @"C:\winget.exe install --id A.B")]
    [InlineData(new[] { "a b", "say \"hi\"", @"C:\my dir\", @"C:\dir\", "" }, @"C:\winget.exe ""a b"" ""say \""hi\"""" ""C:\my dir\\"" C:\dir\ """"")]
    public void The_command_line_quotes_arguments_the_way_they_are_parsed_back(string[] arguments, string expected)
    {
        Assert.Equal(expected, WingetRunner.CommandLine(@"C:\winget.exe", arguments));
    }

    [Fact]
    public void A_failed_start_as_the_signed_in_user_says_why_in_words()
    {
        Assert.Contains("administrator", WingetRunner.LaunchFailure(@"PC\user", 1314), StringComparison.Ordinal);
        Assert.Contains("Secondary Logon", WingetRunner.LaunchFailure(@"PC\user", 1058), StringComparison.Ordinal);
        Assert.Contains("Windows error 5", WingetRunner.LaunchFailure(@"PC\user", 5), StringComparison.Ordinal);
    }

    private static OperationEvaluation Eval(WindowsOperation op, OperationState state) => new(op, state, "", []);

    [Fact]
    public void A_saved_profile_records_the_chosen_outcome_when_it_is_in_place()
    {
        var catalog = WindowsCatalog.Default;
        var maximum = catalog.PresetById("maximum-performance")!;
        var laptop = catalog.Operations.Select(op => Eval(op,
            op.Id is "power.high-performance" or "power.pcie-aspm-off" ? OperationState.Inapplicable
            : maximum.OperationIds.Contains(op.Id) || op.Id == "appearance.dark-mode" ? OperationState.Applied
            : OperationState.NotApplied)).ToList();

        Assert.True(SetupProfile.PresetSatisfied(maximum, laptop));
        var profile = SetupProfile.FromMachine("pc", "", [], laptop, null, [maximum]);
        Assert.Equal(["maximum-performance"], profile.WindowsPresets);
        Assert.Equal(["appearance.dark-mode"], profile.Windows); // the preset's own entries aren't listed again

        // One entry undone since: the outcome isn't in place any more, so the profile lists what is applied instead.
        var partly = laptop.Select(e => e.Operation.Id == "privacy.no-language-list" ? Eval(e.Operation, OperationState.NotApplied) : e).ToList();
        Assert.False(SetupProfile.PresetSatisfied(maximum, partly));

        // Nothing applied at all (everything inapplicable) isn't an outcome either.
        Assert.False(SetupProfile.PresetSatisfied(maximum, catalog.Operations.Select(op => Eval(op, OperationState.Inapplicable))));
    }
}
