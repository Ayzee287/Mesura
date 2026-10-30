using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows.Data;
using Mesura.Core.Setup;
using Mesura.Core.Windows;

namespace Mesura.App;

/// <summary>
/// Debloat (0.2.0): what Windows ships or starts that you may not want, in three tabs: the packaged apps it bundles, its AI
/// features, and the programs that start with it. The first two are ordinary catalog entries (an app removal is a
/// catalog operation, an AI feature a policy), shown here instead of in Tweaks; the third is read from the Run keys and
/// the Startup folders and turned off with the flag Task Manager uses. Everything is chosen first and applied through the
/// same preview, run and Undo as the other pages. Nothing is removed that Windows depends on (<see cref="AppxSetting"/>).
/// </summary>
public sealed class DebloatViewModel : Observable, IPageState
{
    private const string Apps = "apps";
    private const string Ai = "ai";
    private const string Startup = "startup";
    private readonly Services _services;
    private readonly WindowsStateModel _state;
    private readonly Action<string?> _openActivity;
    private readonly ObservableCollection<SettingRow> _all = [];
    private readonly List<SettingRow> _startupRows = [];
    private string _tab = Apps;
    private string _search = "";
    private bool _onlyChangeable;
    private bool _busy;
    private bool _scanning;

    public DebloatViewModel(Services services, WindowsStateModel state, Action<string?> openActivity)
    {
        _services = services;
        _state = state;
        _openActivity = openActivity;
        View = new ListCollectionView(_all) { Filter = Matches };
        View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SettingRow.Category))); // one group per tab: the grid lays its cards out inside a group
        View.SortDescriptions.Add(new SortDescription(nameof(SettingRow.CanSelect), ListSortDirection.Descending)); // what is installed and can be removed first
        View.SortDescriptions.Add(new SortDescription(nameof(SettingRow.SortRank), ListSortDirection.Ascending));

        foreach (var row in state.Rows.Where(r => r.InDebloat))
        {
            _all.Add(row);
            row.PropertyChanged += OnRowChanged;
        }

        foreach (var value in new[] { Apps, Ai, Startup })
        {
            Tabs.Add(new FilterChoice(value, () => Loc.Get($"Debloat.Tab.{value}"), v =>
            {
                _tab = v;
                Refilter();
            }));
        }

        Tabs[0].IsSelected = true;
        Apply = new AsyncCommand(ApplyAsync, () => !_busy && !_services.Changing && _all.Any(r => r.Selected), Fail);
        Clear = new RelayCommand(() => SelectNone(), () => _all.Any(r => r.Selected));
        Rescan = new AsyncCommand(async () =>
        {
            await _state.RefreshAsync();
            await ScanStartupAsync();
        }, () => !_busy && !_scanning, Fail);
        OpenTaskManager = new RelayCommand(() =>
        {
            if (ShellLauncher.Open("ms-settings:startupapps") is { } error)
            {
                Result.Show(Tone.Warn, error);
            }
        });

        state.Updated += () =>
        {
            View.Refresh();
            Refilter();
        };
        services.ChangingChanged += RaiseCommands;
        services.RunRecorded += operation =>
        {
            if (operation is "windows-apply" or "windows-revert" or "undo" or "profile" or "setup")
            {
                _ = ScanStartupAsync();
            }
        };
        Loc.Instance.LanguageChanged += () =>
        {
            foreach (var tab in Tabs)
            {
                tab.Relocalize();
            }

            _ = ScanStartupAsync(); // the generated names are in the new language
            Raise(string.Empty);
        };
        _ = ScanStartupAsync();
    }

    public ListCollectionView View { get; }

    public ObservableCollection<FilterChoice> Tabs { get; } = [];

    public Banner Result { get; } = new();

    public AsyncCommand Apply { get; }

    public RelayCommand Clear { get; }

    public AsyncCommand Rescan { get; }

    public RelayCommand OpenTaskManager { get; }

    public bool InStartup => _tab == Startup;

    public bool Scanning
    {
        get => _scanning;
        private set
        {
            Set(ref _scanning, value);
            Rescan.RaiseCanExecuteChanged();
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

    public string Search
    {
        get => _search;
        set
        {
            Set(ref _search, value ?? "");
            Refilter();
        }
    }

    public bool OnlyChangeable
    {
        get => _onlyChangeable;
        set
        {
            Set(ref _onlyChangeable, value);
            Refilter();
        }
    }

    public bool Empty => View.Count == 0 && (_state.Loaded || InStartup);

    /// <summary>The chosen tab's headline count, for the page's subtitle.</summary>
    public string Status
    {
        get
        {
            if (!_state.Loaded && !InStartup)
            {
                return Loc.Get("State.Checking");
            }

            var rows = _all.Where(InTab).ToList();
            return Loc.Plural($"Debloat.Subtitle.{_tab}", rows.Count(r => r.CanSelect));
        }
    }

    public string SelectionSummary
    {
        get
        {
            var chosen = _all.Count(r => r.Selected);
            return chosen == 0 ? Loc.Get("Debloat.NothingSelected") : Loc.Plural("Optimize.Selected", chosen);
        }
    }

    public void Leave() => Result.Settle();

    private bool InTab(SettingRow row) => _tab switch
    {
        Apps => row.Operation.Category == WindowsCatalog.BundledAppsCategory,
        Ai => row.Operation.Category == WindowsCatalog.AiCategory,
        _ => row.Operation.Category == WindowsCatalog.StartupCategory,
    };

    private bool Matches(object item)
    {
        if (item is not SettingRow row || !InTab(row))
        {
            return false;
        }

        if (_onlyChangeable && !row.CanSelect)
        {
            return false;
        }

        var words = _search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.All(w => row.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase)
            || row.Summary.Contains(w, StringComparison.CurrentCultureIgnoreCase)
            || row.Id.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    private void Refilter()
    {
        View.Refresh();
        Raise(nameof(Empty));
        Raise(nameof(Status));
        Raise(nameof(InStartup));
        Raise(nameof(SelectionSummary));
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingRow.Selected) or "")
        {
            Raise(nameof(SelectionSummary));
            RaiseCommands();
        }
    }

    private void SelectNone()
    {
        foreach (var row in _all)
        {
            row.Selected = false;
        }
    }

    /// <summary>Reads what starts with Windows and turns each program into a row that can be turned off.</summary>
    private async Task ScanStartupAsync()
    {
        if (_scanning)
        {
            return;
        }

        Scanning = true;
        try
        {
            var items = await Task.Run(() => _services.StartupApps.Scan());
            foreach (var old in _startupRows)
            {
                old.PropertyChanged -= OnRowChanged;
                _all.Remove(old);
            }

            _startupRows.Clear();
            foreach (var item in items)
            {
                var limits = Loc.Get(item.Machine ? "Startup.LimitsMachine" : "Startup.LimitsUser");
                var op = item.ToOperation(Loc.Format("Startup.TurnOff", item.Name), item.Command, Loc.Format("Startup.Description", item.Name, item.Command), limits);
                var row = new SettingRow(_services, op, Recommendation.Optional);
                row.Update(new OperationEvaluation(op, item.Enabled ? OperationState.NotApplied : OperationState.Applied, "", []));
                row.PropertyChanged += OnRowChanged;
                _startupRows.Add(row);
                _all.Add(row);
            }

            Refilter();
        }
        finally
        {
            Scanning = false;
        }
    }

    private async Task ApplyAsync()
    {
        var rows = _all.Where(r => r.Selected).ToList();
        var ops = rows.Select(r => r.Operation).ToList();
        var notes = new List<string>();
        if (rows.Any(r => r.Operation.AppxSettings.Count > 0))
        {
            notes.Add(Loc.Get("Debloat.ConfirmApps"));
        }

        if (rows.Count(r => r.NeedsAdmin) is var admin and > 0)
        {
            notes.Add(Loc.Plural(Loc.AdminKey("Optimize.ConfirmAdmin"), admin));
        }

        if (!ConfirmDialog.Ask(Loc.Plural("Optimize.ConfirmTitle", rows.Count), Loc.Get("Optimize.ConfirmMessage"),
                [.. rows.Select(r => r.Name)], string.Join(" ", notes), Loc.Get("Optimize.Apply")))
        {
            return;
        }

        Busy = true;
        try
        {
            var (run, recorded, error) = await RunActions.ApplyWindowsAsync(_services, ops);
            var applied = run.WindowsSteps.Count(s => s.Status == WindowsOpStatus.Applied);
            var problems = run.WindowsSteps.Count(s => !s.Ok);
            var detail = string.Join("\n", RunActions.FollowUps(_services, run).Select(f => f.Text).Append(error ?? "").Append(_services.RestoreNote).Where(t => t.Length > 0));
            Result.Show(problems == 0 ? Tone.Ok : Tone.Warn,
                problems == 0 ? Loc.Plural("Optimize.ResultDone", applied) : Loc.Format("Optimize.ResultPartial", applied, problems), detail,
                recorded && error is null && RunActions.CanUndo(run) ? () => _ = UndoAsync(run) : null,
                recorded ? () => _openActivity(run.Id) : null, RunActions.TechnicalLines(run));
            if (!recorded)
            {
                await _state.RefreshAsync();
            }

            await ScanStartupAsync();
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task UndoAsync(SetupRun run)
    {
        if (_services.Changing || !ConfirmDialog.Ask(Loc.Get("Undo.ConfirmTitle"), Loc.Get("Undo.ConfirmMessage"), confirm: Loc.Get("Undo.Action")))
        {
            return;
        }

        Busy = true;
        try
        {
            await RunActions.UndoAndShowAsync(_services, run, Result, _openActivity);
            await ScanStartupAsync();
        }
        finally
        {
            Busy = false;
        }
    }

    private void Fail(Exception e)
    {
        Busy = false;
        _services.Log($"Debloat: {e}");
        Result.Show(Tone.Danger, Loc.Get("Common.Unexpected"), technical: e.Message);
    }

    private void RaiseCommands()
    {
        Apply.RaiseCanExecuteChanged();
        Clear.RaiseCanExecuteChanged();
        Rescan.RaiseCanExecuteChanged();
    }
}
