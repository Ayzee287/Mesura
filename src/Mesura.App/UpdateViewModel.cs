using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Mesura.Core.Support;
using Mesura.Core.Update;
using Mesura.Updating;

namespace Mesura.App;

public enum UpdatePhase
{
    Idle,
    Checking,
    Available,
    Downloading,
    Ready,
}

/// <summary>
/// Updates for the whole app (0.3, docs/updates.md): a quiet check when Mesura starts, a notice in the sidebar when something is
/// newer, and the steps behind Install: download with progress, proof that the package is the signed one, then either the
/// updater (an installed copy: Mesura closes, the Windows Installer replaces the version, Mesura opens again) or the installer's
/// own wizard (a copy that was unzipped by hand). Nothing is downloaded or installed without a click; a server that can't be
/// reached, a refused manifest, a bad download or a failed install leaves the running version as it is and says so.
/// </summary>
public sealed class UpdateViewModel : Observable
{
    private readonly Services _services;
    private readonly UpdateStore _store;
    private readonly UpdateClient _client = new();
    private readonly SynchronizationContext _ui = SynchronizationContext.Current ?? new SynchronizationContext();
    private UpdatePhase _phase;
    private UpdateCheckResult? _found;
    private string _message = "";
    private double _progress;
    private string _progressText = "";
    private string _noticeKind = "";
    private string _noticeTitle = "";
    private string _noticeText = "";
    private CancellationTokenSource? _cancel;

    public UpdateViewModel(Services services)
    {
        _services = services;
        _store = new UpdateStore(DataFolder.Local(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
        CheckNow = new AsyncCommand(() => CheckAsync(userInitiated: true), () => _phase is UpdatePhase.Idle or UpdatePhase.Available, Fail);
        Install = new AsyncCommand(InstallAsync, () => _phase == UpdatePhase.Available && !_services.Changing, Fail);
        Later = new RelayCommand(RemindLater, () => _phase == UpdatePhase.Available);
        Skip = new RelayCommand(SkipVersion, () => _phase == UpdatePhase.Available);
        CancelDownload = new RelayCommand(CancelCurrentDownload, () => _phase == UpdatePhase.Downloading);
        DismissNotice = new RelayCommand(() => SetNotice("", "", ""));
        ShowDetails = new RelayCommand(OpenDialog, () => HasUpdate);
        services.ChangingChanged += () => Install.RaiseCanExecuteChanged();
        Loc.Instance.LanguageChanged += () => Raise(string.Empty);
    }

    public AsyncCommand CheckNow { get; }

    public AsyncCommand Install { get; }

    public RelayCommand Later { get; }

    public RelayCommand Skip { get; }

    public RelayCommand CancelDownload { get; }

    public RelayCommand DismissNotice { get; }

    public RelayCommand ShowDetails { get; }

    /// <summary>The result of an action on the update dialog or Settings (a failure, a refusal); never a raw message in its text.</summary>
    public Banner Result { get; } = new();

    public UpdatePhase Phase
    {
        get => _phase;
        private set
        {
            Set(ref _phase, value);
            Raise(nameof(HasUpdate));
            Raise(nameof(Downloading));
            Raise(nameof(CanChooseNow));
            Raise(nameof(HasNoticeOrUpdate));
            Raise(nameof(SidebarTitle));
            Raise(nameof(SidebarText));
            RaiseCommands();
        }
    }

    public bool HasUpdate => _phase is UpdatePhase.Available or UpdatePhase.Downloading or UpdatePhase.Ready;

    public bool Downloading => _phase == UpdatePhase.Downloading;

    public bool CanChooseNow => _phase == UpdatePhase.Available;

    public string Version => _found?.Check?.Manifest?.Version ?? "";

    public string Channel => _services.Settings.UpdateChannel;

    public string ChannelName => Loc.Get(Channel == "preview" ? "Update.ChannelPreview" : "Update.ChannelStable");

    /// <summary>"Mesura 0.3.1" or "Mesura 0.3.1 is available".</summary>
    public string Headline => Loc.Format("Update.Headline", Version);

    public string Detail => Loc.Format("Update.Detail", Branding.ShortVersion, ChannelName, SizeText);

    public string SizeText => _found?.Check?.Package is { } p ? Loc.Format("Update.Size", (p.Size / 1_000_000.0).ToString("0", CultureInfo.CurrentCulture)) : "";

    public string Notes => _found?.Check?.Manifest?.NotesFor(Loc.Instance.Language) ?? "";

    /// <summary>Whether this copy is the one the installer registered: it updates itself; any other copy opens the installer.</summary>
    public bool IsInstalled => InstallRegistration.IsInstalledCopy(Services.Executable);

    public string InstallLabel => Loc.Get(IsInstalled ? "Update.InstallRestart" : "Update.OpenInstaller");

    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => Set(ref _progressText, value);
    }

    /// <summary>One line on Settings: when it last checked, or what the last check found.</summary>
    public string Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    public string LastChecked => _services.Settings.LastUpdateCheckUtc is { } at && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when)
        ? Loc.Format("Update.LastChecked", when.ToLocalTime().ToString("g", Loc.Instance.Culture))
        : Loc.Get("Update.NeverChecked");

    // ---- the sidebar notice: an update waiting, or what the last update did -------------------------------------------

    public string NoticeKind
    {
        get => _noticeKind;
        private set => Set(ref _noticeKind, value);
    }

    public string NoticeTitle
    {
        get => _noticeTitle;
        private set => Set(ref _noticeTitle, value);
    }

    public string NoticeText
    {
        get => _noticeText;
        private set => Set(ref _noticeText, value);
    }

    public bool HasNotice => _noticeTitle.Length > 0;

    public bool HasNoticeOrUpdate => HasNotice || HasUpdate;

    public string SidebarTitle => HasUpdate ? Loc.Format("Update.Available", Version) : _noticeTitle;

    public string SidebarText => HasUpdate ? Loc.Get("Update.AvailableHint") : _noticeText;

    private void SetNotice(string kind, string title, string text)
    {
        NoticeKind = kind;
        NoticeTitle = title;
        NoticeText = text;
        Raise(nameof(HasNotice));
        Raise(nameof(HasNoticeOrUpdate));
        Raise(nameof(SidebarTitle));
        Raise(nameof(SidebarText));
    }

    private void CancelCurrentDownload()
    {
        try
        {
            _cancel?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // the download had just finished
        }
    }

    private void RaiseCommands()
    {
        CheckNow.RaiseCanExecuteChanged();
        Install.RaiseCanExecuteChanged();
        Later.RaiseCanExecuteChanged();
        Skip.RaiseCanExecuteChanged();
        CancelDownload.RaiseCanExecuteChanged();
        ShowDetails.RaiseCanExecuteChanged();
    }

    // ---- start-up --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Called once the window is up. Says what the last update did (the updater leaves a result file; Mesura was started with
    /// <c>--updated-from</c> or <c>--update-failed</c>), then, if allowed, asks the server in the background a few seconds later so
    /// the check never delays the window.
    /// </summary>
    public void Begin()
    {
        try
        {
            ReportLastUpdate();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _services.Log("The last update's result couldn't be read: " + e.Message);
        }

        RepointIntegrations();
        if (_services.Settings.CheckForUpdates)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                _ui.Post(_ => CheckNow.Execute(null), null);
            });
        }
    }

    /// <summary>
    /// A copy that was unzipped by hand and then replaced by the installed one leaves its start-up entry and Start menu shortcut
    /// pointing at the old folder. Running from the installed copy, Mesura points the ones that already exist at itself (it never
    /// creates one the user didn't have), so the move to the installer needs no step from the user.
    /// </summary>
    private void RepointIntegrations()
    {
        try
        {
            if (!_services.SameUser || !InstallRegistration.IsInstalledCopy(Services.Executable))
            {
                return;
            }

            if (_services.Startup.PointsElsewhere(Services.Executable))
            {
                _services.Startup.Set(Services.Executable, true);
                _services.Log("The start-up entry pointed at another copy of Mesura: it now starts this installed copy.");
            }

            if (StartMenuEntry.PointsElsewhere(Services.Executable) && StartMenuEntry.Set(Services.Executable, true) is null)
            {
                _services.Log("The Start menu shortcut pointed at another copy of Mesura: it now starts this installed copy.");
            }
        }
        catch (Exception e) when (e is InvalidOperationException or UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            _services.Log("Pointing the start-up entry and shortcut at the installed copy failed: " + e.Message);
        }
    }

    private void ReportLastUpdate()
    {
        var result = UpdateResult.TryRead(_store.Read(UpdateStore.ResultFile));
        if (result is null)
        {
            return;
        }

        var current = AppVersion.TryParse(Branding.ShortVersion);
        if (result.Status == UpdateResult.Succeeded && AppVersion.TryParse(result.To) is { } to && current is not null && current >= to)
        {
            SetNotice(result.Reason == "restart-needed" ? Tone.Warn : Tone.Ok, Loc.Format("Update.Done", Branding.ShortVersion), Loc.Get(result.Reason == "restart-needed" ? "Update.DoneRestart" : "Update.DoneText"));
            _services.Log($"Updated from {result.From} to {Branding.ShortVersion}.");
        }
        else if (result.Status == UpdateResult.Failed)
        {
            SetNotice(Tone.Warn, Loc.Get("Update.FailedTitle"), Loc.Format("Update.FailedText", Branding.ShortVersion, Reason(result.Reason)));
            _services.Log($"The update from {result.From} to {result.To} failed ({result.Reason}, installer code {result.ExitCode}); the installer's log: {result.Log}");
        }
        else
        {
            return; // a success that isn't this version (the app wasn't restarted into it): nothing to say
        }

        _store.Delete(UpdateStore.ResultFile); // said once
        _store.CleanExcept();
    }

    private static string Reason(string code) => code switch
    {
        "signature" or "package-hash" or "manifest-mismatch" => Loc.Get("Update.ReasonUntrusted"),
        "app-did-not-close" => Loc.Get("Update.ReasonStillOpen"),
        "cancelled" => Loc.Get("Update.ReasonCancelled"),
        "installer-busy" => Loc.Get("Update.ReasonBusy"),
        "needs-administrator" or "elevation-declined" => Loc.Get("Update.ReasonAdmin"),
        "installer-failed-rolled-back" => Loc.Get("Update.ReasonRolledBack"),
        "staged-files-unreadable" => Loc.Get("Update.ReasonFiles"),
        _ => Loc.Get("Update.ReasonOther"),
    };

    // ---- checking ----------------------------------------------------------------------------------------------------------

    private async Task CheckAsync(bool userInitiated)
    {
        if (_phase is UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Ready)
        {
            return;
        }

        var wasAvailable = _phase == UpdatePhase.Available;
        Phase = UpdatePhase.Checking;
        Result.Hide();
        var settings = _services.Settings;
        var state = new UpdateState(
            userInitiated ? null : settings.SkippedUpdate,
            !userInitiated && DateTimeOffset.TryParse(settings.UpdateRemindAfterUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var remind) ? remind : null,
            settings.UpdateHighestSequence);
        var result = await _client.CheckAsync(new UpdateRequest(Branding.ShortVersion, settings.UpdateChannel, state, UpdateKeys.Trusted, Language: InstallRegistration.Read()?.Language));

        if (!result.Reached || result.Check is null)
        {
            Phase = wasAvailable ? UpdatePhase.Available : UpdatePhase.Idle;
            _services.Log($"Update check ({settings.UpdateChannel}): {result.Failure} {result.Detail}");
            if (userInitiated)
            {
                Message = FailureText(result.Failure);
                Result.Show(result.Failure == UpdateFailure.NotFound ? Tone.Muted : Tone.Warn, FailureText(result.Failure), Loc.Get("Update.FailureHint"), technical: result.Detail);
            }

            return;
        }

        settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        if (result.Check.Manifest is { } manifest && manifest.Sequence > settings.UpdateHighestSequence)
        {
            settings.UpdateHighestSequence = manifest.Sequence; // judged and signed: an older manifest can no longer replace it
        }

        _services.SaveSettings();
        Raise(nameof(LastChecked));
        switch (result.Check.Outcome)
        {
            case UpdateOutcome.Available:
                _found = result;
                Message = Loc.Format("Update.Available", result.Check.Manifest!.Version);
                _services.Log($"Update check ({settings.UpdateChannel}): {result.Check.Manifest.Version} is available.");
                RaiseDetails();
                Phase = UpdatePhase.Available;
                break;
            case UpdateOutcome.Skipped or UpdateOutcome.Postponed:
                Message = Loc.Get("Update.UpToDate");
                Phase = UpdatePhase.Idle;
                break;
            default:
                _found = null;
                Message = Loc.Get("Update.UpToDate");
                Phase = UpdatePhase.Idle;
                break;
        }
    }

    private void RaiseDetails()
    {
        Raise(nameof(Version));
        Raise(nameof(Headline));
        Raise(nameof(Detail));
        Raise(nameof(SizeText));
        Raise(nameof(Notes));
        Raise(nameof(IsInstalled));
        Raise(nameof(InstallLabel));
        Raise(nameof(ChannelName));
        Raise(nameof(SidebarTitle));
    }

    private static string FailureText(UpdateFailure failure) => failure switch
    {
        UpdateFailure.Offline => Loc.Get("Update.Offline"),
        UpdateFailure.Timeout => Loc.Get("Update.Timeout"),
        UpdateFailure.ServerError => Loc.Get("Update.ServerError"),
        UpdateFailure.NotFound => Loc.Get("Update.NotPublished"),
        UpdateFailure.Rejected => Loc.Get("Update.Refused"),
        _ => Loc.Get("Update.CheckFailed"),
    };

    // ---- the choices ---------------------------------------------------------------------------------------------------------

    private void RemindLater()
    {
        _services.Settings.UpdateRemindAfterUtc = DateTimeOffset.UtcNow.AddDays(1).ToString("o", CultureInfo.InvariantCulture);
        _services.SaveSettings();
        Dismiss(Loc.Get("Update.Later"));
    }

    private void SkipVersion()
    {
        _services.Settings.SkippedUpdate = Version;
        _services.SaveSettings();
        Dismiss(Loc.Format("Update.Skipped", Version));
    }

    private void Dismiss(string message)
    {
        _found = null;
        Message = message;
        Result.Hide();
        RaiseDetails();
        Phase = UpdatePhase.Idle;
        CloseDialog();
    }

    // ---- installing ------------------------------------------------------------------------------------------------------------

    private async Task InstallAsync()
    {
        if (_found is not { Check: { } check, ManifestBytes: { } manifestBytes, Signature: { } signature } || _phase != UpdatePhase.Available)
        {
            return;
        }

        if (_services.Changing)
        {
            Dialogs.Busy();
            return;
        }

        Result.Hide();
        Progress = 0;
        ProgressText = Loc.Get("Update.Downloading");
        _cancel = new CancellationTokenSource();
        Phase = UpdatePhase.Downloading;
        DownloadResult download;
        try
        {
            var progress = new Progress<(long Done, long Total)>(p =>
            {
                Progress = p.Total == 0 ? 0 : (double)p.Done / p.Total;
                ProgressText = Loc.Format("Update.DownloadingOf", (p.Done / 1_000_000.0).ToString("0.0", CultureInfo.CurrentCulture), (p.Total / 1_000_000.0).ToString("0", CultureInfo.CurrentCulture));
            });
            download = await new UpdateDownloader(_client.Http, _store).DownloadAsync(check, manifestBytes, signature, progress, _cancel.Token);
        }
        finally
        {
            _cancel.Dispose();
            _cancel = null;
        }

        if (!download.Ok)
        {
            Phase = UpdatePhase.Available;
            _services.Log($"Update download: {download.Failure} {download.Detail}");
            if (download.Failure != UpdateFailure.Cancelled)
            {
                Result.Show(Tone.Warn, DownloadText(download.Failure), Loc.Get("Update.DownloadHint"), technical: download.Detail);
            }

            return;
        }

        Phase = UpdatePhase.Ready;
        ProgressText = Loc.Get("Update.Verified");
        await ApplyAsync(download.PackagePath!);
    }

    private static string DownloadText(UpdateFailure failure) => failure switch
    {
        UpdateFailure.Offline => Loc.Get("Update.DownloadOffline"),
        UpdateFailure.DiskFull => Loc.Get("Update.DiskFull"),
        UpdateFailure.AccessDenied => Loc.Get("Update.AccessDenied"),
        UpdateFailure.Corrupt => Loc.Get("Update.Corrupt"),
        UpdateFailure.ServerError or UpdateFailure.NotFound => Loc.Get("Update.DownloadServer"),
        _ => Loc.Get("Update.DownloadFailed"),
    };

    /// <summary>Hands the verified package over: to the updater (an installed copy) or to the installer's wizard (any other copy).</summary>
    private Task ApplyAsync(string package)
    {
        var install = InstallRegistration.Read();
        if (install is null || !InstallRegistration.IsInstalledCopy(Services.Executable, install))
        {
            if (!ConfirmDialog.Ask(Loc.Get("Update.PortableTitle"), Loc.Get("Update.PortableMessage"), confirm: Loc.Get("Update.OpenInstallerNow")))
            {
                Phase = UpdatePhase.Available;
                return Task.CompletedTask;
            }

            var opened = InstallerLauncher.OpenInstaller(package);
            Phase = UpdatePhase.Available;
            if (opened is not null)
            {
                Result.Show(Tone.Warn, Loc.Get("Update.InstallerNotStarted"), technical: opened);
            }

            return Task.CompletedTask;
        }

        if (!ConfirmDialog.Ask(Loc.Get("Update.RestartTitle"), Loc.Format("Update.RestartMessage", Version), confirm: Loc.Get("Update.InstallRestart")))
        {
            Phase = UpdatePhase.Available;
            return Task.CompletedTask;
        }

        if (_services.Changing)
        {
            Phase = UpdatePhase.Available;
            Dialogs.Busy();
            return Task.CompletedTask;
        }

        var elevate = install.Scope == InstallScope.Machine && !_services.Reader.IsElevated;
        var data = DataFolder.Local(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        // A copy started with --no-elevate (no UAC prompt) comes back the same way.
        var noElevate = Environment.GetCommandLineArgs().Any(a => a.Equals(App.NoElevateArgument, StringComparison.OrdinalIgnoreCase));
        string[] arguments =
        [
            "update", "apply",
            "--package", package,
            "--manifest", _store.PathOf(UpdateStore.ManifestFile),
            "--sig", _store.PathOf(UpdateStore.SignatureFile),
            "--pid", Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            "--restart", install.Executable,
            "--from", Branding.ShortVersion,
            "--to", Version,
            "--log", _store.PathOf("update-install.log"),
            "--scope", install.Scope == InstallScope.Machine ? "machine" : "user",
            "--folder", install.Folder,
            "--data", data,
            "--language", install.Language,
            .. noElevate ? ["--no-elevate", "1"] : Array.Empty<string>(),
        ];
        _services.Log($"Starting the updater: {Branding.ShortVersion} to {Version} ({install.Scope}, {install.Folder}).");
        string? error;
        if (install.Scope == InstallScope.Machine)
        {
            // Machine-wide: the installed command line (Program Files) runs elevated, verifies the package and moves it into a folder only administrators can write; the
            // updater that runs the installer is the protected copy it makes (docs/security.md). Nothing from the user's folders is ever started elevated.
            var exposed = Mesura.Core.Support.ProgramFolderTrust.Why(install.Folder);
            string[] stage =
            [
                "update", "stage",
                "--package", package,
                "--manifest", _store.PathOf(UpdateStore.ManifestFile),
                "--sig", _store.PathOf(UpdateStore.SignatureFile),
                "--pid", Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                "--data", data,
                .. noElevate ? ["--no-elevate", "1"] : Array.Empty<string>(),
            ];
            error = exposed is not null ? "install-folder-not-protected: " + exposed : InstallerLauncher.StartStage(install.CommandLine, stage, elevate);
        }
        else
        {
            error = InstallerLauncher.StartUpdater(_store, install.CommandLine, arguments, elevate: false);
        }

        if (error is not null)
        {
            Phase = UpdatePhase.Available;
            _services.Log("The updater didn't start: " + error);
            Result.Show(Tone.Warn, Loc.Get(error == "elevation-declined" ? "Update.ElevationDeclined" : "Update.UpdaterNotStarted"), technical: error);
            return Task.CompletedTask;
        }

        // Mesura closes now, so no file of the installed version is in use when the installer needs it.
        Application.Current.Shutdown();
        return Task.CompletedTask;
    }

    private void Fail(Exception error)
    {
        _services.Log("Update: unexpected: " + error.Message);
        if (_phase is UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Ready)
        {
            Phase = _found is null ? UpdatePhase.Idle : UpdatePhase.Available;
        }

        Result.Show(Tone.Warn, Loc.Get("Update.CheckFailed"), Loc.Get("Update.FailureHint"), technical: error.Message);
    }

    // ---- the dialog ----------------------------------------------------------------------------------------------------------------

    private UpdateDialog? _dialog;

    /// <summary>Opens the update dialog (release notes and the choices), or brings it forward.</summary>
    public void OpenDialog()
    {
        if (_dialog is not null)
        {
            _dialog.Activate();
            return;
        }

        _dialog = new UpdateDialog(this) { Owner = Application.Current.MainWindow };
        _dialog.Closed += (_, _) => _dialog = null;
        _dialog.Show();
    }

    private void CloseDialog() => _dialog?.Close();
}
