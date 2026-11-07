using Mesura.Core.Apps;
using Mesura.Core.Setup;
using Mesura.Core.Config;
using Mesura.Setup.Windows;
using Mesura.Setup.Windows.Config;

namespace Mesura.Setup.Tests;

/// <summary>
/// 0.7: an app with two config files (Steam: per-install and per-account) is imported all or nothing. A failure on the second file used to leave the first one replaced and the app half set up.
/// The failure here is real: the second file is read-only, so Windows refuses the move that replaces it.
/// </summary>
public sealed class ConfigAllOrNothingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mesura-config-aon-").FullName;
    private readonly string _install;
    private readonly string _account;
    private const string InstallText = "\"InstallConfigStore\"\n{\n\t\"Software\"\n\t{\n\t\t\"Valve\"\n\t\t{\n\t\t\t\"Steam\"\n\t\t\t{\n\t\t\t\t\"AutoUpdateWindowEnabled\"\t\t\"0\"\n\t\t\t}\n\t\t}\n\t}\n}\n";
    private const string AccountText = "\"UserLocalConfigStore\"\n{\n\t\"system\"\n\t{\n\t\t\"EnableGameOverlay\"\t\t\"1\"\n\t}\n}\n";

    public ConfigAllOrNothingTests()
    {
        _install = Path.Combine(_dir, "config.vdf");
        _account = Path.Combine(_dir, "localconfig.vdf");
        File.WriteAllText(_install, InstallText);
        File.WriteAllText(_account, AccountText);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal); // a backup copied from a read-only file is read-only too
        }

        Directory.Delete(_dir, recursive: true);
    }

    private IReadOnlyList<LocatedConfigFile> Locate(ConfigPortDefinition port) =>
        [new LocatedConfigFile("install", Format.Vdf, _install, File.Exists(_install)), new LocatedConfigFile("account", Format.Vdf, _account, File.Exists(_account))];

    private ConfigImportOutcome Restore(SetupStore store, ConfigImportOutcome import, string runId = "20261106T000100Z-restore") =>
        ConfigService.RestoreBackups(ConfigCatalog.ById("steam")!, "20261106T000000Z-test", import.Written, store, runId, Locate, _ => false);

    private IReadOnlyList<ConfigImportOutcome> Import(SetupStore store)
    {
        var bundle = ConfigBundleFile.Load(ConfigBundleFile.Save(
            [new AppConfigBundle("steam", "Steam", new Dictionary<string, string> { ["steam.autoUpdateWindow"] = "1", ["steam.overlay.enabled"] = "0" }, [])],
            "2026-11-06T00:00:00Z"));
        IReadOnlyList<LocatedConfigFile> Locate(ConfigPortDefinition port) =>
            [new LocatedConfigFile("install", Format.Vdf, _install, true), new LocatedConfigFile("account", Format.Vdf, _account, true)];
        return ConfigService.Import(bundle, [ConfigCatalog.ById("steam")!], store, "20261106T000000Z-test", Locate, _ => false);
    }

    [Fact]
    public void Both_files_are_written_when_nothing_goes_wrong()
    {
        var outcome = Import(new SetupStore(Path.Combine(_dir, "store"))).Single();

        Assert.Equal(ConfigImportStatus.Applied, outcome.Status);
        Assert.Equal(2, outcome.Written.Count);
        Assert.Contains("\"1\"", File.ReadAllText(_install));
        Assert.Contains("\"0\"", File.ReadAllText(_account));
    }

    [Fact]
    public void An_undo_that_fails_on_the_second_file_leaves_the_first_as_it_was_before_the_undo()
    {
        var store = new SetupStore(Path.Combine(_dir, "store"));
        var import = Import(store).Single();
        var afterImport = (File.ReadAllText(_install), File.ReadAllText(_account));
        File.SetAttributes(_account, FileAttributes.ReadOnly);

        var undo = Restore(store, import);

        Assert.Equal(ConfigImportStatus.Failed, undo.Status);
        Assert.Equal(afterImport, (File.ReadAllText(_install), File.ReadAllText(_account))); // nothing half undone
        Assert.Empty(undo.Written);
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.mesura-tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void An_undo_brings_both_files_back_byte_for_byte_and_a_second_undo_changes_nothing()
    {
        var store = new SetupStore(Path.Combine(_dir, "store"));
        var import = Import(store).Single();

        var undo = Restore(store, import);
        var again = Restore(store, import, "20261106T000200Z-restore");

        Assert.Equal(ConfigImportStatus.Applied, undo.Status);
        Assert.Equal((InstallText, AccountText), (File.ReadAllText(_install), File.ReadAllText(_account)));
        Assert.Equal(ConfigImportStatus.Unchanged, again.Status);
    }

    [Fact]
    public void A_damaged_backup_is_never_put_over_a_working_file()
    {
        var store = new SetupStore(Path.Combine(_dir, "store"));
        var import = Import(store).Single();
        var afterImport = (File.ReadAllText(_install), File.ReadAllText(_account));
        var backup = Path.Combine(store.HistoryFolder, "20261106T000000Z-test", "backups", import.Written.First(w => w.SourceId == "account").BackupName);
        File.SetAttributes(backup, FileAttributes.Normal);
        File.WriteAllText(backup, "\"UserLocalConfigStore\"\n{\n\t\"system\"\n\t{\n"); // cut short in the middle of a block

        var undo = Restore(store, import);

        Assert.Equal(ConfigImportStatus.Failed, undo.Status);
        Assert.Contains("damaged", undo.Detail, StringComparison.Ordinal);
        Assert.Equal(afterImport, (File.ReadAllText(_install), File.ReadAllText(_account))); // not even the good backup of the other file was used
    }

    [Fact]
    public void A_missing_backup_or_a_missing_destination_stops_the_undo_with_nothing_written()
    {
        var store = new SetupStore(Path.Combine(_dir, "store"));
        var import = Import(store).Single();
        var afterImport = (File.ReadAllText(_install), File.ReadAllText(_account));
        File.Delete(_account);

        var gone = Restore(store, import);

        Assert.Equal(ConfigImportStatus.Failed, gone.Status);
        Assert.Equal(afterImport.Item1, File.ReadAllText(_install));
        Assert.False(File.Exists(_account)); // an undo does not create the app's file
    }

    [Fact]
    public void A_failure_on_the_second_file_puts_the_first_back_and_leaves_no_temporary_file()
    {
        File.SetAttributes(_account, FileAttributes.ReadOnly);

        var outcome = Import(new SetupStore(Path.Combine(_dir, "store"))).Single();

        Assert.Equal(ConfigImportStatus.Failed, outcome.Status);
        Assert.Equal(InstallText, File.ReadAllText(_install)); // the first file is exactly as it was
        Assert.Equal(AccountText, File.ReadAllText(_account));
        Assert.Empty(outcome.Written); // nothing stays written, so the history has nothing half done to undo
        Assert.Contains("put back as it was", outcome.Detail, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.mesura-tmp", SearchOption.AllDirectories));
    }
}
