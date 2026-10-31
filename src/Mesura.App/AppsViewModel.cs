using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Media;
using Mesura.Core.Apps;
using Mesura.Core.Config;
using Mesura.Core.Setup;
using Mesura.Setup.Windows.Workflow;

namespace Mesura.App;

/// <summary>
/// A customisation of an app, offered on that app's card rather than as an app of its own (D031): Vencord on Discord.
/// It can be ticked when its app is installed or ticked too, and isn't installed yet.
/// </summary>
public sealed class AppMod(AppPackage package, AppItem parent, Func<bool> wingetMissing) : Observable
{
    private bool _selected;
    private InstallState _state = InstallState.Unknown;
    private bool _checking = true;

    public AppPackage Package { get; } = package;

    public InstallState State => _state;

    public string Name => Package.Name;

    /// <summary>What assistive technology reads for this item: its display name, never the type name.</summary>
    public override string ToString() => Name;

    public string Note => Loc.AppNote(Package.Id, Package.Note);

    public bool CanSelect => !_checking && !wingetMissing() && _state != InstallState.Installed
        && (parent.State == InstallState.Installed || parent.Selected);

    public bool Selected
    {
        get => _selected;
        set => Set(ref _selected, value && CanSelect);
    }

    public string StateText => _checking ? Loc.Get("State.Checking") : _state switch
    {
        InstallState.Installed => Loc.Get("Apps.ModInstalled"),
        InstallState.NotInstalled => Loc.Get("Apps.ModNotInstalled"),
        _ => Loc.Get("Apps.Unknown"),
    };

    public string StateTone => _checking ? Tone.Muted : _state == InstallState.Installed ? Tone.Ok : Tone.Neutral;

    public void Update(InstallState state)
    {
        _state = state;
        _checking = false;
        Refresh();
    }

    /// <summary>Re-reads whether it can be ticked (its app was ticked or unticked); a tick it loses is announced as such.</summary>
    public void Refresh()
    {
        var lost = _selected && !CanSelect;
        if (lost)
        {
            _selected = false;
        }

        Raise(string.Empty);
        if (lost)
        {
            Raise(nameof(Selected));
        }
    }
}

/// <summary>A package Setup's Apps card shows: an app or a customisation, by its own name and icon (<see cref="AppGlyph"/>).</summary>
public sealed record PlanAppChip(string Name, string Id);

/// <summary>One app in the Apps list: its icon, its state here, whether it is ticked, and its customisations.</summary>
public sealed class AppItem : Observable
{
    private readonly Func<bool> _wingetMissing;
    private bool _selected;
    private InstallState _state = InstallState.Unknown;
    private bool _checking = true;

    public AppItem(AppPackage package, Func<bool> wingetMissing, Func<AppPackage, bool> canDetect, Func<bool>? elevated = null)
    {
        Package = package;
        _elevated = elevated ?? (() => false);
        _wingetMissing = wingetMissing;
        Detectable = canDetect(package);
    }

    private readonly Func<bool> _elevated;

    public AppPackage Package { get; }

    public bool Detectable { get; }

    public InstallState State => _state;

    public string Name => Package.Name;

    /// <summary>What assistive technology reads for this item: its display name, never the type name.</summary>
    public override string ToString() => Name;

    public string Category => Loc.Category(Package.Category);

    /// <summary>Customisations offered on this card (Vencord on Discord).</summary>
    public ObservableCollection<AppMod> Mods { get; } = [];

    public bool HasMods => Mods.Count > 0;

    /// <summary>
    /// The app's note, which says what an install would do (empty once the app is installed and Mesura carries its settings, and for an app without a
    /// note: the card then has no tooltip).
    /// </summary>
    public string Note => !_checking && _state == InstallState.Installed && Port is not null ? "" : Loc.AppNote(Package.Id, Package.Note);

    /// <summary>The config port that sets this app up after an install, if Mesura has one.</summary>
    public ConfigPortDefinition? Port => ConfigCatalog.ById(Package.Id);

    public bool HasDefaults => Port is not null && ConfigDefaults.Has(Port.Id);

    /// <summary>Manual entries and installed apps can't be ticked, and nothing installs without winget.</summary>
    public bool CanSelect => !_checking && Package.Source != AppSource.Manual && !_wingetMissing() && _state != InstallState.Installed;

    public bool Selected
    {
        get => _selected;
        set
        {
            Set(ref _selected, value && CanSelect);
            foreach (var mod in Mods)
            {
                mod.Refresh();
            }
        }
    }

    /// <summary>The state in one word; for an installed app with a newer version it is the action itself: "Update".</summary>
    public string StateText => _checking ? Loc.Get("State.Checking") : _state switch
    {
        InstallState.Installed when UpdateAvailable => Loc.Get("Apps.Update"),
        InstallState.Installed => Loc.Get("Apps.Installed"),
        InstallState.NotInstalled => Loc.Get("Apps.NotInstalled"),
        _ when _wingetMissing() => Loc.Get("Apps.NoWinget"),
        _ => Loc.Get("Apps.Unknown"),
    };

    public string StateTone => _checking ? Tone.Muted : _state switch
    {
        InstallState.Installed when UpdateAvailable => Tone.Accent,
        InstallState.Installed => Tone.Ok,
        InstallState.NotInstalled => Tone.Neutral,
        _ => Tone.Warn,
    };

    /// <summary>Whether the card shows an uninstall button: an installed app whose uninstall Mesura has verified (never the others, so no button that might do nothing).</summary>
    public bool SupportsUninstall => !_checking && _state == InstallState.Installed && Package.Uninstallable;

    /// <summary>Uninstalling needs administrator rights; without them the button is shown but disabled, and says why.</summary>
    public bool CanUninstall => SupportsUninstall && _elevated();

    public string UninstallTip => _elevated() ? Loc.Format("Apps.UninstallTip", Name) : Loc.Get(Loc.AdminKey("Apps.UninstallNeedsAdmin"));

    public string UninstallAutomationName => _elevated() ? Loc.Format("Apps.UninstallAutomation", Name) : Loc.Format("Apps.UninstallAutomationNeedsAdmin", Name);

    private UpdateCheck? _update;

    /// <summary>Whether winget offers a newer version of this installed app (only asked for the apps Mesura updates, so the action never shows for the others).</summary>
    public bool UpdateAvailable => !_checking && _state == InstallState.Installed && _update is { State: UpdateState.Available };

    /// <summary>The state pill is plain text unless it is the Update action (the card draws one or the other).</summary>
    public bool NoUpdate => !UpdateAvailable;

    /// <summary>What the update would do, for the tooltip.</summary>
    public string UpdateTip => _update is { InstalledVersion: { } from, AvailableVersion: { } to } ? Loc.Format("Apps.UpdateTip", from, to) : Loc.Get("Apps.UpdateTipPlain");

    /// <summary>The name assistive technology reads for the button, which names its app (a row of identical "Update" buttons says nothing).</summary>
    public string UpdateAutomationName => _update?.AvailableVersion is { } to ? Loc.Format("Apps.UpdateAutomationTo", Name, to) : Loc.Format("Apps.UpdateAutomation", Name);

    /// <summary>One line of the confirmation: the app and, when known, the versions.</summary>
    public string UpdateLine => _update is { InstalledVersion: { } from, AvailableVersion: { } to } ? Loc.Format("Apps.UpdateLine", Name, from, to) : Name;

    public void SetUpdate(UpdateCheck? check)
    {
        _update = check;
        Raise(nameof(UpdateAvailable));
        Raise(nameof(UpdateTip));
        Raise(nameof(UpdateAutomationName));
        Raise(nameof(UpdateLine));
    }

    public void Update(InstallState state)
    {
        _state = state;
        _checking = false;
        if (state != InstallState.Installed)
        {
            _update = null; // nothing installed, nothing to update
        }

        if (!CanSelect)
        {
            _selected = false;
        }

        Raise(string.Empty);
        foreach (var mod in Mods)
        {
            mod.Refresh();
        }
    }

    public void Relocalize()
    {
        Raise(string.Empty);
        foreach (var mod in Mods)
        {
            mod.Refresh();
        }
    }
}

/// <summary>
/// Apps (redesign §5, D031): a searchable list of the apps Mesura offers, each with its own icon, with category chips;
/// tick the ones you want, choose how they are set up afterwards (Mesura's defaults, your saved configuration, or not at
/// all), and install them as one run of the setup engine that Setup uses too. Customisations (Vencord) sit on their app's
/// card. No bundles: profiles still read them. What is ticked here is also the Apps card of Setup's plan.
/// </summary>
public sealed class AppsViewModel : Observable, IPageState
{
    private readonly Services _services;
    private readonly Action<string?> _openActivity;
    private string _search = "";
    private string _category = "all";
    private bool _busy;
    private bool _wingetMissing;
    private bool _loaded;
    private string _progress = "";
    private CancellationTokenSource? _cancel;

    public AppsViewModel(Services services, Action<string?> openActivity)
    {
        _services = services;
        _openActivity = openActivity;
        View = new ListCollectionView(Items) { Filter = Matches };

        if (services.Apps.Catalog is { } catalog)
        {
            foreach (var package in catalog.Packages.Where(p => p.Listed && p.Customizes.Length == 0))
            {
                var item = new AppItem(package, () => _wingetMissing, services.Installer.CanDetect, () => services.Reader.IsElevated);
                item.PropertyChanged += OnItemChanged;
                foreach (var mod in catalog.Packages.Where(p => p.Customizes.Equals(package.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    var choice = new AppMod(mod, item, () => _wingetMissing);
                    choice.PropertyChanged += OnItemChanged;
                    item.Mods.Add(choice);
                }

                Items.Add(item);
            }

            foreach (var category in new[] { "all" }.Concat(Items.Select(i => i.Package.Category).Distinct()))
            {
                Categories.Add(new FilterChoice(category, () => category == "all" ? Loc.Get("Filter.All") : Loc.Category(category), c =>
                {
                    _category = c;
                    View.Refresh();
                    Raise(nameof(Empty));
                }));
            }

            Categories[0].IsSelected = true;
        }

        Refresh = new AsyncCommand(DetectAsync, () => !_busy, Fail);
        Install = new AsyncCommand(InstallAsync, () => !_busy && !_services.Changing && Chosen().Count > 0, Fail);
        UpdateApp = new AsyncCommand<AppItem>(item => UpdateAsync(item is null ? [] : [item]), item => !_busy && !_services.Changing && item?.UpdateAvailable == true, Fail);
        UninstallApp = new AsyncCommand<AppItem>(item => UninstallAsync(item), item => !_busy && !_services.Changing && item?.CanUninstall == true, Fail);
        UpdateAll = new AsyncCommand(() => UpdateAsync([.. Items.Where(i => i.UpdateAvailable)]), () => !_busy && !_services.Changing && UpdatesCount > 0, Fail);
        Cancel = new RelayCommand(() =>
        {
            _cancel?.Cancel();
            Progress = Loc.Get("Apps.Cancelling");
            Cancel!.RaiseCanExecuteChanged();
        }, () => _cancel is { IsCancellationRequested: false });
        GetWinget = new RelayCommand(() => ShellLauncher.Open("https://aka.ms/getwinget"));
        ClearSelection = new RelayCommand(() =>
        {
            foreach (var item in Items)
            {
                item.Selected = false;
                foreach (var mod in item.Mods)
                {
                    mod.Selected = false;
                }
            }
        }, () => Chosen().Count > 0);

        services.ChangingChanged += RaiseCommands;
        services.RunRecorded += operation =>
        {
            if (operation is "profile" or "setup" && !_busy)
            {
                Refresh.Execute(null);
            }
        };
        Loc.Instance.LanguageChanged += () =>
        {
            foreach (var item in Items)
            {
                item.Relocalize();
            }

            foreach (var chip in Categories)
            {
                chip.Relocalize();
            }

            Raise(string.Empty);
        };
        Refresh.Execute(null);
    }

    public ObservableCollection<AppItem> Items { get; } = [];

    public ListCollectionView View { get; }

    public ObservableCollection<FilterChoice> Categories { get; } = [];

    public Banner Result { get; } = new();

    public void Leave() => Result.Settle();

    public AsyncCommand Refresh { get; }

    public AsyncCommand Install { get; }

    /// <summary>Updates one app through winget (the button on its card).</summary>
    public AsyncCommand<AppItem> UpdateApp { get; }

    /// <summary>Uninstalls one app through winget (the button on its card); only for the apps Mesura has verified, and only as administrator.</summary>
    public AsyncCommand<AppItem> UninstallApp { get; }

    /// <summary>Updates every app that has an update on offer.</summary>
    public AsyncCommand UpdateAll { get; }

    public int UpdatesCount => Items.Count(i => i.UpdateAvailable);

    public bool HasUpdates => UpdatesCount > 0;

    public string UpdateAllText => Loc.Format("Apps.UpdateAll", UpdatesCount);

    public RelayCommand Cancel { get; }

    public RelayCommand GetWinget { get; }

    public RelayCommand ClearSelection { get; }

    /// <summary>Raised when what is ticked changes (Setup shows the same choice on its Apps card).</summary>
    public event Action? SelectionChanged;

    /// <summary>What Apps says about the app catalog in use: an edited copy, or one set aside (empty for the built-in one).</summary>
    public string CatalogNote => _services.AppsCatalogNote.Text;

    public string CatalogNoteTone => _services.AppsCatalogNote.Tone;

    public string Search
    {
        get => _search;
        set
        {
            Set(ref _search, value ?? "");
            View.Refresh();
            Raise(nameof(Empty));
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

    public bool Installing => _cancel is not null;

    public bool WingetMissing => _wingetMissing;

    /// <summary>Why winget can't be used: refused for this account (with the reason), or simply not there.</summary>
    public string WingetMissingText => _services.Installer.WingetRefusal is { Length: > 0 } refusal
        ? Loc.Format("Apps.WingetRefused", RefusalReason(refusal))
        : Loc.Get("Apps.WingetMissing");

    /// <summary>
    /// The reason winget is refused (<c>WingetContext</c>'s English sentence) in the user's language: one of its three
    /// cases, or a general sentence (the original goes to the session log).
    /// </summary>
    private string RefusalReason(string refusal)
    {
        var key = refusal.Contains("couldn't tell which user", StringComparison.Ordinal) ? "Winget.Refused.UserUnknown"
            : refusal.Contains("shell isn't reachable", StringComparison.Ordinal) ? "Winget.Refused.ShellUnreachable"
            : refusal.Contains("another account", StringComparison.Ordinal) ? "Winget.Refused.OtherAccount"
            : null;
        if (key is null)
        {
            _services.Log($"Apps: winget is refused here: {refusal}");
        }

        return Loc.Get(key ?? "Winget.Refused.Other");
    }

    /// <summary>Getting App Installer helps only when winget is missing, not when it was refused.</summary>
    public bool CanGetWinget => _services.Installer.WingetRefusal.Length == 0;

    public bool Empty => View.Count == 0;

    public string Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    public string Status
    {
        get
        {
            if (_services.Apps.Catalog is null)
            {
                return Loc.Format("Apps.CatalogFailed", _services.Apps.Error ?? "");
            }

            if (!_loaded)
            {
                return Loc.Get("Apps.Checking");
            }

            var installed = Items.Count(i => i.State == InstallState.Installed);
            var text = Loc.Format("Apps.Status", installed, Items.Count - installed);
            return UpdatesCount > 0 ? $"{text} · {Loc.Plural("Apps.StatusUpdates", UpdatesCount)}" : text;
        }
    }

    /// <summary>
    /// The packages ticked to install, in catalog order (an app before its customisation, so Discord installs before
    /// Vencord patches it).
    /// </summary>
    public IReadOnlyList<AppPackage> Chosen()
    {
        var ids = Items.Where(i => i.Selected).Select(i => i.Package.Id)
            .Concat(Items.SelectMany(i => i.Mods).Where(m => m.Selected).Select(m => m.Package.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Apps first, then customisations, each in catalog order: an edited catalog may list a customisation first.
        return _services.Apps.Catalog is { } catalog
            ? [.. catalog.Packages.Where(p => ids.Contains(p.Id)).OrderBy(p => p.Customizes.Length > 0)]
            : [];
    }

    /// <summary>What is ticked, one chip per package (a customisation under its own name), for Setup's Apps card.</summary>
    public IReadOnlyList<PlanAppChip> ChosenChips() =>
        [.. Items.Where(i => i.Selected).Select(i => new PlanAppChip(i.Name, i.Package.Id))
            .Concat(Items.SelectMany(i => i.Mods).Where(m => m.Selected).Select(m => new PlanAppChip(m.Name, m.Package.Id)))];

    // ---- Set-up source -----------------------------------------------------------------------------------------------

    public bool SavedAvailable => _services.Settings.SavedConfiguration is { } path && File.Exists(path);

    public string SavedHint => SavedAvailable ? Path.GetFileName(_services.Settings.SavedConfiguration!) : Loc.Get("Apps.NoSavedConfiguration");

    /// <summary>The chosen set-up source in words, for confirmations and Setup's Apps card.</summary>
    public string SourceText => SourceName(_services.Settings.ConfigureAfterInstall == "saved" && !SavedAvailable ? "none" : _services.Settings.ConfigureAfterInstall);

    private static string SourceName(string source) => source switch
    {
        "none" => Loc.Get("Apps.SourceNoneLine"),
        "saved" => Loc.Get("Apps.ChoiceCustom"),
        _ => Loc.Get("Apps.ChoiceMesura"),
    };

    /// <summary>Remembers the source chosen for an install: Setup (Optimize) installs apps from the same one afterwards.</summary>
    private void RememberSource(string source)
    {
        if (_services.Settings.ConfigureAfterInstall == source)
        {
            return;
        }

        _services.Settings.ConfigureAfterInstall = source;
        _services.SaveSettings();
        Raise(nameof(SourceText));
        SelectionChanged?.Invoke();
    }

    /// <summary>Re-reads whether the saved configuration is there (Configurations may have changed it).</summary>
    public void RefreshSaved()
    {
        Raise(nameof(SavedAvailable));
        Raise(nameof(SavedHint));
        Raise(nameof(SourceText));
        Raise(nameof(SelectionSummary));
        SelectionChanged?.Invoke();
    }

    /// <summary>How many of these packages would be set up afterwards (they have a config port in the chosen bundle).</summary>
    public int WillConfigure(IEnumerable<AppPackage> packages)
    {
        var (bundle, _) = _services.AfterInstallBundle();
        return bundle is null ? 0 : packages.Count(p => ConfigCatalog.ById(p.Id) is { } port && bundle.Apps.ContainsKey(port.Id));
    }

    public string SelectionSummary
    {
        get
        {
            var chosen = Chosen();
            if (chosen.Count == 0)
            {
                return _loaded ? Loc.Get("Apps.NothingSelected") : "";
            }

            return Loc.Plural("Apps.Selected", chosen.Count);
        }
    }

    private bool Matches(object item)
    {
        if (item is not AppItem app)
        {
            return false;
        }

        if (_category != "all" && app.Package.Category != _category)
        {
            return false;
        }

        var words = _search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.All(w => app.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase)
            || app.Category.Contains(w, StringComparison.CurrentCultureIgnoreCase)
            || app.Package.WingetId.Contains(w, StringComparison.OrdinalIgnoreCase)
            || app.Mods.Any(m => m.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase)));
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppItem.Selected))
        {
            return;
        }

        Raise(nameof(SelectionSummary));
        RaiseCommands();
        SelectionChanged?.Invoke();
    }

    private async Task DetectAsync()
    {
        if (_services.Apps.Catalog is null)
        {
            Raise(nameof(Status));
            return;
        }

        IReadOnlyList<AppPlanItem>? updatePlan = null;
        Busy = true;
        try
        {
            var packages = Items.Select(i => i.Package).Concat(Items.SelectMany(i => i.Mods).Select(m => m.Package)).ToList();
            var plan = await Task.Run(() => _services.Installer.BuildPlan(packages));
            var wingetItems = plan.Where(p => p.Package.Detectable && p.Package.Source != AppSource.Manual).ToList();
            _wingetMissing = wingetItems.Count > 0 && wingetItems.All(p => p.State == InstallState.Unknown);
            foreach (var item in Items)
            {
                item.Update(plan.First(p => p.Package.Id == item.Package.Id).State);
                foreach (var mod in item.Mods)
                {
                    mod.Update(plan.First(p => p.Package.Id == mod.Package.Id).State);
                }
            }

            _loaded = true;
            _services.Log($"Apps: {Items.Count(i => i.State == InstallState.Installed)} installed of {Items.Count} listed ({Services.Describe(_services.Apps.Source)}).");
            Raise(nameof(WingetMissing));
            Raise(nameof(WingetMissingText));
            Raise(nameof(CanGetWinget));
            Raise(nameof(Status));
            Raise(nameof(SelectionSummary));
            SelectionChanged?.Invoke();
            updatePlan = plan;
        }
        finally
        {
            Busy = false;
        }

        if (updatePlan is not null)
        {
            await CheckUpdatesAsync(updatePlan);
        }
    }

    /// <summary>
    /// Asks winget which installed apps have a newer version, after the states are on screen (it is one more winget call per app Mesura
    /// updates, so it never delays the list). A failed question leaves the app without an update action, never with a wrong one.
    /// </summary>
    private async Task CheckUpdatesAsync(IReadOnlyList<AppPlanItem> plan)
    {
        IReadOnlyList<UpdateCheck> checks;
        try
        {
            checks = await Task.Run(() => _services.Installer.FindUpdates(plan));
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _services.Log($"Apps: the update check failed: {e.Message}");
            checks = [];
        }

        foreach (var item in Items)
        {
            item.SetUpdate(checks.FirstOrDefault(c => c.Package.Id == item.Package.Id));
        }

        _services.Log($"Apps: {checks.Count(c => c.State == UpdateState.Available)} update(s) available among {checks.Count} checked"
            + (checks.Any(c => c.State == UpdateState.Unknown) ? $", {checks.Count(c => c.State == UpdateState.Unknown)} could not be checked." : "."));
        Raise(nameof(UpdatesCount));
        Raise(nameof(HasUpdates));
        Raise(nameof(UpdateAllText));
        Raise(nameof(Status));
        RaiseCommands();
    }

    /// <summary>
    /// Updates the given apps with winget, one at a time, and shows what really happened: the service asks winget again afterwards whether an
    /// update is still offered, so "updated" is never only an exit code. Recorded in the history as <c>apps-update</c>.
    /// </summary>
    private async Task UpdateAsync(IReadOnlyList<AppItem> items)
    {
        if (items.Count == 0 || _services.Changing)
        {
            return;
        }

        if (!ConfirmDialog.Ask(Loc.Plural("Apps.UpdateConfirmTitle", items.Count), "", [.. items.Select(i => i.UpdateLine)], Loc.Get("Apps.UpdateConfirmNote"), Loc.Get("Apps.Update")))
        {
            return;
        }

        _cancel = new CancellationTokenSource();
        Raise(nameof(Installing));
        Busy = true;
        Progress = Loc.Get("Apps.Starting");
        var change = _services.BeginChange();
        try
        {
            var started = DateTimeOffset.UtcNow;
            var token = _cancel.Token;
            var packages = items.Select(i => i.Package).ToList();
            var outcomes = await Task.Run(() => _services.Installer.Upgrade(packages, (package, line) =>
            {
                if (line.Length > 0)
                {
                    // winget's own lines (English) go to the session log; the page says which app is being updated.
                    _services.Log($"  {line}");
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Progress = Loc.Format("Apps.UpdatingProgress", package.Name));
                }
            }, token));

            var runId = SetupRun.NewId(started, "apps-update");
            var run = SetupRun.Create(runId, "apps-update", null, SetupRun.Stamp(started), SetupRun.Stamp(DateTimeOffset.UtcNow), outcomes, []);
            string? recordError = null;
            try
            {
                _services.Store.WriteRunRecord(runId, run.ToJson());
                _services.Recorded(run);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                recordError = e.Message;
            }

            ShowUpdateResult(run, recordError, token.IsCancellationRequested);
        }
        finally
        {
            change.Dispose();
            _cancel.Dispose();
            _cancel = null;
            Raise(nameof(Installing));
            Progress = "";
            Busy = false;
        }

        await DetectAsync();
    }

    /// <summary>
    /// Uninstalls one app with winget and shows what really happened: the service asks winget again afterwards whether the app is still installed,
    /// so "uninstalled" is never only an exit code. Recorded in the history as <c>apps-uninstall</c>. Nothing can be undone here: the app is gone
    /// (its settings and documents in the profile are normally kept), and installing it again is the way back.
    /// </summary>
    private async Task UninstallAsync(AppItem? item)
    {
        if (item is null || !item.CanUninstall || _services.Changing)
        {
            return;
        }

        if (!ConfirmDialog.Ask(Loc.Format("Apps.UninstallConfirmTitle", item.Name), "", [], Loc.Get("Apps.UninstallConfirmNote"), Loc.Get("Apps.Uninstall")))
        {
            return;
        }

        _cancel = new CancellationTokenSource();
        Raise(nameof(Installing));
        Busy = true;
        Progress = Loc.Get("Apps.Starting");
        var change = _services.BeginChange();
        try
        {
            var started = DateTimeOffset.UtcNow;
            var token = _cancel.Token;
            var outcomes = await Task.Run(() => _services.Installer.Uninstall([item.Package], (package, line) =>
            {
                if (line.Length > 0)
                {
                    _services.Log($"  {line}");
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Progress = Loc.Format("Apps.UninstallingProgress", package.Name));
                }
            }, token));

            var runId = SetupRun.NewId(started, "apps-uninstall");
            var run = SetupRun.Create(runId, "apps-uninstall", null, SetupRun.Stamp(started), SetupRun.Stamp(DateTimeOffset.UtcNow), outcomes, []);
            string? recordError = null;
            try
            {
                _services.Store.WriteRunRecord(runId, run.ToJson());
                _services.Recorded(run);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                recordError = e.Message;
            }

            ShowUninstallResult(item.Name, run, recordError, token.IsCancellationRequested);
        }
        finally
        {
            change.Dispose();
            _cancel.Dispose();
            _cancel = null;
            Raise(nameof(Installing));
            Progress = "";
            Busy = false;
        }

        await DetectAsync();
    }

    private void ShowUninstallResult(string name, SetupRun run, string? recordError, bool cancelled)
    {
        var outcome = run.Apps.Single();
        var done = outcome.Status is InstallStatus.Succeeded or InstallStatus.SucceededRebootRequired or InstallStatus.AlreadyInstalled;
        var lines = RunActions.FollowUps(_services, run).Select(f => f.Text).ToList();
        var technical = RunActions.TechnicalLines(run);
        if (recordError is not null)
        {
            lines.Add(Loc.Get("Common.RecordFailed"));
            technical = string.Join("\n", new[] { technical, recordError }.Where(t => t.Length > 0));
        }

        var recorded = recordError is null && _services.Store.ReadRunRecord(run.Id) is not null;
        Result.Show(done && !cancelled ? Tone.Ok : Tone.Warn,
            cancelled ? Loc.Format("Apps.UninstallResultCancelled", name)
            : done ? Loc.Format("Apps.UninstallResultDone", name)
            : Loc.Format("Apps.UninstallResultFailed", name),
            string.Join("\n", lines), null, recorded ? () => _openActivity(run.Id) : null, technical);
    }

    private void ShowUpdateResult(SetupRun run, string? recordError, bool cancelled)
    {
        var outcomes = run.Apps;
        var updated = outcomes.Count(o => o.Status is InstallStatus.Succeeded or InstallStatus.SucceededRebootRequired);
        var problems = outcomes.Count(o => o.Status is InstallStatus.Failed or InstallStatus.Cancelled or InstallStatus.WingetUnavailable);
        var lines = RunActions.FollowUps(_services, run).Select(f => f.Text).ToList();
        var technical = RunActions.TechnicalLines(run);
        if (recordError is not null)
        {
            lines.Add(Loc.Get("Common.RecordFailed"));
            technical = string.Join("\n", new[] { technical, recordError }.Where(t => t.Length > 0));
        }

        var recorded = recordError is null && _services.Store.ReadRunRecord(run.Id) is not null;
        Result.Show(problems > 0 || cancelled ? Tone.Warn : Tone.Ok,
            cancelled ? Loc.Format("Apps.UpdateResultCancelled", updated, outcomes.Count)
            : problems > 0 ? Loc.Format("Apps.UpdateResultPartial", updated, problems)
            : Loc.Plural("Apps.UpdateResultDone", updated),
            string.Join("\n", lines), null, recorded ? () => _openActivity(run.Id) : null, technical);
    }

    /// <summary>
    /// How many of these packages a configuration source would set up: the ones that have a config port in its bundle (the others install as they
    /// are, which is not an error).
    /// </summary>
    private static int CountIn(ConfigBundleFile? bundle, IEnumerable<AppPackage> packages) =>
        bundle is null ? 0 : packages.Count(p => ConfigCatalog.ById(p.Id) is { } port && bundle.Apps.ContainsKey(port.Id));

    /// <summary>The three ways to set the chosen apps up, one choice for the whole install: Mesura's, the user's own, or none.</summary>
    private List<DialogChoice> ConfigChoices(IReadOnlyList<AppPackage> chosen, string current)
    {
        var total = chosen.Count;
        var sameUser = _services.SameUser;
        var (defaults, _) = _services.BundleFor("defaults");
        var (saved, savedError) = _services.BundleFor("saved");
        var savedFile = SavedAvailable;
        var nMesura = CountIn(defaults, chosen);
        var nCustom = CountIn(saved, chosen);
        string Hint(int n, string countKey, string none) => !sameUser ? Loc.Get("Apps.ConfigBlockedShort") : n > 0 ? Loc.Format(countKey, n, total) : none;
        var customHint = !savedFile ? Loc.Get("Apps.NoSavedConfiguration")
            : savedError is not null ? Loc.Get("Apps.SavedUnreadable")
            : Hint(nCustom, "Apps.ChoiceCountCustom", Loc.Get("Apps.ChoiceCustomNone"));
        return
        [
            new DialogChoice("defaults", Loc.Get("Apps.ChoiceMesura"), Hint(nMesura, "Apps.ChoiceCountMesura", Loc.Get("Apps.ChoiceMesuraNone")), true, current == "defaults"),
            new DialogChoice("saved", Loc.Get("Apps.ChoiceCustom"), customHint, savedFile && savedError is null, current == "saved" && savedFile),
            new DialogChoice("none", Loc.Get("Apps.ChoiceStandard"), Loc.Get("Apps.ChoiceStandardHint"), true, current == "none" || (current == "saved" && !savedFile)),
        ];
    }

    private async Task InstallAsync()
    {
        var chosen = Chosen();
        var current = _services.Settings.ConfigureAfterInstall is "saved" or "none" ? _services.Settings.ConfigureAfterInstall : "defaults";
        var note = Loc.Get("Apps.ConfirmNote") + " " + Loc.Get("Apps.ConfirmKeepsSettings");
        var choice = ConfirmDialog.AskChoice(Loc.Plural("Apps.ConfirmTitle", chosen.Count), [.. chosen.Select(p => p.Name)], ConfigChoices(chosen, current), note, Loc.Get("Apps.Install"));
        if (choice is null)
        {
            return;
        }

        RememberSource(choice);
        var (bundle, configError) = _services.BundleFor(choice);
        var source = SourceName(choice);
        if (configError is not null)
        {
            _services.Log($"Apps: the saved settings couldn't be read: {configError}");
        }

        _cancel = new CancellationTokenSource();
        Raise(nameof(Installing));
        Busy = true;
        Progress = Loc.Get("Apps.Starting");
        var change = _services.BeginChange();
        try
        {
            var started = DateTimeOffset.UtcNow;
            var token = _cancel.Token;
            var setup = PendingSetup.Create(SetupRun.NewId(started, "apps-install"), SetupRun.Stamp(started), null, choice,
                [new SetupStep(StepKind.Apps, [.. chosen.Select(p => p.Id)])], null);

            // The same engine as Setup, with only its apps step; an unfinished setup from Setup is left as it is.
            var sitting = await Task.Run(() => SetupWorkflowRunner.Run(setup, _services.SetupContext(bundle), (_, line) =>
            {
                if (line.Length > 0)
                {
                    // winget's own lines (English) go to the session log; the page says what is happening.
                    _services.Log($"  {line}");
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Progress = Loc.Get("Apps.InstallingProgress"));
                }
            }, token, operation: "apps-install", persist: false));

            if (sitting.RecordError is null && _services.Store.ReadRunRecord(sitting.Run.Id) is not null)
            {
                _services.Recorded(sitting.Run);
            }

            ShowInstallResult(sitting, source, token.IsCancellationRequested);
        }
        finally
        {
            change.Dispose();
            _cancel.Dispose();
            _cancel = null;
            Raise(nameof(Installing));
            Progress = "";
            Busy = false;
        }

        await DetectAsync();
    }

    /// <summary>
    /// The result of an install: how many were installed (an app found already installed isn't counted), which settings
    /// the setup changed and from where, which apps kept the settings they had, and what is left; Undo when settings
    /// were written.
    /// </summary>
    private void ShowInstallResult(SetupSitting sitting, string source, bool cancelled)
    {
        var run = sitting.Run;
        var outcomes = run.Apps;
        var installed = outcomes.Count(o => o.Status is InstallStatus.Succeeded or InstallStatus.SucceededRebootRequired);
        var problems = outcomes.Count(o => o.Status is InstallStatus.Failed or InstallStatus.Cancelled or InstallStatus.WingetUnavailable);
        var already = outcomes.Where(o => o.Status == InstallStatus.AlreadyInstalled).Select(o => RunActions.AppName(_services, o.AppId)).ToList();
        var lines = new List<string>();
        lines.AddRange(run.Config.Where(c => c.Status == ConfigImportStatus.Applied && c.Applied.Count > 0)
            .Select(c => Loc.Format("Apps.SetUpLine", c.AppName, source, RunActions.Names(RunActions.SettingNames(c)))));
        lines.AddRange(sitting.KeptSettings.Select(id => Loc.Format("Apps.KeptSettings", RunActions.AppName(_services, id))));
        if (already.Count > 0)
        {
            lines.Add(Loc.Format("Apps.AlreadyInstalledLine", RunActions.Names(already)));
        }

        lines.AddRange(RunActions.FollowUps(_services, run).Select(f => f.Text));
        var technical = RunActions.TechnicalLines(run);
        if (sitting.RecordError is { } recordError)
        {
            lines.Add(Loc.Get("Common.RecordFailed"));
            technical = string.Join("\n", new[] { technical, recordError }.Where(t => t.Length > 0));
        }

        // A set-up that failed or waited for a running app is a problem too, not only a failed install.
        var setupProblems = run.Config.Count(c => c.Status is ConfigImportStatus.Failed or ConfigImportStatus.SkippedAppRunning);
        var recorded = sitting.RecordError is null && _services.Store.ReadRunRecord(run.Id) is not null;
        Result.Show(problems > 0 || setupProblems > 0 || cancelled ? Tone.Warn : Tone.Ok,
            cancelled ? Loc.Format("Apps.ResultCancelled", installed, outcomes.Count)
            : problems > 0 ? Loc.Format("Apps.ResultPartial", installed, problems)
            : installed == 0 && already.Count > 0 ? Loc.Get("Apps.ResultNothingNew")
            : Loc.Plural("Apps.ResultDone", installed),
            string.Join("\n", lines),
            recorded && RunActions.CanUndo(run) ? () => _ = UndoAsync(run) : null,
            recorded ? () => _openActivity(run.Id) : null, technical);
    }

    /// <summary>Undoes the settings an install's setup wrote (the apps stay installed).</summary>
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

    private void Fail(Exception e)
    {
        Busy = false;
        _services.Log($"Apps: {e}");
        Result.Show(Tone.Danger, Loc.Get("Common.Unexpected"), technical: e.Message);
    }

    private void RaiseCommands()
    {
        Refresh.RaiseCanExecuteChanged();
        Install.RaiseCanExecuteChanged();
        UpdateApp.RaiseCanExecuteChanged();
        UninstallApp.RaiseCanExecuteChanged();
        UpdateAll.RaiseCanExecuteChanged();
        Cancel.RaiseCanExecuteChanged();
        ClearSelection.RaiseCanExecuteChanged();
    }
}
