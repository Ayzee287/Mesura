using System.IO;
using System.Threading.Tasks;
using Mesura.Core.Setup;
using Microsoft.Win32;

namespace Mesura.App;

/// <summary>
/// Backups (0.7): the one place for what Mesura saves and brings back. It holds the two screens that used to be separate pages, "PC setup"
/// (apps, their settings, Windows settings, the NVIDIA choice: <see cref="ProfilesViewModel"/>) and "App settings" (<see cref="ConfigurationsViewModel"/>),
/// and one door in front of them: <see cref="OpenBackup"/> reads a file's own kind and sends it to the tab that knows what to do with it.
/// It changes nothing itself; the tabs keep their own previews, confirmations and undo, so a PC setup (which installs and changes Windows) is
/// never restored through the app-settings path or the other way round.
/// </summary>
public sealed class BackupsViewModel : Observable, IPageState
{
    private readonly Services _services;
    private bool _setupTab = true;

    public BackupsViewModel(Services services, ProfilesViewModel setup, ConfigurationsViewModel appSettings)
    {
        _services = services;
        Setup = setup;
        AppSettings = appSettings;
        OpenBackup = new AsyncCommand(OpenBackupAsync, null, Fail);
        NewSetup = new RelayCommand(() =>
        {
            ShowSetup = true;
            Setup.Save.Execute(null);
        });
        NewAppSettings = new RelayCommand(() =>
        {
            ShowSetup = false;
            AppSettings.SaveMine.Execute(null);
        });
    }

    /// <summary>The "PC setup" tab.</summary>
    public ProfilesViewModel Setup { get; }

    /// <summary>The "App settings" tab.</summary>
    public ConfigurationsViewModel AppSettings { get; }

    /// <summary>Open a file of either kind: it lands on the tab it belongs to.</summary>
    public AsyncCommand OpenBackup { get; }

    /// <summary>New backup of this PC's setup: switches to its tab and starts its save flow (it shows what will be saved before anything is written).</summary>
    public RelayCommand NewSetup { get; }

    /// <summary>New backup of the apps' settings only: switches to its tab and starts its export.</summary>
    public RelayCommand NewAppSettings { get; }

    /// <summary>Says a file isn't a backup; the tabs show their own results.</summary>
    public Banner Result { get; } = new();

    /// <summary>The PC setup tab is the open one (the other is App settings). Two-way with the tab buttons.</summary>
    public bool ShowSetup
    {
        get => _setupTab;
        set
        {
            if (_setupTab != value)
            {
                _setupTab = value;
                Raise();
                Raise(nameof(ShowAppSettings));
                Result.Hide();
            }
        }
    }

    public bool ShowAppSettings
    {
        get => !_setupTab;
        set => ShowSetup = !value;
    }

    // The tabs are two pages of one: both are told when the section opens and closes, so the app-settings checks run and finished results settle.
    public void Enter() => AppSettings.Enter();

    public void Leave()
    {
        Setup.Leave();
        AppSettings.Leave();
        Result.Settle();
    }

    private async Task OpenBackupAsync()
    {
        var dialog = new OpenFileDialog { Title = Loc.Get("Backups.OpenDialogTitle"), Filter = Loc.Get("Backups.OpenFilter") };
        if (dialog.ShowDialog() == true)
        {
            await OpenPathAsync(dialog.FileName);
        }
    }

    /// <summary>Reads the file's kind and hands it to the tab it belongs to; a file that is neither is named as such.</summary>
    internal async Task OpenPathAsync(string path)
    {
        Result.Hide();
        BackupKind kind;
        try
        {
            kind = BackupFile.Detect(await File.ReadAllTextAsync(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Result.Show(Tone.Danger, Loc.Format("Backups.Unreadable", Path.GetFileName(path)), technical: e.Message);
            return;
        }

        switch (kind)
        {
            case BackupKind.PcSetup:
                ShowSetup = true;
                if (Setup.CanOpen)
                {
                    await Setup.OpenPathAsync(path);
                }

                break;
            case BackupKind.AppSettings:
                ShowSetup = false;
                if (AppSettings.ChooseSaved.CanExecute(null))
                {
                    AppSettings.UseSavedFile(path);
                }

                break;
            default:
                Result.Show(Tone.Warn, Loc.Format("Backups.NotABackup", Path.GetFileName(path)), Loc.Get("Backups.NotABackupHint"));
                break;
        }
    }

    private void Fail(Exception e)
    {
        _services.Log($"Backups: {e}");
        Result.Show(Tone.Danger, Loc.Get("Common.Unexpected"), technical: e.Message);
    }
}
