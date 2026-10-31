using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Mesura.Core.Apps;
using Mesura.Core.Setup;
using Mesura.Core.Windows;

namespace Mesura.App;

/// <summary>One recorded run on Activity, in plain words: what changed, when, how it went, and whether it can be undone.</summary>
public sealed class ActivityItem : Observable
{
    private readonly Services _services;
    private bool _expanded;

    public ActivityItem(Services services, SetupRun run, AsyncCommand undo)
    {
        _services = services;
        Run = run;
        Undo = undo;
    }

    public SetupRun Run { get; }

    public AsyncCommand Undo { get; }

    public string Id => Run.Id;

    public bool Expanded
    {
        get => _expanded;
        set => Set(ref _expanded, value);
    }

    /// <summary>
    /// The app a run was about, when it was about one (an install of one app, one app's settings set up or restored), so
    /// Activity shows that app's icon instead of the run's glyph; <c>null</c> otherwise.
    /// </summary>
    public string? AppId =>
        Run.Operation is "config-import" or "config-restore" && Run.Config.Select(c => c.PortId).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 ? Run.Config[0].PortId
        : Run.Operation is "apps-install" or "apps-update" or "apps-uninstall" && Run.Apps.Count == 1 ? Run.Apps[0].AppId
        : null;

    public string AppLabel => AppId is { } id ? RunActions.AppName(_services, id) : "";

    public bool HasApp => AppId is not null;

    public string Glyph => Application.Current?.TryFindResource(Run.Operation switch
    {
        "apps-install" => "IconDownload",
        "apps-update" => "IconUpdate",
        "apps-uninstall" => "IconDebloat",
        "config-import" or "config-restore" => "IconConfigurations",
        "windows-revert" or "undo" => "IconUndo",
        "profile" => "IconProfiles",
        "setup" => "IconResume",
        _ => "IconOptimize",
    }) as string ?? "";

    /// <summary>What assistive technology reads for this item: its display name, never the type name.</summary>
    public override string ToString() => Title;

    public string Title
    {
        get
        {
            var applied = Run.WindowsSteps.Count(s => s.Status == WindowsOpStatus.Applied);
            var apps = Run.Apps.Count(a => a.Status is InstallStatus.Succeeded or InstallStatus.SucceededRebootRequired);
            var configured = string.Join(", ", Run.Config.Select(c => c.AppName));
            return Run.Operation switch
            {
                "windows-apply" when applied == 0 => WithSubject(Loc.Plural("Activity.WindowsTried", Run.WindowsSteps.Count)),
                "windows-apply" => WithSubject(Loc.Plural("Activity.WindowsApplied", applied)),
                "windows-revert" => WithSubject(Loc.Plural("Activity.WindowsReverted", Run.WindowsSteps.Count(s => s.Status == WindowsOpStatus.Reverted))),
                "apps-install" => Loc.Plural("Activity.AppsInstalled", apps) + (Run.Config.Count > 0 ? " · " + Loc.Get("Activity.AndSetUp") : ""),
                "apps-update" => Loc.Plural("Activity.AppsUpdated", apps),
                "apps-uninstall" => Loc.Plural("Activity.AppsUninstalled", Run.Apps.Count(a => a.Status is InstallStatus.Succeeded or InstallStatus.SucceededRebootRequired)),
                "config-import" => Loc.Format("Activity.ConfigImported", configured),
                "config-restore" => Loc.Format("Activity.ConfigRestored", configured),
                "undo" => Loc.Get("Activity.Undo"),
                "profile" => Loc.Format("Activity.Profile", Run.ProfileName ?? ""),
                "setup" => Loc.Format("Activity.Setup", SetupSummary()),
                var other => other,
            };
        }
    }

    /// <summary>
    /// A run that was about exactly one Windows setting names it, so a list of such runs doesn't read as the same line
    /// repeated ("Changed 1 Windows setting" three times); with several settings the count stays and the details list them.
    /// </summary>
    private string WithSubject(string phrase) =>
        Run.WindowsSteps.Count == 1 ? $"{phrase}: {RunActions.StepName(_services, Run.WindowsSteps[0])}" : phrase;

    /// <summary>What a setup did, in a few words: settings applied, apps and runtimes installed, the NVIDIA steps.</summary>
    private string SetupSummary()
    {
        var parts = new List<string>();
        var applied = Run.WindowsSteps.Count(s => s.Status == WindowsOpStatus.Applied);
        if (applied > 0)
        {
            parts.Add(Loc.Plural("Activity.SetupSettings", applied));
        }

        var installed = Run.Apps.Count(a => a.Status is InstallStatus.Succeeded or InstallStatus.SucceededRebootRequired);
        if (installed > 0)
        {
            parts.Add(Loc.Plural("Activity.SetupInstalls", installed));
        }

        if (Run.SetupSteps.Any(s => s.Kind == StepKind.NvidiaDriver))
        {
            parts.Add(Loc.Get("Setup.Step.NvidiaDriver"));
        }

        return parts.Count > 0 ? string.Join(", ", parts) : Loc.Get("Activity.SetupNothing");
    }

    public string When => RunActions.When(Run.StartedUtc);

    public RunStatus Status => RunFollowUp.Status(Run);

    /// <summary>How it went, in a word or two; a run that went well but takes effect only after a restart or a sign-out says so.</summary>
    public string StatusText => Status == RunStatus.Succeeded && RunActions.FinishKey(Run) is { } finish
        ? Loc.Get(finish)
        : Loc.Get($"Activity.Status.{Status}");

    public string StatusTone => Status switch
    {
        RunStatus.Succeeded => RunActions.FinishKey(Run) is null ? Tone.Ok : Tone.Info,
        RunStatus.Incomplete => Tone.Warn,
        _ => Tone.Danger,
    };

    /// <summary>When it ran, and whether it can be undone.</summary>
    public string Subtitle => UndoText.Length > 0 ? $"{When} · {UndoText}" : When;

    public bool CanUndo => RunActions.CanUndo(Run);

    /// <summary>What Undo would and wouldn't put back, in a few words.</summary>
    public string UndoText => CanUndo
        ? Run.Apps.Count > 0 ? Loc.Get("Activity.UndoPartly") : Loc.Get("Activity.UndoFull")
        : Run.Apps.Count > 0 ? Loc.Get("Activity.UndoApps") : "";

    /// <summary>Each step in the user's language.</summary>
    public IReadOnlyList<StepLine> Lines
    {
        get
        {
            var lines = new List<StepLine>();
            lines.AddRange(Run.Apps.Select(a => new StepLine(Tone.Of(StepWords.Install(a.Status)),
                $"{RunActions.AppName(_services, a.AppId)} · {Loc.Get($"{Run.Operation switch { "apps-update" => "Update", "apps-uninstall" => "Uninstall", _ => "Install" }}.{a.Status}")}",
                a.Status is InstallStatus.Failed or InstallStatus.WingetUnavailable ? a.Detail : null, a.AppId, RunActions.AppName(_services, a.AppId))));
            lines.AddRange(Run.Config.Select(c => new StepLine(Tone.Of(StepWords.Config(c)), RunActions.ConfigLine(c),
                c.Status is ConfigImportStatus.Failed or ConfigImportStatus.ChangedSince ? c.Detail : null, c.PortId, c.AppName)));
            lines.AddRange(Run.WindowsSteps.Select(w => new StepLine(Tone.Of(StepWords.Windows(w.Status)),
                $"{RunActions.StepName(_services, w)} · {Loc.Get($"Step.{w.Status}")}", w.Ok ? null : w.Detail)));
            lines.AddRange(Run.SetupSteps.SelectMany(s => RunActions.AdjustmentLines(s).Select(a => new StepLine(Tone.Info, a))
                .Prepend(new StepLine(Tone.Of(RunActions.SetupStateWord(s.State)), RunActions.SetupStepLine(s)))));
            lines.AddRange(RunActions.FollowUps(_services, Run).Select(f => new StepLine(f.Tone, f.Text)));
            return lines;
        }
    }


    /// <summary>The exact changes (registry values, services, files backed up), for the technical view.</summary>
    public string Technical
    {
        get
        {
            var lines = new List<string> { $"{Run.Id} · {Run.Operation}" };
            foreach (var step in Run.WindowsSteps)
            {
                lines.Add($"{step.Status}: {step.Name} — {step.Detail}");
                lines.AddRange(step.Changes.Select(c => $"    {c.Target}: {c.Before ?? WindowsEvaluator.NotSet} -> {c.After ?? WindowsEvaluator.NotSet}"));
            }

            foreach (var config in Run.Config)
            {
                lines.Add($"{config.Status}: {config.AppName} ({config.Applied.Count} setting(s); backups: {string.Join(", ", config.Written.Select(w => w.BackupName))}) {config.Detail}");
            }

            foreach (var app in Run.Apps)
            {
                lines.Add($"{app.Status}: {app.AppId} ({app.WingetId}) {app.Detail}");
            }

            foreach (var step in Run.SetupSteps)
            {
                lines.Add($"{step.State}: {RunFollowUp.SetupStepName(step.Kind)} — {step.Detail}");
            }

            return string.Join("\n", lines);
        }
    }

    public void Relocalize() => Raise(string.Empty);
}

/// <summary>
/// Activity (redesign §2): the recorded runs, newest first, as plain sentences; each opens to its steps and what is left,
/// with the exact changes behind a further disclosure, and Undo where something can be put back. Records written by
/// earlier versions and the CLI are read the same way. The session log is at the bottom.
/// </summary>
public sealed class ActivityViewModel : Observable, IPageState
{
    private readonly Services _services;
    private bool _busy;
    private int _unreadable;

    public ActivityViewModel(Services services)
    {
        _services = services;
        OpenFolder = new RelayCommand(() =>
        {
            if (ShellLauncher.OpenFolder(_services.Store.HistoryFolder) is { } error)
            {
                Result.Show(Tone.Warn, error);
            }
        });
        Load();
        services.RunRecorded += _ => Load();
        services.ChangingChanged += () =>
        {
            foreach (var item in Items)
            {
                item.Undo.RaiseCanExecuteChanged();
            }
        };
        services.Logged += _ => Application.Current?.Dispatcher.BeginInvoke(() => Raise(nameof(SessionLog)));
        Loc.Instance.LanguageChanged += () =>
        {
            foreach (var item in Items)
            {
                item.Relocalize();
            }

            Raise(string.Empty);
        };
    }

    public ObservableCollection<ActivityItem> Items { get; } = [];

    public Banner Result { get; } = new();

    public RelayCommand OpenFolder { get; }

    public string SessionLog => _services.SessionLog;

    public string ProfileNote => _services.ProfileNote;

    public bool Empty => Items.Count == 0;

    /// <summary>While an undo runs every Undo is off; each one's state is asked again when it ends (the items may have been rebuilt meanwhile).</summary>
    private bool Busy
    {
        get => _busy;
        set
        {
            _busy = value;
            foreach (var item in Items)
            {
                item.Undo.RaiseCanExecuteChanged();
            }
        }
    }

    public string Status => Items.Count == 0 ? Loc.Get("Activity.Empty")
        : Loc.Plural("Activity.Count", Items.Count) + (_unreadable > 0 ? " · " + Loc.Plural("Activity.Unreadable", _unreadable) : "");

    public void Load()
    {
        var expanded = Items.Where(i => i.Expanded).Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        Items.Clear();
        _unreadable = 0;
        foreach (var id in _services.Store.ListRuns())
        {
            try
            {
                if (_services.Store.ReadRunRecord(id) is { } json)
                {
                    var run = SetupRun.FromJson(json);
                    ActivityItem? item = null;
                    var undo = new AsyncCommand(() => UndoAsync(item!), () => !_busy && !_services.Changing && RunActions.CanUndo(run), Fail);
                    item = new ActivityItem(_services, run, undo) { Expanded = expanded.Contains(id) };
                    Items.Add(item);
                }
            }
            catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException or ArgumentException)
            {
                _unreadable++;
                _services.Log($"History: {id} is unreadable ({e.Message}).");
            }
        }

        Raise(nameof(Status));
        Raise(nameof(Empty));
    }

    public void Leave() => Result.Settle();

    /// <summary>Opens one run (after a page's "Details").</summary>
    public void Show(string? runId)
    {
        foreach (var item in Items)
        {
            item.Expanded = item.Id == runId;
        }
    }

    private async Task UndoAsync(ActivityItem item)
    {
        var note = item.Run.Apps.Count > 0 ? Loc.Get("Activity.UndoAppsNote") : "";
        if (!ConfirmDialog.Ask(Loc.Get("Undo.ConfirmTitle"), Loc.Get("Undo.ConfirmMessage"), note: note, confirm: Loc.Get("Undo.Action")))
        {
            return;
        }

        Busy = true;
        try
        {
            if (await RunActions.UndoAndShowAsync(_services, item.Run, Result, Show) is { } undo)
            {
                Show(undo.Id);
            }
        }
        finally
        {
            Busy = false;
        }
    }

    private void Fail(Exception e)
    {
        Busy = false;
        _services.Log($"Activity: {e}");
        Result.Show(Tone.Danger, Loc.Get("Common.Unexpected"), technical: e.Message);
    }
}
