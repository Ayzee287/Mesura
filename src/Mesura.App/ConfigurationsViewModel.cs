using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Mesura.Core.Config;
using Mesura.Core.Setup;
using Mesura.Platform.Windows;
using Mesura.Setup.Windows.Config;
using Microsoft.Win32;

namespace Mesura.App;

/// <summary>Where one source of settings (Mesura's defaults, the saved configuration) stands for one app here.</summary>
public enum ConfigSourceState
{
    /// <summary>Not offered for this app (no Mesura defaults, or the app isn't in the saved settings), or no saved file.</summary>
    None,

    /// <summary>Not read yet.</summary>
    Checking,

    /// <summary>Every value already matches: nothing to apply.</summary>
    InPlace,

    /// <summary>Applied by this page in this session, and it still matches.</summary>
    AppliedNow,

    /// <summary>Some values differ: applying would change them.</summary>
    Pending,

    /// <summary>The app hasn't created the file these values go to yet (start it once).</summary>
    Waiting,

    /// <summary>The app's settings couldn't be read.</summary>
    Unreadable,
}

/// <summary>
/// One supported app on Configurations: its icon, whether its settings are on this PC, where Mesura's defaults and the
/// saved settings stand against them, and the next action that makes sense (never one that would change nothing).
/// </summary>
public sealed class ConfigCard : Observable
{
    private readonly ConfigurationsViewModel _owner;
    private bool _found;
    private string? _undoRun;
    private IReadOnlyList<WrittenConfigFile> _undoFiles = [];
    private ConfigSourceState _defaults = ConfigSourceState.Checking;
    private ConfigSourceState _saved = ConfigSourceState.Checking;
    private IReadOnlyList<string> _defaultChanges = [];
    private IReadOnlyList<string> _savedChanges = [];
    private string _technical = "";
    private bool _expanded;

    public ConfigCard(ConfigurationsViewModel owner, ConfigPortDefinition port)
    {
        _owner = owner;
        Port = port;
        ApplyDefaults = new AsyncCommand(() => owner.ApplyAsync(this, saved: false), () => owner.CanAct && _defaults == ConfigSourceState.Pending, owner.Fail);
        RestoreSaved = new AsyncCommand(() => owner.ApplyAsync(this, saved: true), () => owner.CanAct && _saved == ConfigSourceState.Pending, owner.Fail);
        Undo = new AsyncCommand(() => owner.UndoAsync(this), () => owner.CanAct && _undoRun is not null, owner.Fail);
    }

    public ConfigPortDefinition Port { get; }

    public string AppId => Port.Id;

    public string Name => Loc.Category(Port.AppName);

    /// <summary>What assistive technology reads for this item: its display name, never the type name.</summary>
    public override string ToString() => Name;

    public bool HasDefaults => ConfigDefaults.Has(Port.Id);

    public bool InSaved => _owner.Saved?.Apps.ContainsKey(Port.Id) == true;

    public bool Found => _found;

    public string StateText => _found ? Loc.Get("Config.Found") : Loc.Get("Config.NotFound");

    public string StateTone => _found ? Tone.Neutral : Tone.Muted;

    public ConfigSourceState DefaultsState => _defaults;

    public ConfigSourceState SavedState => _saved;

    /// <summary>Where Mesura's defaults stand, in words.</summary>
    public string DefaultsLine => !HasDefaults ? Loc.Format("Config.Defaults.None", Name) : SourceLine("Defaults", _defaults, _defaultChanges.Count);

    public string DefaultsTone => !HasDefaults ? Tone.Muted : SourceTone(_defaults);

    /// <summary>Where the saved settings stand, in words; empty when there is no saved file at all.</summary>
    public string SavedLine => _owner.Saved is null ? ""
        : !InSaved ? Loc.Format("Config.Saved.NotIn", Name)
        : SourceLine("Saved", _saved, _savedChanges.Count);

    public string SavedTone => !InSaved ? Tone.Muted : SourceTone(_saved);

    public bool HasSavedLine => SavedLine.Length > 0;

    private static string SourceLine(string source, ConfigSourceState state, int changes) => state switch
    {
        ConfigSourceState.Checking => Loc.Get("State.Checking"),
        ConfigSourceState.InPlace => Loc.Get($"Config.{source}.InPlace"),
        ConfigSourceState.AppliedNow => Loc.Get($"Config.{source}.AppliedNow"),
        ConfigSourceState.Pending => Loc.Format($"Config.{source}.Pending", Loc.Plural("Config.Changes", changes)),
        ConfigSourceState.Waiting => Loc.Get("Config.Waiting"),
        ConfigSourceState.Unreadable => Loc.Get("Config.Unreadable"),
        _ => Loc.Get($"Config.{source}.NotHere"),
    };

    private static string SourceTone(ConfigSourceState state) => state switch
    {
        ConfigSourceState.InPlace or ConfigSourceState.AppliedNow => Tone.Ok,
        ConfigSourceState.Pending => Tone.Accent,
        ConfigSourceState.Waiting => Tone.Warn,
        ConfigSourceState.Unreadable => Tone.Danger,
        _ => Tone.Muted,
    };

    /// <summary>What to do when nothing can be applied yet: start the app once (or install it first).</summary>
    public string Hint => !_found ? Loc.Format("Config.NextStartOnce", Name)
        : _defaults == ConfigSourceState.Waiting || _saved == ConfigSourceState.Waiting ? Loc.Format("FollowUp.StartAppOnce", Name)
        : "";

    /// <summary>
    /// The one primary action: restore the saved settings when they differ (the user's own choice comes first), else
    /// apply Mesura's defaults when they differ; nothing when everything is in place.
    /// </summary>
    public bool ShowRestorePrimary => _saved == ConfigSourceState.Pending;

    public bool ShowDefaultsPrimary => !ShowRestorePrimary && _defaults == ConfigSourceState.Pending;

    /// <summary>Mesura's defaults as a secondary action, when the saved settings took the primary place.</summary>
    public bool ShowDefaultsSecondary => ShowRestorePrimary && _defaults == ConfigSourceState.Pending;

    public string RestoreLabel => Loc.Get("Config.RestoreSaved");

    public string DefaultsLabel => Loc.Get("Config.ApplyDefaults");

    public bool HasUndo => _undoRun is not null;

    public string? UndoRun => _undoRun;

    public IReadOnlyList<WrittenConfigFile> UndoFiles => _undoFiles;

    /// <summary>The settings each source would change, by name, for the details.</summary>
    public IReadOnlyList<string> DefaultChanges => _defaultChanges;

    public IReadOnlyList<string> SavedChanges => _savedChanges;

    public bool HasDefaultChanges => _defaults == ConfigSourceState.Pending && _defaultChanges.Count > 0;

    public bool HasSavedChanges => _saved == ConfigSourceState.Pending && _savedChanges.Count > 0;

    public string Included => Loc.Plural("Config.SettingsCount", Port.Settings.Count);

    public string Never => string.Join(", ", Port.Exclusions.Select(Loc.Exclusion));

    /// <summary>Raw reasons (unreadable files), for the details' technical part.</summary>
    public string Technical => _technical;

    public bool Expanded
    {
        get => _expanded;
        set => Set(ref _expanded, value);
    }

    public AsyncCommand ApplyDefaults { get; }

    public AsyncCommand RestoreSaved { get; }

    public AsyncCommand Undo { get; }

    public void Update(bool found, string? undoRun, IReadOnlyList<WrittenConfigFile> undoFiles)
    {
        _found = found;
        _undoRun = undoRun;
        _undoFiles = undoFiles;
        Raise(string.Empty);
        RaiseCommands();
    }

    /// <summary>Where both sources stand, after a preview against the app's files.</summary>
    public void UpdateSources(ConfigSourceState defaults, IReadOnlyList<string> defaultChanges, ConfigSourceState saved, IReadOnlyList<string> savedChanges, string technical)
    {
        _defaults = defaults;
        _defaultChanges = defaultChanges;
        _saved = saved;
        _savedChanges = savedChanges;
        _technical = technical;
        Raise(string.Empty);
        RaiseCommands();
    }

    public void RaiseCommands()
    {
        ApplyDefaults.RaiseCanExecuteChanged();
        RestoreSaved.RaiseCanExecuteChanged();
        Undo.RaiseCanExecuteChanged();
    }

    public void Relocalize() => Raise(string.Empty);
}

/// <summary>
/// Configurations (redesign §5, made action-oriented in the 2026-10-24 quality pass): one card per supported app with
/// its icon, whether its settings are here, where Mesura's defaults and the saved settings stand against them (already
/// in place, applied just now, so many changes, waiting for the app's first start), and only the actions that would
/// change something, plus undo of the last change and the saved configuration itself (export this PC's settings,
/// choose a file). It uses the sanitised import and export unchanged: allowlisted settings only, every replaced file
/// backed up, a running app left alone.
/// </summary>
public sealed class ConfigurationsViewModel : Observable, IPageState
{
    private readonly Services _services;
    private readonly Action<string?> _openActivity;
    private readonly Dictionary<string, string> _appliedNow = new(StringComparer.OrdinalIgnoreCase);
    private bool _busy;
    private bool _checking;
    private ConfigBundleFile? _saved;
    private string _savedError = "";

    public ConfigurationsViewModel(Services services, Action<string?> openActivity)
    {
        _services = services;
        _openActivity = openActivity;
        foreach (var port in ConfigCatalog.All)
        {
            Cards.Add(new ConfigCard(this, port));
        }

        SaveMine = new AsyncCommand(SaveMineAsync, () => CanAct, Fail);
        ChooseSaved = new RelayCommand(DoChooseSaved, () => !_busy);
        Refresh = new AsyncCommand(CheckAsync, () => !_busy && !_checking, Fail);
        LoadSaved();
        Reload();
        _ = CheckAsync();
        services.ChangingChanged += RaiseCommands;
        services.RunRecorded += operation =>
        {
            Reload();
            _ = CheckAsync();
        };
        Loc.Instance.LanguageChanged += () =>
        {
            foreach (var card in Cards)
            {
                card.Relocalize();
            }

            Raise(string.Empty);
        };
    }

    public ObservableCollection<ConfigCard> Cards { get; } = [];

    public Banner Result { get; } = new();

    public AsyncCommand SaveMine { get; }

    public RelayCommand ChooseSaved { get; }

    public AsyncCommand Refresh { get; }

    /// <summary>Raised when the saved configuration changed (Apps offers it as a source).</summary>
    public event Action? SavedChanged;

    public ConfigBundleFile? Saved => _saved;

    /// <summary>Whether actions can run: nothing else is changing the PC, and Mesura runs as the signed-in user.</summary>
    public bool CanAct => !_busy && !_services.Changing && _services.SameUser;

    /// <summary>Why the actions are blocked, when Mesura runs as another account than the signed-in user.</summary>
    public string Blocked => _services.SameUser ? "" : _services.ProfileNote.Length > 0 ? _services.ProfileNote : Loc.Get("Config.UserUnknown");

    public string SavedTitle => _saved is null ? Loc.Get("Config.NoSaved") : Path.GetFileName(_services.Settings.SavedConfiguration ?? "");

    public string SavedDetail
    {
        get
        {
            if (_savedError.Length > 0)
            {
                return _savedError;
            }

            if (_saved is null)
            {
                return Loc.Get("Config.NoSavedHint");
            }

            var when = DateTimeOffset.TryParse(_saved.CreatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
                ? t.ToLocalTime().ToString("d MMM yyyy", Loc.Instance.Culture)
                : "";
            var apps = string.Join(", ", _saved.Apps.Values.Select(a => a.AppName));
            return Loc.Format("Config.SavedDetail", when, apps);
        }
    }

    /// <summary>A one-line summary under the title: how many apps have something to apply.</summary>
    public string Summary
    {
        get
        {
            if (_checking)
            {
                return Loc.Get("Config.Checking");
            }

            var pending = Cards.Count(c => c.ShowRestorePrimary || c.ShowDefaultsPrimary);
            return pending > 0 ? Loc.Plural("Config.SummaryPending", pending)
                : _saved is null ? Loc.Get("Config.SummaryNothingNoSaved")
                : Loc.Get("Config.SummaryNothing");
        }
    }

    public bool Busy
    {
        get => _busy;
        private set
        {
            Set(ref _busy, value);
            RaiseCommands();
        }
    }

    public bool Checking => _checking;

    public void Enter()
    {
        if (!_busy && !_checking)
        {
            Reload();
            _ = CheckAsync(); // an app may have rewritten its file, or been closed, since the page was last shown
        }
    }

    public void Leave() => Result.Settle();

    private void LoadSaved()
    {
        _saved = null;
        _savedError = "";
        if (_services.Settings.SavedConfiguration is { } path)
        {
            try
            {
                _saved = File.Exists(path) ? ConfigBundleFile.Load(File.ReadAllText(path)) : null;
                _savedError = File.Exists(path) ? "" : Loc.Format("Config.SavedMissing", Path.GetFileName(path));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
            {
                // An unreadable saved file is reported here; it never keeps the window from opening.
                _services.Log($"Configurations: the saved settings {path} couldn't be read: {e.Message}");
                _savedError = Loc.Format("Config.SavedUnreadable", Path.GetFileName(path));
            }
        }

        Raise(nameof(SavedTitle));
        Raise(nameof(SavedDetail));
        foreach (var card in Cards)
        {
            card.Relocalize();
            card.RaiseCommands();
        }
    }

    /// <summary>Re-reads which apps have settings here, and the last change of each that can be undone.</summary>
    private void Reload()
    {
        var undo = LatestBackups();
        foreach (var card in Cards)
        {
            var found = ConfigLocator.Locate(card.Port).Any(f => f.Exists);
            var last = undo.TryGetValue(card.Port.Id, out var u) ? u : default;
            card.Update(found, last.RunId, last.Files ?? []);
        }
    }

    /// <summary>
    /// Previews Mesura's defaults and the saved settings against every app's files (read only), so each card says what
    /// is already in place and offers only what would change something.
    /// </summary>
    private async Task CheckAsync()
    {
        if (_checking)
        {
            return;
        }

        _checking = true;
        Raise(nameof(Checking));
        Raise(nameof(Summary));
        Refresh.RaiseCanExecuteChanged();
        try
        {
            var defaults = ConfigDefaults.Bundle(SetupRun.Stamp(DateTimeOffset.UtcNow));
            var saved = _saved;
            var ports = Cards.Select(c => c.Port).ToList();
            var (defaultOutcomes, savedOutcomes) = await Task.Run(() =>
            {
                var d = ConfigService.Import(defaults, [.. ports.Where(p => ConfigDefaults.Has(p.Id))], null, null);
                var s = saved is null ? [] : ConfigService.Import(saved, [.. ports.Where(p => saved.Apps.ContainsKey(p.Id))], null, null);
                return (d, s);
            });

            foreach (var card in Cards)
            {
                var d = defaultOutcomes.FirstOrDefault(o => o.PortId.Equals(card.Port.Id, StringComparison.OrdinalIgnoreCase));
                var s = savedOutcomes.FirstOrDefault(o => o.PortId.Equals(card.Port.Id, StringComparison.OrdinalIgnoreCase));
                var technical = string.Join("\n", new[] { d, s }.Where(o => o is { Status: ConfigImportStatus.Failed }).Select(o => o!.Detail));
                card.UpdateSources(
                    StateOf(d, card, "defaults"), d is null ? [] : RunActions.SettingNames(d),
                    StateOf(s, card, "saved"), s is null ? [] : RunActions.SettingNames(s),
                    technical);
            }
        }
        finally
        {
            _checking = false;
            Raise(nameof(Checking));
            Raise(nameof(Summary));
            Refresh.RaiseCanExecuteChanged();
        }
    }

    private ConfigSourceState StateOf(ConfigImportOutcome? outcome, ConfigCard card, string source) => outcome switch
    {
        null => ConfigSourceState.None,
        { Status: ConfigImportStatus.Failed } => ConfigSourceState.Unreadable,
        { Status: ConfigImportStatus.Applied, Applied.Count: > 0 } => ConfigSourceState.Pending,
        { DestinationMissing.Count: > 0 } when !card.Found => ConfigSourceState.None,
        { DestinationMissing.Count: > 0 } => ConfigSourceState.Waiting,
        _ when _appliedNow.TryGetValue(card.Port.Id, out var applied) && applied == source => ConfigSourceState.AppliedNow,
        _ => ConfigSourceState.InPlace,
    };

    /// <summary>For each app, the newest recorded run that replaced its files (whose backups can be put back).</summary>
    private Dictionary<string, (string RunId, IReadOnlyList<WrittenConfigFile> Files)> LatestBackups()
    {
        var found = new Dictionary<string, (string, IReadOnlyList<WrittenConfigFile>)>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in _services.Store.ListRuns())
        {
            try
            {
                if (_services.Store.ReadRunRecord(id) is not { } json)
                {
                    continue;
                }

                foreach (var config in SetupRun.FromJson(json).Config.Where(c => c.Written.Count > 0))
                {
                    found.TryAdd(config.PortId, (id, config.Written));
                }
            }
            catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException or ArgumentException)
            {
                // An unreadable record says nothing either way.
            }
        }

        return found;
    }

    internal async Task ApplyAsync(ConfigCard card, bool saved)
    {
        var bundle = saved ? _saved : ConfigDefaults.Bundle(SetupRun.Stamp(DateTimeOffset.UtcNow));
        if (bundle is null)
        {
            return;
        }

        Busy = true;
        try
        {
            var (_, preview, _) = await RunActions.ImportAsync(_services, bundle, [card.Port], apply: false);
            var outcome = preview.FirstOrDefault();
            if (outcome is null || outcome.Status == ConfigImportStatus.Unchanged)
            {
                Result.Show(outcome?.DestinationMissing.Count > 0 ? Tone.Warn : Tone.Ok,
                    outcome?.DestinationMissing.Count > 0 ? Loc.Format("FollowUp.StartAppOnce", card.Name) : Loc.Format("Config.AlreadySet", card.Name));
                return;
            }

            if (outcome.Status == ConfigImportStatus.Failed)
            {
                Result.Show(Tone.Danger, Loc.Format("FollowUp.ConfigFailed", card.Name), technical: outcome.Detail);
                return;
            }

            var title = saved ? Loc.Format("Config.ConfirmRestore", card.Name) : Loc.Format("Config.ConfirmDefaults", card.Name);
            var names = RunActions.SettingNames(outcome);
            if (!ConfirmDialog.Ask(title, Loc.Plural("Config.ConfirmCount", names.Count), names,
                    Loc.Format("Config.ConfirmNote", card.Name), Loc.Get("Config.Apply")))
            {
                return;
            }

            var (run, outcomes, error) = await RunActions.ImportAsync(_services, bundle, [card.Port], apply: true);
            var result = outcomes.FirstOrDefault();
            var followUps = run is null ? [] : RunActions.FollowUps(_services, run).Select(f => f.Text).ToList();
            if (error is not null)
            {
                followUps.Add(error);
            }

            if (result?.Status == ConfigImportStatus.Applied)
            {
                _appliedNow[card.Port.Id] = saved ? "saved" : "defaults";
            }

            Result.Show(result?.Status == ConfigImportStatus.Applied ? Tone.Ok : Tone.Warn,
                result?.Status == ConfigImportStatus.Applied
                    ? Loc.Format(saved ? "Config.ResultRestoredSaved" : "Config.ResultApplied", card.Name)
                    : Loc.Format("Config.ResultNotApplied", card.Name),
                string.Join("\n", followUps), details: run is null ? null : () => _openActivity(run.Id),
                technical: run is null ? null : RunActions.TechnicalLines(run));
        }
        finally
        {
            Busy = false;
            Reload();
            _ = CheckAsync();
        }
    }

    internal async Task UndoAsync(ConfigCard card)
    {
        if (card.UndoRun is not { } runId
            || !ConfirmDialog.Ask(Loc.Format("Config.ConfirmUndo", card.Name), Loc.Get("Config.ConfirmUndoMessage"),
                note: Loc.Format("Config.ConfirmNote", card.Name), confirm: Loc.Get("Undo.Action")))
        {
            return;
        }

        Busy = true;
        var change = _services.BeginChange();
        try
        {
            var started = DateTimeOffset.UtcNow;
            var id = SetupRun.NewId(started, "config-restore");
            var outcome = await Task.Run(() => ConfigService.RestoreBackups(card.Port, runId, card.UndoFiles, _services.Store, id));
            if (outcome.Written.Count == 0 && outcome.Status is ConfigImportStatus.Unchanged or ConfigImportStatus.ChangedSince)
            {
                // Nothing was put back: already as before, or changed since (left as it is rather than overwritten).
                Result.Show(Tone.Neutral, Loc.Get("Undo.NothingLeft"),
                    outcome.Status == ConfigImportStatus.ChangedSince ? Loc.Format("FollowUp.ConfigChangedSince", card.Name) : "");
                return;
            }

            // Recorded whatever happened (restored, skipped while the app ran, failed), as an undo from Activity is.
            var run = SetupRun.Create(id, "config-restore", null, SetupRun.Stamp(started), SetupRun.Stamp(DateTimeOffset.UtcNow), [], [outcome]);
            var error = Dialogs.TryRecord(_services, run);
            _appliedNow.Remove(card.Port.Id);
            Result.Show(outcome.Status == ConfigImportStatus.Applied ? Tone.Ok : Tone.Warn,
                outcome.Status switch
                {
                    ConfigImportStatus.Applied => Loc.Format("Config.ResultRestored", card.Name),
                    ConfigImportStatus.ChangedSince => Loc.Get("Undo.Partial"), // one file put back, another changed since
                    ConfigImportStatus.SkippedAppRunning => Loc.Format("FollowUp.AppRunning", card.Name),
                    _ => Loc.Format("FollowUp.ConfigFailed", card.Name),
                },
                string.Join("\n", new[]
                {
                    outcome.Status == ConfigImportStatus.ChangedSince ? Loc.Format("FollowUp.ConfigChangedSince", card.Name) : "",
                    error ?? "",
                }.Where(t => t.Length > 0)),
                details: error is null ? () => _openActivity(run.Id) : null,
                technical: outcome.Status == ConfigImportStatus.Failed ? outcome.Detail : null);
        }
        finally
        {
            change.Dispose();
            Busy = false;
        }
    }

    private async Task SaveMineAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = Loc.Get("Config.SaveDialogTitle"),
            Filter = Loc.Get("Config.FileFilter"),
            FileName = "my-settings.json",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        Busy = true;
        try
        {
            var result = await Task.Run(() => ConfigService.Export(ConfigCatalog.All, SetupRun.Stamp(DateTimeOffset.UtcNow)));
            LocalStore.WriteUserExport(dialog.FileName, Encoding.UTF8.GetBytes(result.BundleJson));
            _services.Settings.SavedConfiguration = dialog.FileName;
            _services.SaveSettings();
            _services.Log($"Settings exported to {dialog.FileName}: " + string.Join(", ", result.Apps.Select(a => $"{a.Port.AppName} {a.Bundle.Values.Count}")));
            LoadSaved();
            SavedChanged?.Invoke();
            var captured = result.Apps.Where(a => a.Bundle.Values.Count > 0).Select(a => a.Port.AppName).ToList();
            Result.Show(Tone.Ok, Loc.Format("Config.ResultSaved", Path.GetFileName(dialog.FileName)),
                captured.Count == 0 ? Loc.Get("Config.NothingCaptured") : Loc.Format("Config.Captured", string.Join(", ", captured)));
        }
        finally
        {
            Busy = false;
            _ = CheckAsync();
        }
    }

    private void DoChooseSaved()
    {
        var dialog = new OpenFileDialog { Title = Loc.Get("Config.OpenDialogTitle"), Filter = Loc.Get("Config.FileFilter") };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            ConfigBundleFile.Load(File.ReadAllText(dialog.FileName));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            Result.Show(Tone.Danger, Loc.Format("Config.SavedUnreadable", Path.GetFileName(dialog.FileName)), technical: e.Message);
            return;
        }

        _services.Settings.SavedConfiguration = dialog.FileName;
        _services.SaveSettings();
        LoadSaved();
        SavedChanged?.Invoke();
        Result.Hide();
        _ = CheckAsync();
    }

    internal void Fail(Exception e)
    {
        Busy = false;
        _services.Log($"Configurations: {e}");
        Result.Show(Tone.Danger, Loc.Get("Common.Unexpected"), technical: e.Message);
    }

    private void RaiseCommands()
    {
        SaveMine.RaiseCanExecuteChanged();
        ChooseSaved.RaiseCanExecuteChanged();
        Refresh.RaiseCanExecuteChanged();
        foreach (var card in Cards)
        {
            card.RaiseCommands();
        }
    }
}
