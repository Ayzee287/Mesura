using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mesura.Core.Apps;
using Mesura.Core.Setup;
using Mesura.Core.Windows;
using Mesura.Platform.Windows;
using Mesura.Setup.Windows.Profiles;
using Microsoft.Win32;

namespace Mesura.App;

/// <summary>
/// A line in a profile step or a run: what would happen or happened to one item, in its tone; for an app, its id and
/// name (the line shows the app's icon); a raw message behind it, shown on hover only.
/// </summary>
public sealed record StepLine(string Tone, string Text, string? Technical = null, string? AppId = null, string? AppName = null)
{
    public bool HasTechnical => !string.IsNullOrEmpty(Technical);
}

/// <summary>One step of a profile (apps, app settings, Windows settings, what's left), with its lines.</summary>
public sealed class ProfileStep(string number, string title, string summary, string tone, IReadOnlyList<StepLine> lines) : Observable
{
    private bool _expanded;

    public string Number { get; } = number;

    public string Title { get; } = title;

    public string Summary { get; } = summary;

    public string Tone { get; } = tone;

    public IReadOnlyList<StepLine> Lines { get; } = lines;

    public bool HasLines => Lines.Count > 0;

    public bool Expanded
    {
        get => _expanded;
        set => Set(ref _expanded, value);
    }
}

/// <summary>A profile Profiles lists: one remembered file, what it holds, when it was used here and how its last restore went.</summary>
public sealed class RecentProfileItem : Observable
{
    private readonly ProfilesViewModel _owner;

    public RecentProfileItem(RecentProfile entry, ProfilesViewModel owner)
    {
        Entry = entry;
        _owner = owner;
        Apps = owner.Chips(entry.Apps);
        Open = new AsyncCommand(() => owner.OpenPathAsync(entry.Path), () => owner.CanOpen && File.Exists(entry.Path), owner.Fail);
    }

    public RecentProfile Entry { get; }

    public string Name => Entry.Name;

    public string FileName => Path.GetFileName(Entry.Path);

    public bool Exists => File.Exists(Entry.Path);

    public IReadOnlyList<PlanAppChip> Apps { get; }

    public bool HasApps => Apps.Count > 0;

    public string Contents => _owner.ContentsLine(Entry.Apps?.Count ?? 0, Entry.SettingsApps, Entry.WindowsSettings, Entry.Presets, Entry.Nvidia);

    public string When => Loc.Format($"Profiles.Recent{Entry.Action}", RunActions.When(Entry.LastUsedUtc));

    public string StatusText => !Exists ? Loc.Get("Profiles.RecentMissing")
        : Entry.LastStatus is { } status ? Loc.Format("Profiles.LastRestore", Loc.Get($"Activity.Status.{status}"))
        : "";

    public string StatusTone => !Exists ? Tone.Muted : Entry.LastStatus switch
    {
        "Succeeded" => Tone.Ok,
        "Incomplete" => Tone.Warn,
        "NeedsAttention" => Tone.Danger,
        _ => Tone.Neutral,
    };

    public bool HasStatus => StatusText.Length > 0;

    public AsyncCommand Open { get; }

    public void Relocalize() => Raise(string.Empty);
}

/// <summary>
/// Profiles (redesign §2, made clearer in the 2026-10-24 quality pass): two distinct actions. <em>Save this PC</em> reads
/// the PC first and shows what the profile will hold (apps, their settings, the Windows outcome and settings, the NVIDIA
/// configuration) before it is written to a file; <em>Restore a profile</em> opens one, previews its steps here, runs
/// them in order (install, settings, Windows) and can run again for whatever had to wait. Profiles saved or opened here
/// are listed with what they hold and how their last restore went. It reuses the profile runner the CLI uses, so the
/// steps, the record and the reruns are the same: completed steps are skipped.
/// </summary>
public sealed class ProfilesViewModel : Observable, IPageState
{
    private const string SavedOnPrefix = "Saved from a PC by Mesura on ";
    private readonly Services _services;
    private readonly Action<string?> _openActivity;
    private bool _busy;
    private ProfilePlan? _plan;
    private string _folder = "";
    private string _profileFile = "";
    private string _profilePath = "";
    private string _progress = "";
    private bool _waiting;
    private CancellationTokenSource? _cancel;
    private ProfileCapture? _capture;

    public ProfilesViewModel(Services services, Action<string?> openActivity)
    {
        _services = services;
        _openActivity = openActivity;
        Save = new AsyncCommand(ReadForSaveAsync, () => !_busy && Ready, Fail);
        ConfirmSave = new AsyncCommand(WriteProfileAsync, () => !_busy && _capture is not null, Fail);
        CancelSave = new RelayCommand(() =>
        {
            _capture = null;
            RaiseCapture();
        }, () => !_busy);
        Open = new AsyncCommand(OpenAsync, () => CanOpen, Fail);
        // An unreadable settings file stops a run, except as another account, where the settings step is skipped anyway.
        Run = new AsyncCommand(RunAsync, () => !_busy && !_services.Changing && _plan is not null && (_plan.BundleError is null || !_services.SameUser), Fail);
        Close = new RelayCommand(() =>
        {
            _plan = null;
            Steps.Clear();
            Result.Hide();
            Raise(string.Empty);
        }, () => !_busy);
        Cancel = new RelayCommand(() =>
        {
            _cancel?.Cancel();
            Progress = Loc.Get("Apps.Cancelling");
            Cancel!.RaiseCanExecuteChanged();
        }, () => _cancel is { IsCancellationRequested: false });
        services.ChangingChanged += RaiseCommands;
        Loc.Instance.LanguageChanged += () =>
        {
            LoadRecent();
            Raise(string.Empty);
        };
        LoadRecent();
    }

    public AsyncCommand Save { get; }

    public AsyncCommand ConfirmSave { get; }

    public RelayCommand CancelSave { get; }

    public AsyncCommand Open { get; }

    public AsyncCommand Run { get; }

    public RelayCommand Close { get; }

    public RelayCommand Cancel { get; }

    public Banner Result { get; } = new();

    public ObservableCollection<ProfileStep> Steps { get; } = [];

    /// <summary>Profiles saved or opened here, newest first.</summary>
    public ObservableCollection<RecentProfileItem> Recent { get; } = [];

    public bool HasRecent => Recent.Count > 0;

    public bool Ready => _services.Apps.Catalog is not null && _services.WindowsCatalog.Catalog is not null;

    internal bool CanOpen => !_busy && Ready;

    public string Unavailable => Ready ? "" : Loc.Format("Profiles.Unavailable", _services.Apps.Error ?? _services.WindowsCatalog.Error ?? "");

    public string ProfileNote => _services.ProfileNote;

    public bool HasProfile => _plan is not null;

    public string ProfileName => _plan?.Profile.Name ?? "";

    public string ProfileDetail => _plan is null ? ""
        : string.Join(" · ", new[] { _profileFile, Description(_plan.Profile.Description) }.Where(s => s.Length > 0));

    /// <summary>The opened profile's apps, as icons, and what it holds in one line.</summary>
    public IReadOnlyList<PlanAppChip> ProfileApps => _plan is null ? [] : Chips(_plan.Profile.Apps);

    public string ProfileContents => _plan is null ? ""
        : ContentsLine(_plan.Profile.Apps.Count, _plan.Ports.Count, _plan.Profile.Windows.Count, _plan.Profile.WindowsPresets, _plan.Profile.Nvidia is not null);

    public string RunLabel => _waiting ? Loc.Get("Profiles.RunAgain") : Loc.Get("Profiles.Run");

    public bool Running => _cancel is not null;

    // ---- Save: what the profile will hold, before it is written ------------------------------------------------------

    public bool HasPendingSave => _capture is not null;

    public IReadOnlyList<PlanAppChip> PendingApps => _capture is null ? [] : Chips(_capture.Profile.Apps);

    public bool HasPendingApps => PendingApps.Count > 0;

    /// <summary>What the profile about to be saved holds, one line per part.</summary>
    public IReadOnlyList<StepLine> PendingLines
    {
        get
        {
            if (_capture is not { } capture)
            {
                return [];
            }

            var profile = capture.Profile;
            var lines = new List<StepLine>
            {
                new(profile.Apps.Count > 0 ? Tone.Ok : Tone.Muted, Loc.Plural("Profiles.HoldsApps", profile.Apps.Count)),
                capture.Settings.Count > 0
                    ? new(Tone.Ok, Loc.Format("Profiles.HoldsSettings", string.Join(", ", capture.Settings.Select(s => s.Port.AppName))))
                    : new(Tone.Muted, _services.SameUser ? Loc.Get("Profiles.HoldsNoSettings") : Loc.Get("Profiles.SettingsNotSavedOtherUser")),
                new(profile.WindowsPresets.Count > 0 || profile.Windows.Count > 0 ? Tone.Ok : Tone.Muted, WindowsLine(profile.WindowsPresets, profile.Windows.Count)),
            };
            if (profile.Nvidia is not null)
            {
                lines.Add(new(Tone.Ok, Loc.Get("Profiles.HoldsNvidia")));
            }

            return lines;
        }
    }

    public string Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
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

    public void Leave() => Result.Settle();

    private string AppName(string id) => _services.Apps.Catalog?.ById(id)?.Name ?? id;

    private string OpName(string id) => _services.WindowsCatalog.Catalog?.ById(id) is { } op ? Loc.Name(op) : id;

    /// <summary>App ids as icon chips, in the order given (unknown ids under their own name).</summary>
    internal IReadOnlyList<PlanAppChip> Chips(IEnumerable<string>? ids) => [.. (ids ?? []).Select(id => new PlanAppChip(AppName(id), id))];

    /// <summary>The Windows part of a profile in words: its outcome, plus how many single settings.</summary>
    private string WindowsLine(IReadOnlyList<string> presets, int settings)
    {
        var names = presets.Select(p => _services.WindowsCatalog.Catalog?.PresetById(p) is { } preset ? Loc.Name(preset) : p).ToList();
        return names.Count > 0
            ? Loc.Format("Profiles.SavedOutcome", string.Join(", ", names)) + (settings > 0 ? " + " + Loc.Plural("Profiles.SavedWindows", settings) : "")
            : Loc.Plural("Profiles.SavedWindows", settings);
    }

    /// <summary>What a profile holds, in one line: apps, apps' settings, Windows, NVIDIA.</summary>
    internal string ContentsLine(int apps, int settingsApps, int windows, IReadOnlyList<string>? presets, bool nvidia)
    {
        var parts = new List<string> { Loc.Plural("Profiles.SavedApps", apps), Loc.Plural("Profiles.SavedSettings", settingsApps), WindowsLine(presets ?? [], windows) };
        if (nvidia)
        {
            parts.Add(Loc.Get("Profiles.NvidiaShort"));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>A profile's description in the user's language when Mesura wrote it ("Saved from a PC by Mesura on …").</summary>
    private static string Description(string description) =>
        description.StartsWith(SavedOnPrefix, StringComparison.Ordinal) && description.TrimEnd('.').Length > SavedOnPrefix.Length
            ? Loc.Format("Profiles.SavedOn", RunActions.When(description[SavedOnPrefix.Length..].TrimEnd('.'), dateOnly: true))
            : description;

    private void LoadRecent()
    {
        Recent.Clear();
        foreach (var entry in _services.Settings.RecentProfiles ?? [])
        {
            Recent.Add(new RecentProfileItem(entry, this));
        }

        Raise(nameof(HasRecent));
    }

    private void Remember(string path, SetupProfile profile, string action, int settingsApps, string? runId = null, string? status = null)
    {
        _services.Settings.Remember(new RecentProfile(path, profile.Name, SetupRun.Stamp(DateTimeOffset.UtcNow), action,
            [.. profile.Apps], settingsApps, profile.Windows.Count, [.. profile.WindowsPresets], profile.Nvidia is not null, runId, status));
        _services.SaveSettings();
        LoadRecent();
    }

    /// <summary>Reads this PC (read only) and shows what a profile of it would hold; nothing is written yet.</summary>
    private async Task ReadForSaveAsync()
    {
        Busy = true;
        Progress = Loc.Get("Profiles.Reading");
        Result.Hide();
        try
        {
            var outcome = _services.Settings.Outcome;
            // As another account than the signed-in user, the app settings Mesura could read are that account's: the
            // profile is saved without them.
            var withSettings = _services.SameUser;
            var capture = await Task.Run(() =>
            {
                _services.Reader.Refresh();
                return ProfileRunner.Capture("my-setup", "my-setup.settings.json", _services.Apps.Catalog!, _services.Installer,
                    _services.WindowsCatalog.Catalog!, _services.Reader, SetupRun.Stamp(DateTimeOffset.UtcNow), outcome, withSettings,
                    _services.Settings.NvidiaConfig);
            });

            if (!capture.Profile.SelectsAnything)
            {
                Result.Show(Tone.Warn, Loc.Get("Profiles.NothingToSave"));
                return;
            }

            _capture = capture;
            RaiseCapture();
        }
        finally
        {
            Progress = "";
            Busy = false;
        }
    }

    /// <summary>Writes the profile shown (and its settings file beside it) where the user chooses.</summary>
    private async Task WriteProfileAsync()
    {
        if (_capture is not { } capture)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = Loc.Get("Profiles.SaveDialogTitle"),
            Filter = Loc.Get("Profiles.SaveFilter"),
            FileName = "my-setup.profile.json",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var profilePath = dialog.FileName;
        var stem = Path.GetFileName(profilePath);
        stem = stem.EndsWith(".profile.json", StringComparison.OrdinalIgnoreCase) ? stem[..^".profile.json".Length] : Path.GetFileNameWithoutExtension(stem);
        var bundleName = stem + ".settings.json";
        var bundlePath = Path.Combine(Path.GetDirectoryName(profilePath)!, bundleName);
        // As another account no settings file is written, so there is nothing to replace.
        if (capture.BundleJson is not null && File.Exists(bundlePath)
            && !ConfirmDialog.Ask(Loc.Get("Profiles.ReplaceTitle"), Loc.Format("Profiles.ReplaceMessage", bundleName)))
        {
            return;
        }

        Busy = true;
        try
        {
            var profile = capture.Profile with { Name = stem, ConfigBundle = capture.Profile.ConfigBundle is null ? null : bundleName };
            await Task.Run(() =>
            {
                if (capture.BundleJson is { } bundle)
                {
                    LocalStore.WriteUserExport(bundlePath, Encoding.UTF8.GetBytes(bundle));
                }

                LocalStore.WriteUserExport(profilePath, Encoding.UTF8.GetBytes(profile.ToJson()));
            });
            _services.Log($"Profile saved: {profilePath} ({profile.Apps.Count} apps, {capture.Settings.Count} app settings, presets {string.Join(",", profile.WindowsPresets)}, {profile.Windows.Count} settings).");
            Remember(profilePath, profile, "Saved", capture.Settings.Count);
            _capture = null;
            RaiseCapture();
            Result.Show(_services.SameUser ? Tone.Ok : Tone.Warn, Loc.Format("Profiles.Saved", Path.GetFileName(profilePath)),
                string.Join(Environment.NewLine, new[]
                {
                    ContentsLine(profile.Apps.Count, capture.Settings.Count, profile.Windows.Count, profile.WindowsPresets, profile.Nvidia is not null),
                    _services.SameUser ? "" : Loc.Get("Profiles.SettingsNotSavedOtherUser"),
                    capture.BundleJson is null ? "" : Loc.Get("Profiles.SavedHint"),
                }.Where(line => line.Length > 0)));
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task OpenAsync()
    {
        var dialog = new OpenFileDialog { Title = Loc.Get("Profiles.OpenDialogTitle"), Filter = Loc.Get("Profiles.OpenFilter") };
        if (dialog.ShowDialog() == true)
        {
            await OpenPathAsync(dialog.FileName);
        }
    }

    /// <summary>Opens a profile file and previews what it would do here now.</summary>
    internal async Task OpenPathAsync(string path)
    {
        SetupProfile profile;
        try
        {
            profile = SetupProfile.Parse(await File.ReadAllTextAsync(path), _services.Apps.Catalog!, _services.WindowsCatalog.Catalog);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            Result.Show(Tone.Danger, Loc.Format("Profiles.Unreadable", Path.GetFileName(path)), technical: e.Message);
            return;
        }

        Busy = true;
        Progress = Loc.Format("Profiles.Checking", profile.Name);
        Result.Hide();
        try
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
            var sameUser = _services.SameUser;
            var (plan, config, windows) = await Task.Run(() =>
            {
                _services.Reader.Refresh();
                var p = ProfileRunner.Plan(profile, folder, _services.Apps.Catalog!, _services.Installer, _services.WindowsCatalog.Catalog, _services.Store);
                // As another account, the settings step is skipped (it would write that account's files), so it isn't
                // previewed against them either.
                return (p, sameUser ? ProfileRunner.PreviewConfig(p) : [], p.Windows.Select(op => WindowsEvaluator.Evaluate(op, _services.Reader)).ToList());
            });
            _plan = plan;
            _folder = folder;
            _profileFile = Path.GetFileName(path);
            _profilePath = Path.GetFullPath(path);
            _waiting = false;
            var previous = _services.Settings.RecentProfiles?.FirstOrDefault(r => string.Equals(r.Path, _profilePath, StringComparison.OrdinalIgnoreCase));
            Remember(_profilePath, profile, "Opened", plan.Ports.Count, previous?.LastRunId, previous?.LastStatus);
            Preview(plan, config, windows);
            Raise(string.Empty);
        }
        finally
        {
            Progress = "";
            Busy = false;
        }
    }

    private void RaiseCapture()
    {
        Raise(nameof(HasPendingSave));
        Raise(nameof(PendingApps));
        Raise(nameof(HasPendingApps));
        Raise(nameof(PendingLines));
        RaiseCommands();
    }

    /// <summary>The steps as they would run here now.</summary>
    private void Preview(ProfilePlan plan, IReadOnlyList<ConfigImportOutcome> config, IReadOnlyList<OperationEvaluation> windows)
    {
        Steps.Clear();

        var apps = plan.Apps.Select(a => a switch
        {
            _ when plan.InstalledEarlier.Contains(a.Package.Id) => new StepLine(Tone.Ok, Loc.Format("Profiles.InstalledEarlier", a.Package.Name), AppId: a.Package.Id, AppName: a.Package.Name),
            { Package.Source: AppSource.Manual } => new StepLine(Tone.Info, Loc.Format("Profiles.ByHand", a.Package.Name), AppId: a.Package.Id, AppName: a.Package.Name),
            { State: InstallState.Installed } => new StepLine(Tone.Ok, Loc.Format("Profiles.AlreadyInstalled", a.Package.Name), AppId: a.Package.Id, AppName: a.Package.Name),
            _ => new StepLine(Tone.Accent, Loc.Format("Profiles.WillInstall", a.Package.Name), AppId: a.Package.Id, AppName: a.Package.Name),
        }).OrderBy(l => l.Tone == Tone.Ok).ToList();
        if (plan.WingetMissing)
        {
            apps.Insert(0, new StepLine(Tone.Danger, Loc.Get("Profiles.NoWinget")));
        }

        var install = apps.Count(l => l.Tone == Tone.Accent);
        Steps.Add(new ProfileStep("1", Loc.Get("Profiles.StepApps"),
            install == 0 ? Loc.Get("Profiles.NothingToDo") : Loc.Plural("Profiles.ToInstall", install), install == 0 ? Tone.Ok : Tone.Accent, apps));

        var settings = new List<StepLine>();
        string settingsSummary;
        string settingsTone;
        if ((plan.Bundle is not null || plan.BundleError is not null) && !_services.SameUser)
        {
            settings.Add(new StepLine(Tone.Warn, Loc.Get("Profiles.ConfigSkippedOtherUser")));
            settingsSummary = Loc.Get("Profiles.SettingsSkipped");
            settingsTone = Tone.Warn;
        }
        else if (plan.BundleError is not null)
        {
            settings.Add(new StepLine(Tone.Danger, Loc.Get("Profiles.SettingsUnreadableLine"), plan.BundleError));
            settingsSummary = Loc.Get("Profiles.SettingsUnreadable");
            settingsTone = Tone.Danger;
        }
        else if (plan.Bundle is null)
        {
            settingsSummary = Loc.Get("Profiles.NoSettings");
            settingsTone = Tone.Muted;
        }
        else
        {
            settings.AddRange(config.Select(o => o.Status switch
            {
                ConfigImportStatus.Applied => new StepLine(Tone.Accent, Loc.Plural("Profiles.SettingsToImport", o.Applied.Count, o.AppName), AppId: o.PortId, AppName: o.AppName),
                ConfigImportStatus.Unchanged when o.DestinationMissing.Count > 0 => new StepLine(Tone.Warn, Loc.Format("FollowUp.StartAppOnce", o.AppName), AppId: o.PortId, AppName: o.AppName),
                ConfigImportStatus.Unchanged => new StepLine(Tone.Ok, Loc.Format("Profiles.SettingsAlready", o.AppName), AppId: o.PortId, AppName: o.AppName),
                ConfigImportStatus.SkippedAppRunning => new StepLine(Tone.Warn, Loc.Format("FollowUp.AppRunning", o.AppName), AppId: o.PortId, AppName: o.AppName),
                _ => new StepLine(Tone.Danger, Loc.Format("FollowUp.ConfigFailed", o.AppName), o.Detail, o.PortId, o.AppName),
            }));
            var pending = settings.Count(l => l.Tone == Tone.Accent);
            settingsSummary = pending == 0 ? Loc.Get("Profiles.NothingToDo") : Loc.Plural("Profiles.AppsToSetUp", pending);
            settingsTone = pending == 0 ? Tone.Ok : Tone.Accent;
        }

        Steps.Add(new ProfileStep("2", Loc.Get("Profiles.StepSettings"), settingsSummary, settingsTone, settings));

        var winLines = windows.Select(w => w.State switch
        {
            OperationState.NotApplied or OperationState.Partial when w.Operation.NeedsElevation && !_services.Reader.IsElevated =>
                new StepLine(Tone.Warn, $"{Loc.Name(w.Operation)} · {Loc.Get("Common.NeedsAdmin")}"),
            OperationState.NotApplied or OperationState.Partial => new StepLine(Tone.Accent, Loc.Name(w.Operation)),
            OperationState.Applied => new StepLine(Tone.Ok, $"{Loc.Name(w.Operation)} · {Loc.Get("Step.AlreadyApplied")}"),
            OperationState.Inapplicable => new StepLine(Tone.Muted, $"{Loc.Name(w.Operation)} · {Loc.Get("State.Inapplicable")}"),
            OperationState.Observation or OperationState.Manual => new StepLine(Tone.Info, $"{Loc.Name(w.Operation)} · {Loc.Get("Profiles.Guide")}"),
            _ => new StepLine(Tone.Danger, $"{Loc.Name(w.Operation)} · {Loc.Get($"State.{w.State}")}", w.Reason),
        }).OrderBy(l => l.Tone == Tone.Ok || l.Tone == Tone.Muted).ToList();
        winLines.AddRange(RunActions.ConflictLines(WindowsConfigurator.ConflictGroups(plan.Windows, _services.Reader)).Select(c => new StepLine(Tone.Danger, c)));
        var toApply = winLines.Count(l => l.Tone == Tone.Accent);
        var presets = plan.Profile.WindowsPresets.Select(p => _services.WindowsCatalog.Catalog?.PresetById(p) is { } preset ? Loc.Name(preset) : p).ToList();
        Steps.Add(new ProfileStep("3", Loc.Get("Profiles.StepWindows"),
            (presets.Count > 0 ? string.Join(", ", presets) + " · " : "") + (toApply == 0 ? Loc.Get("Profiles.NothingToDo") : Loc.Plural("Profiles.ToApply", toApply)),
            toApply == 0 ? Tone.Ok : Tone.Accent, winLines));
        if (NvidiaStep(plan.Profile, adopted: false) is { } nvidia)
        {
            Steps.Add(nvidia);
        }
    }

    /// <summary>
    /// The profile's NVIDIA configuration as a step (D032): it becomes this PC's when the profile runs; the NVIDIA steps
    /// themselves run from Setup. <c>null</c> when the profile carries none.
    /// </summary>
    private ProfileStep? NvidiaStep(SetupProfile profile, bool adopted)
    {
        if (profile.Nvidia is not { } config)
        {
            return null;
        }

        var lines = new List<StepLine>
        {
            new(Tone.Neutral, Loc.Format("Profiles.NvidiaComponents", string.Join(", ", config.Components.Select(id => Mesura.Core.Nvidia.NvidiaComponents.ById(id)?.Label ?? id)))),
            new(Tone.Neutral, Loc.Plural("Profiles.NvidiaTweaks", config.Tweaks.Count)),
            new(Tone.Neutral, config.Profile is { } named && Mesura.Core.Nvidia.NvidiaProfiles.ById(named.Id) is { } builtIn
                ? Loc.Format("Profiles.NvidiaProfile", builtIn.Name)
                : Loc.Get("Profiles.NvidiaNoProfile")),
        };
        return new ProfileStep((Steps.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), Loc.Get("Profiles.StepNvidia"),
            adopted ? Loc.Get("Profiles.NvidiaAdopted") : Loc.Get("Profiles.NvidiaWillAdopt"), adopted ? Tone.Ok : Tone.Accent, lines);
    }

    private async Task RunAsync()
    {
        if (_plan is not { } plan)
        {
            return;
        }

        if (RunActions.Conflicts(_services, plan.Windows) is { Count: > 0 } conflicts)
        {
            Result.Show(Tone.Danger, Loc.Get("Profiles.Conflict"), string.Join("\n", conflicts));
            return;
        }

        if (!ConfirmDialog.Ask(Loc.Format("Profiles.ConfirmTitle", plan.Profile.Name), Loc.Get("Profiles.ConfirmMessage"),
                [Loc.Get("Profiles.StepApps"), Loc.Get("Profiles.StepSettings"), Loc.Get("Profiles.StepWindows")], Loc.Get("Profiles.ConfirmNote"),
                Loc.Get("Profiles.Run")))
        {
            return;
        }

        _cancel = new CancellationTokenSource();
        Raise(nameof(Running));
        Busy = true;
        Progress = Loc.Format("Profiles.RunningProfile", plan.Profile.Name);
        var change = _services.BeginChange();
        try
        {
            var token = _cancel.Token;
            var folder = _folder;
            var settingsSkipped = !_services.SameUser && (plan.Bundle is not null || plan.BundleError is not null);
            var result = await Task.Run<(SetupRun Run, ProfilePlan Plan)?>(() =>
            {
                // Plan again from the PC as it is now, so a second run skips what the first one did.
                _services.Reader.Refresh();
                var fresh = ProfileRunner.Plan(plan.Profile, folder, _services.Apps.Catalog!, _services.Installer, _services.WindowsCatalog.Catalog, _services.Store);
                if (settingsSkipped)
                {
                    // App settings live in the process user's profile, which isn't the signed-in user's: leave them out.
                    fresh = fresh with { Bundle = null, Ports = [], BundleError = null };
                }

                if (fresh.BundleError is not null)
                {
                    return null;
                }

                // Recorded below, on the page's thread, so a failed history write is reported beside the outcome.
                var ran = ProfileRunner.Run(fresh, _services.Installer, _services.Store, () => DateTimeOffset.UtcNow, (package, line) =>
                {
                    // winget's own lines (English) go to the session log; the page says which app is being installed.
                    _services.Log($"  [{package.Name}] {line.Trim()}");
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Progress = Loc.Format("Profiles.Installing", package.Name));
                }, token, _services.Windows, record: false);
                return (ran, fresh);
            });
            if (result is not { } done)
            {
                Result.Show(Tone.Danger, Loc.Format("Profiles.BundleGone", plan.Profile.Name));
                return;
            }

            var run = done.Run;

            // The profile's NVIDIA configuration becomes this PC's (the NVIDIA steps run from Setup).
            if (plan.Profile.Nvidia is { } nvidia && !token.IsCancellationRequested)
            {
                _services.Settings.NvidiaConfig = nvidia;
                _services.SaveSettings();
                _services.NvidiaConfigUpdated();
                _services.Log($"Profile {plan.Profile.Name}: its NVIDIA configuration is now this PC's.");
            }

            var recordError = run.ChangedNothing ? null : Dialogs.TryRecord(_services, run);
            var followUps = RunActions.FollowUps(_services, run).ToList();
            if (settingsSkipped)
            {
                followUps.Insert(0, (Tone.Warn, Loc.Get("Profiles.ConfigSkippedOtherUser")));
            }

            if (recordError is not null)
            {
                followUps.Add((Tone.Warn, recordError));
            }

            var status = RunFollowUp.Status(run);
            _waiting = status == RunStatus.Incomplete;
            if (_profilePath.Length > 0)
            {
                // Profiles lists the profile with how its last restore here went.
                Remember(_profilePath, plan.Profile, "Restored", done.Plan.Ports.Count, run.ChangedNothing ? null : run.Id, status.ToString());
            }

            Outcome(run, followUps, done.Plan, token.IsCancellationRequested, settingsSkipped);
            if (run.ChangedNothing && !token.IsCancellationRequested)
            {
                // With the settings step skipped, "everything is in place" would claim settings nobody checked.
                Result.Show(settingsSkipped ? Tone.Warn : Tone.Ok, Loc.Format("Profiles.AllInPlace", plan.Profile.Name),
                    settingsSkipped ? Loc.Get("Profiles.ConfigSkippedOtherUser") : "");
                return;
            }

            var finish = status == RunStatus.Succeeded ? RunActions.FinishKey(run) : null;
            Result.Show(status switch { RunStatus.Succeeded => finish is null ? Tone.Ok : Tone.Info, RunStatus.Incomplete => Tone.Warn, _ => Tone.Danger },
                token.IsCancellationRequested ? Loc.Format("Profiles.Cancelled", plan.Profile.Name)
                : status == RunStatus.Succeeded ? Loc.Format("Profiles.Done", plan.Profile.Name) + (finish is null ? "" : " · " + Loc.Get(finish))
                : status == RunStatus.Incomplete ? Loc.Format("Profiles.Waiting", plan.Profile.Name)
                : Loc.Format("Profiles.NeedsAttention", plan.Profile.Name),
                string.Join("\n", followUps.Select(f => f.Text)),
                recordError is null && RunActions.CanUndo(run) ? () => _ = UndoAsync(run) : null,
                !run.ChangedNothing && recordError is null ? () => _openActivity(run.Id) : null, RunActions.TechnicalLines(run));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Result.Show(Tone.Danger, Loc.Format("Profiles.Stopped", plan.Profile.Name), technical: e.Message);
        }
        finally
        {
            change.Dispose();
            _cancel.Dispose();
            _cancel = null;
            Raise(nameof(Running));
            Raise(nameof(RunLabel));
            Progress = "";
            Busy = false;
        }
    }

    /// <summary>Undoes the run's Windows settings and app settings (apps it installed stay installed).</summary>
    private async Task UndoAsync(SetupRun run)
    {
        if (_services.Changing || !ConfirmDialog.Ask(Loc.Get("Undo.ConfirmTitle"), Loc.Get("Undo.ConfirmMessageApps"), confirm: Loc.Get("Undo.Action")))
        {
            return;
        }

        Busy = true;
        try
        {
            await RunActions.UndoAndShowAsync(_services, run, Result, _openActivity);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// What each step did, in words and in its own colour (a failure or a wait shows on the step, not only inside it),
    /// a step the run never reached says so, then what is left.
    /// </summary>
    private void Outcome(SetupRun run, IReadOnlyList<(string Tone, string Text)> followUps, ProfilePlan plan, bool cancelled, bool settingsSkipped)
    {
        Steps.Clear();

        var apps = run.Apps.Select(a => new StepLine(Tone.Of(StepWords.Install(a.Status)), $"{AppName(a.AppId)} · {Loc.Get($"Install.{a.Status}")}",
            a.Status is InstallStatus.Failed or InstallStatus.WingetUnavailable ? a.Detail : null, a.AppId, AppName(a.AppId))).ToList();
        var installed = run.Apps.Count(a => a.Status is InstallStatus.Succeeded or InstallStatus.SucceededRebootRequired);
        var appsCancelled = run.Apps.Any(a => a.Status == InstallStatus.Cancelled);
        Steps.Add(new ProfileStep("1", Loc.Get("Profiles.StepApps"), Counted(Loc.Plural("Profiles.AppsDone", installed), apps), StepTone(apps, appsCancelled), apps));

        if (settingsSkipped)
        {
            Steps.Add(new ProfileStep("2", Loc.Get("Profiles.StepSettings"), Loc.Get("Profiles.SettingsSkipped"), Tone.Warn,
                [new StepLine(Tone.Warn, Loc.Get("Profiles.ConfigSkippedOtherUser"))]));
        }
        else if (plan.Bundle is null)
        {
            Steps.Add(new ProfileStep("2", Loc.Get("Profiles.StepSettings"), Loc.Get("Profiles.NoSettings"), Tone.Muted, []));
        }
        else if (cancelled && run.Config.Count == 0)
        {
            Steps.Add(new ProfileStep("2", Loc.Get("Profiles.StepSettings"), Loc.Get("Profiles.NotRun"), Tone.Muted, []));
        }
        else
        {
            var settings = run.Config.Select(c => new StepLine(Tone.Of(StepWords.Config(c)), RunActions.ConfigLine(c),
                c.Status == ConfigImportStatus.Failed ? c.Detail : null, c.PortId, c.AppName)).ToList();
            Steps.Add(new ProfileStep("2", Loc.Get("Profiles.StepSettings"),
                Counted(Loc.Plural("Profiles.SettingsDone", run.Config.Count(c => c.Status == ConfigImportStatus.Applied)), settings), StepTone(settings), settings));
        }

        if (plan.Windows.Count == 0)
        {
            Steps.Add(new ProfileStep("3", Loc.Get("Profiles.StepWindows"), Loc.Get("Profiles.NothingSelected"), Tone.Muted, []));
        }
        else
        {
            var windows = run.WindowsSteps.Select(w => new StepLine(Tone.Of(StepWords.Windows(w.Status)), $"{OpName(w.OperationId)} · {Loc.Get($"Step.{w.Status}")}")).ToList();
            // A cancelled run stops between Windows settings: the ones it never reached are listed as such.
            var reached = run.WindowsSteps.Select(w => w.OperationId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var notRun = plan.Windows.Where(op => !reached.Contains(op.Id)).Select(op => new StepLine(Tone.Muted, $"{Loc.Name(op)} · {Loc.Get("Profiles.NotRunLine")}")).ToList();
            windows.AddRange(notRun);
            Steps.Add(new ProfileStep("3", Loc.Get("Profiles.StepWindows"),
                run.WindowsSteps.Count == 0 ? Loc.Get("Profiles.NotRun")
                : Counted(Loc.Plural("Profiles.WindowsDone", run.WindowsSteps.Count(w => w.Status == WindowsOpStatus.Applied)), windows),
                run.WindowsSteps.Count == 0 ? Tone.Muted : StepTone(windows, notRun.Count > 0), windows));
        }

        if (!cancelled && NvidiaStep(plan.Profile, adopted: true) is { } nvidia)
        {
            Steps.Add(nvidia);
        }

        Steps.Add(new ProfileStep((Steps.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), Loc.Get("Profiles.StepLeft"), followUps.Count == 0 ? Loc.Get("Profiles.NothingLeft") : Loc.Plural("Profiles.ThingsLeft", followUps.Count),
            followUps.Count == 0 ? Tone.Ok : Tone.Warn, [.. followUps.Select(f => new StepLine(f.Tone, f.Text))]) { Expanded = followUps.Count > 0 });
    }

    /// <summary>A step's colour from its lines: red when one failed, amber when one waits (or some never ran), green otherwise.</summary>
    private static string StepTone(IReadOnlyList<StepLine> lines, bool incomplete = false) =>
        lines.Any(l => l.Tone == Tone.Danger) ? Tone.Danger
        : incomplete || lines.Any(l => l.Tone == Tone.Warn) ? Tone.Warn
        : Tone.Ok;

    /// <summary>A step's summary: what it did, then how many failed and how many wait.</summary>
    private static string Counted(string done, IReadOnlyList<StepLine> lines)
    {
        var failed = lines.Count(l => l.Tone == Tone.Danger);
        var waiting = lines.Count(l => l.Tone == Tone.Warn);
        return done + (failed > 0 ? " · " + Loc.Plural("Profiles.ItemsFailed", failed) : "") + (waiting > 0 ? " · " + Loc.Plural("Profiles.ItemsWaiting", waiting) : "");
    }

    internal void Fail(Exception e)
    {
        Busy = false;
        Progress = "";
        _services.Log($"Profiles: {e}");
        Result.Show(Tone.Danger, Loc.Get("Common.Unexpected"), technical: e.Message);
    }

    private void RaiseCommands()
    {
        Save.RaiseCanExecuteChanged();
        ConfirmSave.RaiseCanExecuteChanged();
        CancelSave.RaiseCanExecuteChanged();
        Open.RaiseCanExecuteChanged();
        foreach (var item in Recent)
        {
            item.Open.RaiseCanExecuteChanged();
        }
        Run.RaiseCanExecuteChanged();
        Close.RaiseCanExecuteChanged();
        Cancel.RaiseCanExecuteChanged();
    }
}
