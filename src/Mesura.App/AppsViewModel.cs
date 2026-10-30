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

/// <summary>The folder choice of the Apps page, kept out of the item so the item stays about what it shows.</summary>
internal static class InstallFolderDialog
{
    public static void Choose(Services services, string id, string? current)
    {
        var title = Loc.Get("Apps.FolderTitle");
        if (current is { Length: > 0 })
        {
            var pick = ConfirmDialog.Choose(title, Loc.Format("Apps.FolderChosen", current), Loc.Get("Apps.FolderChange"), Loc.Get("Apps.FolderReset"), "", out _);
            if (pick == false)
            {
                services.Settings.InstallFolders?.Remove(id);
                services.SaveSettings();
                return;
            }

            if (pick is null)
            {
                return;
            }
        }

        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title, Multiselect = false };
        if (current is { Length: > 0 } && Directory.Exists(current))
        {
            dialog.InitialDirectory = current;
        }

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (Mesura.Setup.Windows.Apps.InstallFolders.Valid(dialog.FolderName) is not { } folder)
        {
            ConfirmDialog.Tell(title, Loc.Get("Apps.FolderRefused"));
            return;
        }

        (services.Settings.InstallFolders ??= [])[id] = folder;
        services.SaveSettings();
    }
}

/// <summary>One app in the Apps list: its icon, its state here, whether it is ticked, and its customisations.</summary>
public sealed class AppItem : Observable
{
    private readonly Func<bool> _wingetMissing;
    private bool _selected;
    private InstallState _state = InstallState.Unknown;
    private bool _checking = true;

    public AppItem(AppPackage package, Func<bool> wingetMissing, Func<AppPackage, bool> canDetect, Func<string, string?>? folderOf = null, Action<string, string?>? chooseFolder = null)
    {
        Package = package;
        _wingetMissing = wingetMissing;
        Detectable = canDetect(package);
        _folderOf = folderOf;
        ChooseFolder = new RelayCommand(() =>
        {
            if (chooseFolder is not null)
            {
                chooseFolder(package.Id, _folderOf?.Invoke(package.Id));
                Raise(nameof(InstallFolder));
                Raise(nameof(FolderTip));
                Raise(nameof(Note));
            }
        }, () => package.InstallLocation && CanSelect);
    }

    private readonly Func<string, string?>? _folderOf;

    /// <summary>Chooses (or, from the dialog's Reset, clears) the folder this app installs to; only where its installer takes one.</summary>
    public RelayCommand ChooseFolder { get; }

    /// <summary>The folder the user chose for the next install, or empty for the installer's own default.</summary>
    public string InstallFolder => Package.InstallLocation ? _folderOf?.Invoke(Package.Id) ?? "" : "";

    /// <summary>Whether the card offers a folder at all: only for installers that take one.</summary>
    public bool CanChooseFolder => Package.InstallLocation && !_checking && _state != InstallState.Installed;

    /// <summary>What the folder button says: the chosen folder, the default, or why there is no choice (the honest limit).</summary>
    public string FolderTip => !Package.InstallLocation ? Loc.Get("Apps.FolderNone")
        : InstallFolder.Length > 0 ? Loc.Format("Apps.FolderChosen", InstallFolder)
        : Loc.Get("Apps.FolderDefault");

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
    /// The app's note, which says what an install would do. Once the app is installed and carried by Mesura, the card has
    /// the Installed pill and the link to Configurations, so a sentence repeating both is dropped.
    /// </summary>
    public string Note => ShowConfigureLink ? "" : InstallFolder.Length > 0 && !_checking && _state != InstallState.Installed ? Loc.Format("Apps.InstallsTo", InstallFolder) : Loc.AppNote(Package.Id, Package.Note);

    /// <summary>Whether the card offers the way to Configurations: the app is installed and Mesura carries its settings.</summary>
    public bool ShowConfigureLink => !_checking && _state == InstallState.Installed && Port is not null;

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

    public string StateText => _checking ? Loc.Get("State.Checking") : _state switch
    {
        InstallState.Installed => Loc.Get("Apps.Installed"),
        InstallState.NotInstalled => Loc.Get("Apps.NotInstalled"),
        _ when _wingetMissing() => Loc.Get("Apps.NoWinget"),
        _ => Loc.Get("Apps.Unknown"),
    };

    public string StateTone => _checking ? Tone.Muted : _state switch
    {
        InstallState.Installed => Tone.Ok,
        InstallState.NotInstalled => Tone.Neutral,
        _ => Tone.Warn,
    };

    public void Update(InstallState state)
    {
        _state = state;
        _checking = false;
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

    public AppsViewModel(Services services, Action<string?> openActivity, Action openConfigurations)
    {
        _services = services;
        _openActivity = openActivity;
        OpenConfigurations = new RelayCommand(openConfigurations);
        View = new ListCollectionView(Items) { Filter = Matches };

        if (services.Apps.Catalog is { } catalog)
        {
            foreach (var package in catalog.Packages.Where(p => p.Listed && p.Customizes.Length == 0))
            {
                var item = new AppItem(package, () => _wingetMissing, services.Installer.CanDetect, id => services.Settings.InstallFolders?.GetValueOrDefault(id), (id, current) => InstallFolderDialog.Choose(services, id, current));
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

    public RelayCommand Cancel { get; }

    public RelayCommand GetWinget { get; }

    public RelayCommand ClearSelection { get; }

    /// <summary>Opens Configurations, where an installed app's settings are changed.</summary>
    public RelayCommand OpenConfigurations { get; }

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
            return Loc.Format("Apps.Status", installed, Items.Count - installed);
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

    public bool ConfigureDefaults
    {
        get => _services.Settings.ConfigureAfterInstall == "defaults";
        set => SetSource(value, "defaults");
    }

    public bool ConfigureSaved
    {
        get => _services.Settings.ConfigureAfterInstall == "saved" && SavedAvailable;
        set => SetSource(value, "saved");
    }

    public bool ConfigureNone
    {
        get => _services.Settings.ConfigureAfterInstall == "none" || (_services.Settings.ConfigureAfterInstall == "saved" && !SavedAvailable);
        set => SetSource(value, "none");
    }

    public bool SavedAvailable => _services.Settings.SavedConfiguration is { } path && File.Exists(path);

    public string SavedHint => SavedAvailable ? Path.GetFileName(_services.Settings.SavedConfiguration!) : Loc.Get("Apps.NoSavedConfiguration");

    /// <summary>The chosen set-up source in words, for confirmations and Setup's Apps card.</summary>
    public string SourceText => ConfigureNone ? Loc.Get("Apps.SourceNoneLine")
        : ConfigureSaved ? Loc.Get("Apps.SourceSaved") : Loc.Get("Apps.SourceDefaults");

    private void SetSource(bool value, string source)
    {
        if (!value || _services.Settings.ConfigureAfterInstall == source)
        {
            return;
        }

        _services.Settings.ConfigureAfterInstall = source;
        _services.SaveSettings();
        Raise(nameof(ConfigureDefaults));
        Raise(nameof(ConfigureSaved));
        Raise(nameof(ConfigureNone));
        Raise(nameof(SourceText));
        Raise(nameof(SelectionSummary));
        SelectionChanged?.Invoke();
    }

    /// <summary>Re-reads whether the saved configuration is there (Configurations may have changed it).</summary>
    public void RefreshSaved()
    {
        Raise(nameof(SavedAvailable));
        Raise(nameof(SavedHint));
        Raise(nameof(ConfigureSaved));
        Raise(nameof(ConfigureNone));
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

            var configured = WillConfigure(chosen);
            var text = Loc.Plural("Apps.Selected", chosen.Count);
            return configured > 0 ? $"{text} · {Loc.Plural("Apps.WillConfigure", configured)}" : text;
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
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task InstallAsync()
    {
        var chosen = Chosen();
        var (bundle, configError) = _services.AfterInstallBundle();
        var source = _services.Settings.ConfigureAfterInstall == "saved" ? Loc.Get("Apps.SourceSaved") : Loc.Get("Apps.SourceDefaults");
        var configured = bundle is null ? [] : chosen.Where(p => ConfigCatalog.ById(p.Id) is { } port && bundle.Apps.ContainsKey(port.Id)).Select(p => p.Id).ToHashSet();
        var lines = chosen.Select(p => configured.Contains(p.Id) ? Loc.Format("Apps.ConfirmLineConfigured", p.Name, source) : p.Name).ToList();
        var note = Loc.Get("Apps.ConfirmNote") + (configured.Count > 0 ? " " + Loc.Get("Apps.ConfirmKeepsSettings") : "")
            + (configError is null ? "" : " " + Loc.Get("Apps.SavedUnreadable"))
            + (!_services.SameUser ? " " + Loc.Get("Apps.ConfigBlocked") : "");
        if (!ConfirmDialog.Ask(Loc.Plural("Apps.ConfirmTitle", chosen.Count), "", lines, note, Loc.Get("Apps.Install")))
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
            var setup = PendingSetup.Create(SetupRun.NewId(started, "apps-install"), SetupRun.Stamp(started), null, _services.Settings.ConfigureAfterInstall,
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
        Cancel.RaiseCanExecuteChanged();
        ClearSelection.RaiseCanExecuteChanged();
    }
}
