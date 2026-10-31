using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows.Data;
using Mesura.Core.Setup;
using Mesura.Core.Windows;

namespace Mesura.App;

/// <summary>A filter chip: a value, its label, and whether it is the chosen one.</summary>
public sealed class FilterChoice(string value, Func<string> label, Action<string> chosen) : Observable
{
    private bool _selected;

    public string Value { get; } = value;

    public string Label => label();

    /// <summary>What assistive technology reads for this item: its display name, never the type name.</summary>
    public override string ToString() => Label;

    public bool IsSelected
    {
        get => _selected;
        set
        {
            Set(ref _selected, value);
            if (value)
            {
                chosen(Value);
            }
        }
    }

    public void Relocalize() => Raise(nameof(Label));
}

/// <summary>A quick selection in All settings: a collection preset.</summary>
public sealed record CollectionChoice(string Name, string Summary, RelayCommand Select);

/// <summary>
/// A category in All settings' rail (an impact, or the guides): its label, how many entries it holds and how many of
/// them can be applied here now, and whether it is the chosen one.
/// </summary>
public sealed class RailChoice(string value, Func<string> label, Action<string> chosen) : Observable
{
    private bool _selected;
    private int _count;
    private int _available;

    public string Value { get; } = value;

    public string Label => label();

    /// <summary>What assistive technology reads for this item: its display name, never the type name.</summary>
    public override string ToString() => Label;

    public int Count
    {
        get => _count;
        set => Set(ref _count, value);
    }

    public int Available
    {
        get => _available;
        set => Set(ref _available, value);
    }

    public bool IsSelected
    {
        get => _selected;
        set
        {
            Set(ref _selected, value);
            if (value)
            {
                chosen(Value);
            }
        }
    }

    public void Relocalize() => Raise(nameof(Label));
}

/// <summary>
/// All settings (redesign §4, reorganised in the 2026-10-24 quality pass): every catalog entry, by impact category in a
/// rail with counts (guides and checks apart), filtered by recommendation level and searchable, with its status, risk
/// and benefit here, its details on demand, and the collection presets as quick selections. Inside a category, what can
/// act here comes first, advanced entries next, entries that don't apply to this PC last. Applying uses the same run as
/// Setup.
/// </summary>
public sealed class AllSettingsViewModel : Observable, IPageState, IPageBack
{
    private const string Guides = "guides";
    private readonly Services _services;
    private readonly WindowsStateModel _state;
    private readonly Action<string?> _openActivity;
    private string _search = "";
    private string _recommendation = "all";
    private string _rail = "all";
    private bool _onlyChangeable;
    private SettingRow? _selectedRow;
    private bool _busy;

    public AllSettingsViewModel(Services services, WindowsStateModel state, Action back, Action<string?> openActivity)
    {
        _services = services;
        _state = state;
        _openActivity = openActivity;
        View = new ListCollectionView(state.Rows);
        View.Filter = Matches;
        Regroup();

        foreach (var value in new[] { "all", "Recommended", "Optional" })
        {
            RecommendationFilters.Add(new FilterChoice(value, () => value == "all" ? Loc.Get("Filter.AllLevels") : Loc.Get($"Recommendation.{value}"), v =>
            {
                _recommendation = v;
                Refilter();
            }));
        }

        foreach (var value in new[] { "all" }.Concat(Enum.GetNames<OperationImpact>()).Append(Guides))
        {
            Rail.Add(new RailChoice(value, () => value switch
            {
                "all" => Loc.Get("AllSettings.AllCategories"),
                Guides => Loc.Get("AllSettings.Guides"),
                _ => Loc.Get($"Impact.{value}"),
            }, v =>
            {
                _rail = v;
                Regroup();
                Refilter();
            }));
        }

        RecommendationFilters[0].IsSelected = true;
        Rail[0].IsSelected = true;
        Recount();

        if (state.Catalog is { } catalog)
        {
            foreach (var preset in catalog.Presets.Where(p => p.Tier is PresetTier.Collection or PresetTier.Specialized))
            {
                Collections.Add(new CollectionChoice(Loc.Name(preset), Loc.Summary(preset), new RelayCommand(() => SelectOnly(preset.OperationIds))));
            }
        }

        Back = new RelayCommand(back);
        Apply = new AsyncCommand(ApplyAsync, () => !_busy && !_services.Changing && state.Rows.Any(r => r.Selected && !r.InDebloat), Fail);
        Clear = new RelayCommand(() => SelectOnly([]), () => state.Rows.Any(r => r.Selected && !r.InDebloat));
        OpenTarget = new RelayCommand(() =>
        {
            if (SelectedRow?.OpenTarget is { } target && ShellLauncher.Open(target) is { } error)
            {
                Result.Show(Tone.Warn, error);
            }
        }, () => SelectedRow?.OpenTarget is not null);

        foreach (var row in state.Rows)
        {
            row.PropertyChanged += OnRowChanged;
        }

        state.Updated += () =>
        {
            View.Refresh(); // the sort rank follows the state (what doesn't apply here moves down)
            Recount();
            Refilter();
            Raise(nameof(SelectedRow));
        };
        services.ChangingChanged += RaiseCommands;
        Loc.Instance.LanguageChanged += () =>
        {
            foreach (var chip in RecommendationFilters)
            {
                chip.Relocalize();
            }

            foreach (var item in Rail)
            {
                item.Relocalize();
            }

            Collections.Clear();
            if (state.Catalog is { } c)
            {
                foreach (var preset in c.Presets.Where(p => p.Tier is PresetTier.Collection or PresetTier.Specialized))
                {
                    Collections.Add(new CollectionChoice(Loc.Name(preset), Loc.Summary(preset), new RelayCommand(() => SelectOnly(preset.OperationIds))));
                }
            }

            View.Refresh(); // the group headers are the categories in the new language
            Raise(string.Empty);
        };
    }

    public ListCollectionView View { get; }

    public ObservableCollection<FilterChoice> RecommendationFilters { get; } = [];

    /// <summary>The categories: all, each impact, and the guides and checks.</summary>
    public ObservableCollection<RailChoice> Rail { get; } = [];

    /// <summary>The chosen category's name and what in it can be applied here, for the list's header.</summary>
    public string RailTitle => Searching ? Loc.Get("AllSettings.SearchResults") : Rail.FirstOrDefault(r => r.Value == _rail)?.Label ?? "";

    public string RailSummary
    {
        get
        {
            if (!_state.Loaded)
            {
                return Loc.Get("State.Checking");
            }

            if (Searching)
            {
                return Loc.Format("AllSettings.SearchSummary", View.Cast<object>().Count());
            }

            var rows = _state.Rows.Where(InRail).ToList();
            return _rail == Guides
                ? Loc.Get("AllSettings.GuidesSummary")
                : Loc.Format("AllSettings.CategorySummary", rows.Count(r => r.CanSelect), rows.Count(r => r.State == OperationState.Applied));
        }
    }

    /// <summary>Whether the rail's category is the guides (no ticks there: each opens its steps or the Windows page).</summary>
    public bool InGuides => _rail == Guides && !Searching;

    public ObservableCollection<CollectionChoice> Collections { get; } = [];

    public Banner Result { get; } = new();

    public RelayCommand Back { get; }

    public AsyncCommand Apply { get; }

    public RelayCommand Clear { get; }

    public RelayCommand OpenTarget { get; }

    public string Search
    {
        get => _search;
        set
        {
            var was = Searching;
            Set(ref _search, value ?? "");
            if (Searching != was)
            {
                Regroup(); // results from every category are grouped by category; a category on its own is flat
            }

            Refilter();
        }
    }

    /// <summary>Whether a search is running: then it covers every category and level, not only the ones chosen on the left and above.</summary>
    public bool Searching => _search.Trim().Length > 0;

    public bool OnlyChangeable
    {
        get => _onlyChangeable;
        set
        {
            Set(ref _onlyChangeable, value);
            Refilter();
        }
    }

    public SettingRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            Set(ref _selectedRow, value);
            Raise(nameof(HasSelection));
            OpenTarget.RaiseCanExecuteChanged();
        }
    }

    public bool HasSelection => _selectedRow is not null;

    /// <summary>Back first closes the open details drawer.</summary>
    public bool CloseOpenDetail()
    {
        if (_selectedRow is null)
        {
            return false;
        }

        SelectedRow = null;
        return true;
    }

    public string CountText => Loc.Plural("AllSettings.Shown", View.Count);

    public bool Empty => View.Count == 0 && _state.Loaded;

    public string SelectionSummary
    {
        get
        {
            var chosen = _state.Rows.Where(r => r.Selected && !r.InDebloat).ToList();
            if (chosen.Count == 0)
            {
                return Loc.Get("AllSettings.NothingSelected");
            }

            var hidden = chosen.Count(r => !Matches(r));
            var text = Loc.Plural("Optimize.Selected", chosen.Count);
            return hidden > 0 ? $"{text} · {Loc.Plural("AllSettings.Hidden", hidden)}" : text;
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

    /// <summary>Whether a row belongs to the rail's category (guides only in their own).</summary>
    private bool InRail(SettingRow row) => !row.InDebloat && _rail switch
    {
        Guides => row.Recommendation == Recommendation.Guide,
        "all" => row.Recommendation != Recommendation.Guide,
        _ => row.Recommendation != Recommendation.Guide && row.Impact.ToString() == _rail,
    };

    private bool Matches(object item)
    {
        if (item is not SettingRow row || row.InDebloat)
        {
            return false;
        }

        if (_onlyChangeable && !row.CanSelect)
        {
            return false;
        }

        var words = _search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            if (!InRail(row))
            {
                return false;
            }

            return _rail == Guides || _recommendation == "all" || row.Recommendation.ToString() == _recommendation;
        }

        // A search looks everywhere (the guides included) and in what the card shows: its name, description and category, and the English name for
        // those who search in English on a Russian screen. Never the internal id: it is not on the card, and a short query such as "p" matched almost everything
        // through it. A one- or two-letter word must start a word, and a single letter is looked for in the name only (in the description and category it would match nearly everything); longer ones may be inside a word.
        return words.All(w => Hit(row.Name, w) || (w.Length >= 2 && (Hit(row.Summary, w) || Hit(row.Category, w) || Hit(row.Operation.Name, w))));
    }

    private static bool Hit(string text, string word)
    {
        if (word.Length >= 3)
        {
            return text.Contains(word, StringComparison.CurrentCultureIgnoreCase);
        }

        for (var at = text.IndexOf(word, StringComparison.CurrentCultureIgnoreCase); at >= 0; at = text.IndexOf(word, at + 1, StringComparison.CurrentCultureIgnoreCase))
        {
            if (at == 0 || !char.IsLetterOrDigit(text[at - 1]))
            {
                return true;
            }
        }

        return false;
    }

    private void Refilter()
    {
        View.Refresh();
        Raise(nameof(CountText));
        Raise(nameof(Empty));
        Raise(nameof(SelectionSummary));
        Raise(nameof(RailTitle));
        Raise(nameof(RailSummary));
        Raise(nameof(Searching));
        Raise(nameof(InGuides));
    }

    /// <summary>
    /// Groups the list: by group when every category is shown, flat inside one group; the catalog's order with the sort rank first.
    /// </summary>
    private void Regroup()
    {
        using (View.DeferRefresh())
        {
            View.GroupDescriptions.Clear();
            View.SortDescriptions.Clear();
            if (_rail == "all" || Searching)
            {
                View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SettingRow.ImpactText)));
                View.SortDescriptions.Add(new SortDescription(nameof(SettingRow.ImpactOrder), ListSortDirection.Ascending));
            }
            // Inside one group the list is flat (0.4): a second level of headings (Taskbar and Start, File Explorer…) said nothing the names don't.

            View.SortDescriptions.Add(new SortDescription(nameof(SettingRow.SortRank), ListSortDirection.Ascending));
            View.SortDescriptions.Add(new SortDescription(nameof(SettingRow.CatalogIndex), ListSortDirection.Ascending));
        }
    }

    /// <summary>Each category's entries and what of it can be applied here now.</summary>
    private void Recount()
    {
        foreach (var item in Rail)
        {
            var rows = _state.Rows.Where(r => !r.InDebloat).Where(r => item.Value switch
            {
                Guides => r.Recommendation == Recommendation.Guide,
                "all" => r.Recommendation != Recommendation.Guide,
                var impact => r.Recommendation != Recommendation.Guide && r.Impact.ToString() == impact,
            }).ToList();
            item.Count = rows.Count;
            item.Available = rows.Count(r => r.CanSelect);
        }
    }

    public void Leave() => Result.Settle();

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingRow.Selected) or "")
        {
            Raise(nameof(SelectionSummary));
            RaiseCommands();
        }
    }

    private void SelectOnly(IReadOnlyCollection<string> ids)
    {
        var set = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _state.Rows.Where(r => !r.InDebloat))
        {
            row.Selected = set.Contains(row.Id) && row.CanSelect;
        }

        if (ids.Count > 0)
        {
            var ticked = _state.Rows.Count(r => r.Selected && !r.InDebloat);
            Result.Show(ticked == 0 ? Tone.Ok : Tone.Neutral,
                ticked == 0 ? Loc.Get("AllSettings.CollectionDone") : Loc.Plural("AllSettings.CollectionSelected", ticked));
        }
    }

    private async Task ApplyAsync()
    {
        var rows = _state.Rows.Where(r => r.Selected && !r.InDebloat).ToList();
        var ops = rows.Select(r => r.Operation).ToList();
        if (RunActions.Conflicts(_services, ops) is { Count: > 0 } conflicts)
        {
            Result.Show(Tone.Danger, Loc.Get("Optimize.Conflict"), string.Join("\n", conflicts));
            return;
        }

        var notes = new List<string>();
        if (rows.Count(r => r.NeedsRestart) is var restart and > 0)
        {
            notes.Add(Loc.Plural("Optimize.ConfirmRestart", restart));
        }

        if (rows.Any(r => r.Risk == RiskLevel.High))
        {
            notes.Add(Loc.Format("AllSettings.ConfirmHigherRisk", RunActions.Names([.. rows.Where(r => r.Risk == RiskLevel.High).Select(r => r.Name)])));
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
            var detail = string.Join("\n", RunActions.FollowUps(_services, run).Select(f => f.Text).Append(error ?? "").Where(t => t.Length > 0));
            Result.Show(problems == 0 && _services.RestoreNote.Length == 0 ? Tone.Ok : Tone.Warn,
                problems == 0 ? Loc.Plural("Optimize.ResultDone", applied) : Loc.Format("Optimize.ResultPartial", applied, problems), detail,
                recorded && error is null && RunActions.CanUndo(run) ? () => _ = UndoAsync(run) : null,
                recorded ? () => _openActivity(run.Id) : null, RunActions.Technical(_services, run), _services.RestoreNote);
            if (!recorded)
            {
                await _state.RefreshAsync();
            }
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
        }
        finally
        {
            Busy = false;
        }
    }

    private void Fail(Exception e)
    {
        Busy = false;
        _services.Log($"All settings: {e}");
        Result.Show(Tone.Danger, Loc.Get("Common.Unexpected"), technical: e.Message);
    }

    private void RaiseCommands()
    {
        Apply.RaiseCanExecuteChanged();
        Clear.RaiseCanExecuteChanged();
    }
}
