using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Mesura.Core.Apps;
using Mesura.Core.Nvidia;
using Mesura.Core.Setup;
using Mesura.Core.Windows;
using Mesura.Platform.Windows.Windows;
using Mesura.Setup.Windows.Apps;
using Mesura.Setup.Windows.Nvidia;
using Mesura.Setup.Windows.Workflow;

namespace Mesura.App;

/// <summary>One of the two outcomes Optimize offers, with how much of it is still to do here.</summary>
public sealed class OutcomeChoice(WindowsPreset preset) : Observable
{
    private bool _selected;
    private int _pending = -1;

    public WindowsPreset Preset { get; } = preset;

    public string Name => Loc.Name(Preset);

    /// <summary>What assistive technology reads for this item: its display name, never the type name.</summary>
    public override string ToString() => Name;

    public string Summary => Loc.Summary(Preset);

    public bool IsSelected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    public string PendingText => _pending < 0 ? "" : _pending == 0 ? Loc.Get("Optimize.OutcomeDone") : Loc.Plural("Optimize.OutcomePending", _pending);

    public string PendingTone => _pending == 0 ? Tone.Ok : Tone.Accent;

    public void Update(int pending)
    {
        _pending = pending;
        Raise(string.Empty);
    }

    public void Relocalize() => Raise(string.Empty);
}

/// <summary>An entry of the plan: a setting that would change, ticked or not.</summary>
public sealed class PlanItem(SettingRow row, bool included) : Observable
{
    private bool _included = included;
    private bool _expanded;

    public SettingRow Row { get; } = row;

    public bool Included
    {
        get => _included;
        set => Set(ref _included, value);
    }

    public bool Expanded
    {
        get => _expanded;
        set => Set(ref _expanded, value);
    }
}

/// <summary>A fact about this PC in the summary strip.</summary>
public sealed record SystemFact(string Glyph, string Text, string Tone)
{
    /// <summary>A fact with one of the theme's icons, by its resource key.</summary>
    public static SystemFact With(string iconKey, string text, string tone) =>
        new(Application.Current?.TryFindResource(iconKey) as string ?? "", text, tone);
}

/// <summary>A guide in the NVIDIA section: a procedure carried out by hand, with its steps and its page.</summary>
public sealed class GuideItem(SettingRow row) : Observable
{
    private bool _expanded;

    public SettingRow Row { get; } = row;

    public bool Expanded
    {
        get => _expanded;
        set => Set(ref _expanded, value);
    }

    public RelayCommand Open { get; } = new(() =>
    {
        if (row.OpenTarget is { } target)
        {
            ShellLauncher.Open(target);
        }
    }, () => row.OpenTarget is not null);
}

/// <summary>
/// Setup (D031; Optimize in the redesign, §3): what Mesura sees on this PC, the choice between Recommended and Maximum
/// performance, and one plan of compact cards — Windows settings, the apps ticked on Apps, Visual C++ runtimes, and the
/// NVIDIA driver with its profiles where an NVIDIA GPU is present — previewed and confirmed once, then run in order by
/// the setup engine. An unfinished setup (the PC restarted, or the driver step waits in NVCleanstall) is shown first,
/// with what is done and what is left, and continues without repeating anything.
/// </summary>
public sealed class OptimizeViewModel : Observable, IPageState
{
    private const string GhostSpectrePreset = "ghost-spectre-superlite";
    private static readonly string[] NvidiaGuideIds = ["manual.nvidia-clean-driver", "manual.nvidia-profile-inspector", "manual.vrr-sync-setup"];

    private readonly Services _services;
    private readonly WindowsStateModel _state;
    private readonly AppsViewModel _apps;
    private readonly Action<string?> _openActivity;
    private readonly Dictionary<string, bool> _choices = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _startedHere = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromSeconds(4) };
    private OutcomeChoice? _selected;
    private SystemSummary? _summary;
    private IReadOnlyList<InstalledRuntime>? _installedRuntimes;
    private DriverLookupResult? _lookup;
    private bool _lookingUp;
    private string? _nvCleanstall;
    private ProfileInspector? _inspector;
    private bool _toolsRead;
    private PendingSetup? _pending;
    private string? _driverNow;
    private bool _finishing;
    private bool _watching;
    private bool _runtimesUnreadable;
    private bool _busy;
    private string _progress = "";
    private CancellationTokenSource? _cancel;

    public OptimizeViewModel(Services services, WindowsStateModel state, AppsViewModel apps, Action openAllSettings, Action<string?> openActivity, Action openApps)
    {
        _services = services;
        _state = state;
        _apps = apps;
        _openActivity = openActivity;

        var off = services.Settings.PlanSectionsOff ?? [];
        Windows = Section("windows", "IconWindows", "Setup.Section.Windows", off);
        AppsSection = Section("apps", "IconApps", "Setup.Section.Apps", off);
        Runtimes = Section("runtimes", "IconRuntime", "Setup.Section.Runtimes", off);
        Nvidia = Section("nvidia", "IconGpu", "Setup.Section.Nvidia", off);
        Sections = [Windows, AppsSection, Runtimes, Nvidia];

        Apply = new AsyncCommand(ReviewAsync, () => !_busy && !_services.Changing && _pending is null && PlannedSteps().Count > 0, Fail);
        Refresh = new AsyncCommand(RefreshAllAsync, () => !_busy && !state.Busy, Fail);
        OpenAllSettings = new RelayCommand(openAllSettings);
        OpenApps = new RelayCommand(openApps);
        Cancel = new RelayCommand(() =>
        {
            _cancel?.Cancel();
            Progress = Loc.Get("Setup.Cancelling");
            Cancel!.RaiseCanExecuteChanged();
        }, () => _cancel is { IsCancellationRequested: false });
        ContinueSetup = new AsyncCommand(ContinueAsync, () => !_busy && !_finishing && !_services.Changing && _pending is not null && !WaitingForDriver, Fail);
        DiscardSetup = new RelayCommand(Discard, () => !_busy && !_finishing && _pending is not null);
        DriverFinished = new AsyncCommand(() => FinishDriverAsync(skipped: false), () => !_busy && !_finishing && !_services.Changing && WaitingForDriver, Fail);
        SkipDriver = new AsyncCommand(() => FinishDriverAsync(skipped: true), () => !_busy && !_finishing && !_services.Changing && WaitingForDriver, Fail);
        ReopenNvCleanstall = new RelayCommand(Reopen, () => WaitingForDriver && _nvCleanstall is not null);
        ResetComponents = new RelayCommand(UseDefaultConfig, () => !IsDefaultSelection);
        OpenDriverPage = new RelayCommand(() => ShellLauncher.Open(_lookup?.Release?.DetailsUrl is { Length: > 0 } url ? url : NvidiaDriver.DownloadPage));

        if (state.Catalog is { } catalog)
        {
            foreach (var preset in catalog.Presets.Where(p => p.Tier == PresetTier.Primary))
            {
                var choice = new OutcomeChoice(preset);
                choice.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(OutcomeChoice.IsSelected) && choice.IsSelected && _selected != choice)
                    {
                        Select(choice);
                    }
                };
                Outcomes.Add(choice);
            }

            var chosen = Outcomes.FirstOrDefault(o => o.Preset.Id == services.Settings.Outcome) ?? Outcomes.FirstOrDefault();
            if (chosen is not null)
            {
                chosen.IsSelected = true;
            }
        }

        _pending = SetupWorkflowRunner.Pending(services.Store);
        _watch.Tick += async (_, _) => await WatchDriverAsync();

        state.Updated += Rebuild;
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WindowsStateModel.Busy))
            {
                Raise(nameof(Checking));
                Refresh.RaiseCanExecuteChanged();
            }
        };
        apps.SelectionChanged += UpdateApps;
        services.NvidiaConfigChanged += () =>
        {
            BuildComponents();
            UpdateNvidia();
        };
        services.ChangingChanged += RaiseCommands;
        Loc.Instance.LanguageChanged += () =>
        {
            foreach (var outcome in Outcomes)
            {
                outcome.Relocalize();
            }

            foreach (var section in Sections)
            {
                section.Relocalize();
            }

            foreach (var row in RuntimeRows)
            {
                row.Relocalize();
            }

            foreach (var row in Components)
            {
                row.Relocalize();
            }

            foreach (var row in Tweaks)
            {
                row.Relocalize();
            }

            Rebuild();
            Raise(nameof(Facts));
            Raise(nameof(ShowRuntimes));
        };
        _ = LoadAsync();
    }

    // ---- Collections ---------------------------------------------------------------------------------------------------

    public ObservableCollection<OutcomeChoice> Outcomes { get; } = [];

    public IReadOnlyList<PlanSection> Sections { get; }

    public PlanSection Windows { get; }

    public PlanSection AppsSection { get; }

    public PlanSection Runtimes { get; }

    public PlanSection Nvidia { get; }

    public ObservableCollection<PlanItem> Changes { get; } = [];

    public ObservableCollection<PlanItem> Extras { get; } = [];

    public ObservableCollection<PlanItem> GhostSpectre { get; } = [];

    public ObservableCollection<SettingRow> Done { get; } = [];

    public ObservableCollection<SettingRow> NotHere { get; } = [];

    public ObservableCollection<GuideItem> NvidiaGuides { get; } = [];

    public ObservableCollection<PlanAppChip> PlanApps { get; } = [];

    public ObservableCollection<RuntimeRow> RuntimeRows { get; } = [];

    public ObservableCollection<ComponentRow> Components { get; } = [];

    public ObservableCollection<TweakRow> Tweaks { get; } = [];

    public ObservableCollection<StepChip> PendingSteps { get; } = [];

    public Banner Result { get; } = new();

    public void Leave() => Result.Settle();

    // ---- Commands ------------------------------------------------------------------------------------------------------

    /// <summary>Review and apply: the one confirmation of the whole plan.</summary>
    public AsyncCommand Apply { get; }

    public AsyncCommand Refresh { get; }

    public RelayCommand OpenAllSettings { get; }

    public RelayCommand OpenApps { get; }

    public RelayCommand Cancel { get; }

    public AsyncCommand ContinueSetup { get; }

    public RelayCommand DiscardSetup { get; }

    public AsyncCommand DriverFinished { get; }

    public AsyncCommand SkipDriver { get; }

    public RelayCommand ReopenNvCleanstall { get; }

    public RelayCommand ResetComponents { get; }

    public RelayCommand OpenDriverPage { get; }

    // ---- State ---------------------------------------------------------------------------------------------------------

    public bool Checking => _state.Busy || !_state.Loaded || _installedRuntimes is null;

    public bool Busy
    {
        get => _busy;
        private set
        {
            Set(ref _busy, value);
            RaiseCommands();
        }
    }

    /// <summary>What the running setup is doing now.</summary>
    public string Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    public bool Running => _cancel is not null;

    private OutcomeChoice? Selected => _selected;

    /// <summary>Whether the catalog in use offers the outcomes (an edited copy may lack them).</summary>
    public bool HasOutcomes => Outcomes.Count > 0;

    /// <summary>What the page says about the Windows catalog in use: an edited copy, or one set aside (empty for the built-in one).</summary>
    public string CatalogNote => _services.WindowsCatalogNote.Text;

    public string CatalogNoteTone => _services.WindowsCatalogNote.Tone;

    /// <summary>The page's one-line status.</summary>
    public string Status
    {
        get
        {
            if (_state.Catalog is null)
            {
                return Loc.Format("Optimize.CatalogFailed", _services.WindowsCatalog.Error ?? "");
            }

            if (_pending is not null)
            {
                return Loc.Get("Setup.StatusUnfinished");
            }

            if (!HasOutcomes)
            {
                return Loc.Get("Optimize.NoOutcomes");
            }

            if (!_state.Loaded)
            {
                return Loc.Get("Optimize.Checking");
            }

            var steps = PlannedSteps().Count;
            return steps == 0 ? Loc.Get("Setup.StatusReady") : Loc.Plural("Setup.StatusPending", steps);
        }
    }

    /// <summary>The Visual C++ step is shown only while a runtime is missing (or could not be read): a complete set needs no card.</summary>
    public bool ShowRuntimes => _installedRuntimes is null || _runtimesUnreadable || _installedRuntimes.Count < VcRuntimes.All.Count;

    public IReadOnlyList<SystemFact> Facts
    {
        get
        {
            if (_summary is not { } s)
            {
                return [];
            }

            // Home says what matters for the choice below: which Windows this is, and an image that leaves less to do. The edition, build,
            // device type, graphics card and runtimes are on System; who is running Mesura is in the dock.
            var facts = new List<SystemFact>
            {
                SystemFact.With("IconWindows", $"{s.WindowsName} {s.Version}".Replace("  ", " ", StringComparison.Ordinal).Trim(), Tone.Neutral),
            };

            if (s.GhostSpectre)
            {
                facts.Add(SystemFact.With("IconImage", Loc.Get("Optimize.GhostSpectreImage"), Tone.Neutral));
            }

            return facts;
        }
    }

    public bool HasExtras => Extras.Count > 0;

    public bool HasGhostSpectre => GhostSpectre.Count > 0;

    public bool HasDone => Done.Count > 0;

    public bool HasNotHere => NotHere.Count > 0;

    public string DoneHeader => Loc.Plural("Optimize.DoneHeader", Done.Count);

    public string NotHereHeader => Loc.Plural("Optimize.NotHereHeader", NotHere.Count);

    public bool PlanEmpty => _state.Loaded && Selected is not null && Changes.Count == 0 && Extras.Count == 0 && GhostSpectre.Count == 0;

    public bool HasPlanApps => PlanApps.Count > 0;

    public string AppsSourceLine => Loc.Format("Setup.AppsSource", _apps.SourceText);

    // ---- NVIDIA --------------------------------------------------------------------------------------------------------

    public bool HasNvidia => _summary?.Nvidia is not null;

    public string NvidiaGpu => _summary?.Nvidia?.Name ?? "";

    public string NvidiaInstalled => _summary?.Nvidia?.Release is { } release ? release : Loc.Get("Setup.DriverUnread");

    /// <summary>The newest driver NVIDIA offers, or how the lookup stands.</summary>
    public string NvidiaLatest => _lookingUp ? Loc.Get("Setup.DriverLookingUp")
        : _lookup?.Release is { } release
            ? NvidiaDriver.IsNewer(release.Version, _summary?.Nvidia?.Release)
                ? Loc.Format("Setup.DriverNewer", release.Version, release.ReleaseDate)
                : Loc.Format("Setup.DriverCurrent", release.Version)
            : _lookup?.Error is not null ? Loc.Get("Setup.DriverLookupFailed") : "";

    public string NvCleanstallState => !_toolsRead ? Loc.Get("State.Checking")
        : _nvCleanstall is not null ? Loc.Get("Setup.ToolInstalled") : Loc.Get("Setup.ToolWillInstall");

    public string InspectorState => !_toolsRead ? Loc.Get("State.Checking")
        : _inspector is { } inspector
            ? inspector.CanMerge ? Loc.Format("Setup.InspectorReady", inspector.Version) : Loc.Format("Setup.InspectorOld", inspector.Version, ProfileInspector.MergeImport)
            : Loc.Get("Setup.ToolWillInstall");

    /// <summary>The saved NVIDIA driver configuration (D032), or the default one (the owner's) until it is changed.</summary>
    private static NvidiaDriverConfig Saved(Services services) => NvidiaDriverConfig.Normalize(services.Settings.NvidiaConfig);

    /// <summary>The configuration as the card shows it now: the ticked components and tweaks, with the saved rest.</summary>
    private NvidiaDriverConfig CurrentConfig()
    {
        var saved = Saved(_services);
        return NvidiaDriverConfig.Normalize(saved with
        {
            Components = [.. Components.Where(c => c.Included).Select(c => c.Component.Id)],
            Tweaks = [.. Tweaks.Where(t => t.Included).Select(t => t.Tweak.Id)],
        });
    }

    public bool IsDefaultSelection => CurrentConfig().IsDefault;

    /// <summary>The configuration's name: the default (the owner's), or changed from it.</summary>
    public string ComponentsName => IsDefaultSelection ? Loc.Get("Setup.ConfigDefault") : Loc.Get("Setup.ConfigChanged");

    public string InterruptLine => Loc.Format("Setup.InterruptLine", CurrentConfig().InterruptPolicy, CurrentConfig().InterruptPriority);

    /// <summary>The NVIDIA profile the configuration imports after the driver, by name, or that none is set.</summary>
    public string ProfileName => CurrentConfig().Profile is { } reference && NvidiaProfiles.ById(reference.Id) is { } profile
        ? profile.Name
        : Loc.Get("Setup.ProfileNone");

    public bool HasProfile => CurrentConfig().Profile is not null;

    /// <summary>The settings inside that profile, by NVIDIA Profile Inspector's names, for the card's details.</summary>
    public IReadOnlyList<string> ProfileSettings
    {
        get
        {
            if (CurrentConfig().Profile is not { } reference || NvidiaProfiles.Load(reference) is not { Bytes: { } bytes })
            {
                return [];
            }

            try
            {
                return [.. NipFile.Parse(NipFile.Decode(bytes)).SelectMany(p => p.Settings.Select(s =>
                    MesuraNvidiaProfile.Settings.FirstOrDefault(m => m.Id == s.Id) is { } known
                        ? $"{known.Name} = {known.ValueMeaning}"
                        : $"{p.Name} · 0x{s.Id:X8} = {s.Value}"))];
            }
            catch (FormatException)
            {
                return [];
            }
        }
    }

    public string ProfileSource => CurrentConfig().Profile is { } reference && NvidiaProfiles.ById(reference.Id) is { } profile
        ? Loc.Format("Setup.ProfileSource", profile.Source, profile.Sha256[..16])
        : "";

    /// <summary>
    /// How the profile is fitted to this PC before the import (D036), as the import would fit it now: one line per
    /// setting left out (the power setting that would cost a laptop battery life).
    /// </summary>
    public IReadOnlyList<string> ProfileFit
    {
        get
        {
            if (CurrentConfig().Profile is not { } reference || NvidiaProfiles.Load(reference) is not { Bytes: { } bytes })
            {
                return [];
            }

            try
            {
                return [.. NvidiaProfilePortability.Adapt(bytes, _summary?.HasBattery).Adjustments
                    .Select(a => Loc.Format("Setup.ProfileFitLine", a.Setting, a.OriginalShort, a.Imported ?? Loc.Get("Activity.ProfileLeftOut")))];
            }
            catch (FormatException)
            {
                return [];
            }
        }
    }

    public bool HasProfileFit => ProfileFit.Count > 0;

    // ---- Unfinished setup ----------------------------------------------------------------------------------------------

    public bool HasPending => _pending is not null;

    public string PendingTitle => _pending is { } pending
        ? Loc.Format("Setup.PendingTitle", Started(pending), pending.Steps.Count(s => s.Finished), pending.Steps.Count)
        : "";

    public string PendingNext => _pending?.Next is { } next ? Loc.Format("Setup.PendingNext", RunActions.SetupStepName(next.Kind)) : "";

    /// <summary>The driver step waits for the user in NVCleanstall.</summary>
    public bool WaitingForDriver => _pending?.Step(StepKind.NvidiaDriver)?.State == StepState.Waiting;

    public string WaitingTarget => _pending?.Nvidia is { } target
        ? target.Latest is { Length: > 0 } latest
            ? Loc.Format("Setup.WaitingTarget", latest, target.DriverAtStart.Length > 0 ? target.DriverAtStart : "?")
            : Loc.Format("Setup.WaitingTargetUnknown", target.DriverAtStart.Length > 0 ? target.DriverAtStart : "?")
        : "";

    public string WaitingWatch => _driverNow is { } now && _pending?.Nvidia is { } target && now != target.DriverAtStart
        ? Loc.Format("Setup.WaitingChanged", now)
        : Loc.Get("Setup.WaitingWatching");

    /// <summary>The components to tick and leave in NVCleanstall, by its labels.</summary>
    public IReadOnlyList<string> WaitingChecklist => _pending?.Nvidia is { } target
        ? [.. NvidiaComponents.All.Select(c => (target.Components.Contains(c.Id, StringComparer.OrdinalIgnoreCase) ? "✓  " : "✗  ") + c.Label)]
        : [];

    /// <summary>The tweaks to tick and leave on NVCleanstall's Tweaks page (with Expert Tweaks shown), by its labels.</summary>
    public IReadOnlyList<string> WaitingTweaks => _pending?.Nvidia is { } target
        ? [.. NvidiaTweaks.All.Select(t => ((target.Tweaks ?? (target.CleanInstall ? [NvidiaTweaks.CleanInstall] : [])).Contains(t.Id, StringComparer.OrdinalIgnoreCase) ? "✓  " : "✗  ") + t.Label)]
        : [];

    public string WaitingInterrupt => _pending?.Nvidia is { } target
        ? Loc.Format("Setup.InterruptLine", target.InterruptPolicy, target.InterruptPriority)
        : "";

    // ---- Summary -------------------------------------------------------------------------------------------------------

    /// <summary>What Review and apply would do, in one line.</summary>
    public string SelectionSummary
    {
        get
        {
            if (_pending is not null)
            {
                return Loc.Get("Setup.SummaryUnfinished");
            }

            var parts = new List<string>();
            if (Windows.Active && IncludedRows() is { Count: > 0 } rows)
            {
                parts.Add(Loc.Plural("Setup.SummarySettings", rows.Count));
            }

            if (AppsSection.Active && _apps.Chosen().Count is var apps and > 0)
            {
                parts.Add(Loc.Plural("Setup.SummaryApps", apps));
            }

            if (Runtimes.Active && RuntimeRows.Count(r => r.Included) is var runtimes and > 0)
            {
                parts.Add(Loc.Plural("Setup.SummaryRuntimes", runtimes));
            }

            if (Nvidia.Active)
            {
                parts.Add(Loc.Get("Setup.SummaryDriver"));
            }

            if (parts.Count == 0)
            {
                return _state.Loaded ? Loc.Get("Setup.SummaryNothing") : "";
            }

            var restart = Windows.Active ? IncludedRows().Count(r => r.NeedsRestart) : 0;
            return string.Join(" · ", parts) + (restart > 0 ? " · " + Loc.Plural("Optimize.AfterRestart", restart) : "");
        }
    }

    /// <summary>The Windows card's heading inside: how many settings the outcome, extras and follow-up would change.</summary>
    public string PlanHeader => Loc.Plural("Optimize.PlanHeader", IncludedRows().Count);

    // ---- Loading -------------------------------------------------------------------------------------------------------

    private PlanSection Section(string key, string icon, string title, IReadOnlyCollection<string> off) =>
        new(key, icon, title, !off.Contains(key, StringComparer.OrdinalIgnoreCase), OnSectionSwitched);

    private void OnSectionSwitched(PlanSection section)
    {
        var off = Sections.Where(s => !s.Included).Select(s => s.Key).ToList();
        _services.Settings.PlanSectionsOff = off.Count > 0 ? off : null;
        _services.SaveSettings();
        RaisePlan();
    }

    private async Task LoadAsync()
    {
        try
        {
            _summary = await _services.Summary();
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _summary = null;
            _services.Log($"The system summary couldn't be read: {e.Message}");
        }

        BuildComponents();
        Raise(nameof(Facts));
            Raise(nameof(ShowRuntimes));
        Rebuild();
        await Task.WhenAll(ReadRuntimesAsync(), ReadNvidiaToolsAsync(), LookUpDriverAsync());
        if (WaitingForDriver)
        {
            _watch.Start();
        }
    }

    private async Task RefreshAllAsync()
    {
        _installedRuntimes = null;
        Raise(nameof(Checking));
        await Task.WhenAll(_state.RefreshAsync(), ReadRuntimesAsync(), ReadNvidiaToolsAsync());
    }

    private async Task ReadRuntimesAsync()
    {
        // Unreadable is its own state: nothing is shown as missing and nothing can be ticked, so nothing installs blind.
        var detected = await Task.Run(() => VcRuntimeDetector.Detect());
        _runtimesUnreadable = detected is null;
        _installedRuntimes = detected ?? [];
        var choices = _services.Settings.RuntimeChoices ?? [];
        RuntimeRows.Clear();
        foreach (var runtime in _runtimesUnreadable ? [] : VcRuntimes.All)
        {
            var installed = _installedRuntimes.FirstOrDefault(i => i.Id == runtime.Id)?.Version;
            RuntimeRows.Add(new RuntimeRow(runtime, installed, choices.TryGetValue(runtime.Id, out var chosen) ? chosen : runtime.Default, () =>
            {
                _services.Settings.RuntimeChoices = RuntimeRows.Where(r => r.CanInclude && r.Included != r.Runtime.Default)
                    .ToDictionary(r => r.Runtime.Id, r => r.Included);
                _services.SaveSettings();
                UpdateRuntimes();
            }));
        }

        _services.Log(_runtimesUnreadable
            ? "Runtimes: the installed Visual C++ runtimes couldn't be read from the registry."
            : $"Runtimes: {_installedRuntimes.Count} of {VcRuntimes.All.Count} Visual C++ runtimes installed.");
        Raise(nameof(Checking));
        Raise(nameof(Facts));
            Raise(nameof(ShowRuntimes));
        UpdateRuntimes();
    }

    private async Task ReadNvidiaToolsAsync()
    {
        if (!HasNvidia)
        {
            return;
        }

        (_nvCleanstall, _inspector) = await Task.Run(() => (_services.Nvidia.FindNvCleanstall(), _services.Nvidia.FindProfileInspector()));
        _toolsRead = true;
        UpdateNvidia();
        ReopenNvCleanstall.RaiseCanExecuteChanged();
    }

    private async Task LookUpDriverAsync()
    {
        if (_summary?.Nvidia is not { } gpu)
        {
            return;
        }

        _lookingUp = true;
        UpdateNvidia();
        _lookup = await NvidiaDriverLookup.LatestAsync(gpu, _summary.Build);
        _lookingUp = false;
        _services.Log(_lookup.Release is { } release
            ? $"NVIDIA: {gpu.Name} runs {gpu.Release ?? "an unread driver"}; NVIDIA's newest WHQL Game Ready driver for it is {release.Version} ({release.ReleaseDate})."
            : $"NVIDIA: the driver lookup didn't answer: {_lookup.Error}");
        UpdateNvidia();
    }

    /// <summary>Shows the saved NVIDIA configuration (the default one until changed) on the card's components and tweaks.</summary>
    private void BuildComponents()
    {
        Components.Clear();
        Tweaks.Clear();
        var saved = Saved(_services);
        foreach (var component in NvidiaComponents.All)
        {
            Components.Add(new ComponentRow(component, saved.Components.Contains(component.Id, StringComparer.OrdinalIgnoreCase), SaveConfig));
        }

        foreach (var tweak in NvidiaTweaks.All)
        {
            Tweaks.Add(new TweakRow(tweak, saved.Tweaks.Contains(tweak.Id, StringComparer.OrdinalIgnoreCase), SaveConfig));
        }
    }

    /// <summary>Keeps what the card shows as the saved configuration (nothing is saved while it is the default).</summary>
    private void SaveConfig()
    {
        var config = CurrentConfig();
        _services.Settings.NvidiaConfig = config.IsDefault ? null : config;
        _services.SaveSettings();
        UpdateNvidia();
    }

    private void UseDefaultConfig()
    {
        _services.Settings.NvidiaConfig = null;
        _services.SaveSettings();
        BuildComponents();
        UpdateNvidia();
    }

    // ---- The plan ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Makes one outcome the chosen one. The cards are radio buttons, but WPF may report the new card as checked before
    /// it unchecks the old one, so the plan doesn't read the cards: the choice is kept here, the other card cleared, and
    /// only then is the plan rebuilt.
    /// </summary>
    private void Select(OutcomeChoice choice)
    {
        _selected = choice;
        foreach (var other in Outcomes.Where(o => o != choice))
        {
            other.IsSelected = false;
        }

        Rebuild();
    }

    /// <summary>Rebuilds the plan from the current reading, keeping what the user ticked or unticked.</summary>
    private void Rebuild()
    {
        foreach (var item in Changes.Concat(Extras).Concat(GhostSpectre))
        {
            item.PropertyChanged -= OnItemChanged;
        }

        Changes.Clear();
        Extras.Clear();
        GhostSpectre.Clear();
        Done.Clear();
        NotHere.Clear();
        NvidiaGuides.Clear();

        if (Selected is { } outcome && _state.Loaded)
        {
            foreach (var row in _state.RowsOf(outcome.Preset.OperationIds))
            {
                if (row.CanSelect)
                {
                    Changes.Add(Item(row, true));
                }
                else if (row.State == OperationState.Applied)
                {
                    Done.Add(row);
                }
                else
                {
                    NotHere.Add(row);
                }
            }

            foreach (var row in _state.RowsOf(outcome.Preset.OptionalIds).Where(r => r.CanSelect))
            {
                Extras.Add(Item(row, false));
            }

            if (_summary?.GhostSpectre == true && _state.Catalog?.PresetById(GhostSpectrePreset) is { } ghost)
            {
                var inPlan = Changes.Concat(Extras).Select(i => i.Row.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var row in _state.RowsOf(ghost.OperationIds).Where(r => r.CanSelect && !inPlan.Contains(r.Id)))
                {
                    GhostSpectre.Add(Item(row, true));
                }
            }

            foreach (var other in Outcomes)
            {
                other.Update(_state.RowsOf(other.Preset.OperationIds).Count(r => r.CanSelect));
            }
        }

        if (HasNvidia)
        {
            foreach (var row in _state.RowsOf(NvidiaGuideIds))
            {
                NvidiaGuides.Add(new GuideItem(row));
            }
        }

        UpdateWindows();
        UpdateApps();
        UpdateRuntimes();
        UpdateNvidia();
        UpdatePending();
        Raise(string.Empty);
        RaiseCommands();
    }

    private void UpdateWindows()
    {
        var rows = IncludedRows();
        var restart = rows.Count(r => r.NeedsRestart);
        Windows.Update(
            !_state.Loaded ? Loc.Get("Optimize.Checking")
            : rows.Count == 0 ? Loc.Format("Optimize.StatusDone", Selected?.Name ?? "")
            : Loc.Plural("Setup.WindowsLine", rows.Count, Selected?.Name ?? "") + (restart > 0 ? " · " + Loc.Plural("Optimize.AfterRestart", restart) : ""),
            rows.Count > 0 ? rows.Count.ToString(Loc.Instance.Culture) : "", Tone.Accent, rows.Count > 0);
        RaisePlan();
    }

    private void UpdateApps()
    {
        PlanApps.Clear();
        foreach (var chip in _apps.ChosenChips())
        {
            PlanApps.Add(chip);
        }

        var chosen = _apps.Chosen();
        var names = RunActions.Names([.. chosen.Select(p => p.Name)]);
        AppsSection.Update(
            chosen.Count == 0 ? Loc.Get("Setup.AppsNone")
            : _apps.WillConfigure(chosen) > 0 ? Loc.Format("Setup.AppsLine", names, _apps.SourceText)
            : names,
            chosen.Count > 0 ? chosen.Count.ToString(Loc.Instance.Culture) : "", Tone.Accent, chosen.Count > 0);
        Raise(nameof(HasPlanApps));
        Raise(nameof(AppsSourceLine));
        RaisePlan();
    }

    private void UpdateRuntimes()
    {
        if (_installedRuntimes is null)
        {
            Runtimes.Update(Loc.Get("State.Checking"), "", Tone.Neutral, false);
            return;
        }

        if (_runtimesUnreadable)
        {
            Runtimes.Update(Loc.Get("Setup.RuntimesUnreadable"), "", Tone.Warn, false);
            return;
        }

        var missing = RuntimeRows.Count(r => !r.Installed);
        var chosen = RuntimeRows.Count(r => r.Included);
        Runtimes.Update(
            missing == 0 ? Loc.Get("Setup.RuntimesAll")
            : chosen == 0 ? Loc.Plural("Setup.RuntimesMissingNone", missing)
            : Loc.Format("Setup.RuntimesLine", chosen, missing),
            chosen > 0 ? chosen.ToString(Loc.Instance.Culture) : "", Tone.Accent, chosen > 0);
        RaisePlan();
    }

    private void UpdateNvidia()
    {
        if (!HasNvidia)
        {
            Nvidia.Update(_summary is null ? Loc.Get("State.Checking") : Loc.Get("Setup.NoNvidia"), "", Tone.Muted, false);
        }
        else
        {
            var line = Loc.Format("Setup.NvidiaLine", NvidiaGpu, NvidiaInstalled)
                + (_lookup?.Release is { } release && NvidiaDriver.IsNewer(release.Version, _summary?.Nvidia?.Release) ? " · " + Loc.Format("Setup.NvidiaAvailable", release.Version) : "");

            // NVIDIA Profile Inspector runs only elevated, and the steps are back up, driver, restore or nothing: without
            // administrator rights the card says so instead of offering a plan that would stop at the backup.
            var elevated = _services.Reader.IsElevated || Services.PreviewHardware;
            Nvidia.Update(elevated ? line : line + " · " + Loc.Get(Loc.AdminKey("Setup.NvidiaNeedsAdmin")), "", elevated ? Tone.Neutral : Tone.Warn, elevated);
        }

        Raise(nameof(NvidiaLatest));
        Raise(nameof(NvidiaInstalled));
        Raise(nameof(NvCleanstallState));
        Raise(nameof(InspectorState));
        Raise(nameof(ComponentsName));
        Raise(nameof(IsDefaultSelection));
        Raise(nameof(InterruptLine));
        Raise(nameof(ProfileName));
        Raise(nameof(HasProfile));
        Raise(nameof(ProfileSource));
        Raise(nameof(ProfileFit));
        Raise(nameof(HasProfileFit));
        Raise(nameof(HasNvidia));
        Raise(nameof(NvidiaGpu));
        ResetComponents.RaiseCanExecuteChanged();
        RaisePlan();
    }

    private void UpdatePending()
    {
        PendingSteps.Clear();
        foreach (var step in _pending?.Steps ?? [])
        {
            PendingSteps.Add(StepStates.Chip(step));
        }

        foreach (var section in Sections)
        {
            var step = _pending?.Steps.FirstOrDefault(s => SectionOf(s.Kind) == section.Key && !s.Finished)
                ?? _pending?.Steps.LastOrDefault(s => SectionOf(s.Kind) == section.Key);
            section.ShowRun(step is null ? "" : Loc.Get($"Setup.State.{step.State}"), step is null ? Tone.Neutral : Tone.Of(RunActions.SetupStateWord(step.State)));
        }

        Raise(nameof(HasPending));
        Raise(nameof(PendingTitle));
        Raise(nameof(PendingNext));
        Raise(nameof(WaitingForDriver));
        Raise(nameof(WaitingTarget));
        Raise(nameof(WaitingWatch));
        Raise(nameof(WaitingChecklist));
        Raise(nameof(WaitingTweaks));
        Raise(nameof(WaitingInterrupt));
        Raise(nameof(Status));
        Raise(nameof(SelectionSummary));
        RaiseCommands();
    }

    private static string SectionOf(StepKind kind) => kind switch
    {
        StepKind.Runtimes => "runtimes",
        StepKind.Apps => "apps",
        StepKind.Windows => "windows",
        _ => "nvidia",
    };

    private void RaisePlan()
    {
        Raise(nameof(SelectionSummary));
        Raise(nameof(PlanHeader));
        Raise(nameof(Status));
        Apply?.RaiseCanExecuteChanged();
    }

    private PlanItem Item(SettingRow row, bool included)
    {
        var item = new PlanItem(row, _choices.TryGetValue(row.Id, out var chosen) ? chosen : included);
        item.PropertyChanged += OnItemChanged;
        return item;
    }

    private void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlanItem.Included) && sender is PlanItem item)
        {
            _choices[item.Row.Id] = item.Included;
            UpdateWindows();
        }
    }

    private List<SettingRow> IncludedRows() =>
        [.. Changes.Concat(Extras).Concat(GhostSpectre).Where(i => i.Included).Select(i => i.Row).DistinctBy(r => r.Id)];

    /// <summary>The steps Review and apply would run, from the cards that are switched on and have something to do.</summary>
    private List<SetupStep> PlannedSteps()
    {
        var steps = new List<SetupStep>();
        if (Runtimes.Active)
        {
            steps.Add(new SetupStep(StepKind.Runtimes, [.. RuntimeRows.Where(r => r.Included).Select(r => r.Runtime.Id)]));
        }

        if (AppsSection.Active)
        {
            steps.Add(new SetupStep(StepKind.Apps, [.. _apps.Chosen().Select(p => p.Id)]));
        }

        if (Windows.Active)
        {
            steps.Add(new SetupStep(StepKind.Windows, [.. IncludedRows().Select(r => r.Id)]));
        }

        if (Nvidia.Active)
        {
            steps.Add(new SetupStep(StepKind.NvidiaBackup, []));
            steps.Add(new SetupStep(StepKind.NvidiaDriver, []));
            steps.Add(new SetupStep(StepKind.NvidiaRestore, []));
            steps.Add(new SetupStep(StepKind.NvidiaProfile, []));
        }

        return [.. steps.Where(s => s.Items.Count > 0 || s.Kind >= StepKind.NvidiaBackup)];
    }

    // ---- Running -------------------------------------------------------------------------------------------------------

    private async Task ReviewAsync()
    {
        var steps = PlannedSteps();
        var rows = Windows.Active ? IncludedRows() : [];
        if (rows.Count > 0 && RunActions.Conflicts(_services, [.. rows.Select(r => r.Operation)]) is { Count: > 0 } conflicts)
        {
            Result.Show(Tone.Danger, Loc.Get("Optimize.Conflict"), string.Join("\n", conflicts));
            return;
        }

        var (bundle, configError) = _services.AfterInstallBundle();
        var lines = new List<string>();
        var notes = new List<string>();
        foreach (var step in steps)
        {
            switch (step.Kind)
            {
                case StepKind.Runtimes:
                    lines.Add(Loc.Format("Setup.ConfirmRuntimes", RunActions.Names([.. step.Items.Select(id => VcRuntimes.ById(id)!.Package.Name.Replace("Visual C++ ", "", StringComparison.Ordinal))])));
                    break;
                case StepKind.Apps:
                    var apps = _apps.Chosen();
                    var appNames = RunActions.Names([.. apps.Select(p => p.Name)]);
                    lines.Add(_apps.WillConfigure(apps) > 0
                        ? Loc.Format("Setup.ConfirmApps", appNames, _apps.SourceText)
                        : Loc.Format("Setup.ConfirmAppsPlain", appNames));
                    break;
                case StepKind.Windows:
                    lines.Add(Loc.Plural("Setup.ConfirmWindows", rows.Count, RunActions.Names([.. rows.Select(r => r.Name)])));
                    break;
                case StepKind.NvidiaBackup:
                    lines.Add(Loc.Get("Setup.ConfirmNvidiaBackup"));
                    break;
                case StepKind.NvidiaDriver:
                    lines.Add(_lookup?.Release is { } release
                        ? Loc.Format("Setup.ConfirmNvidiaDriver", release.Version, ComponentsName)
                        : Loc.Format("Setup.ConfirmNvidiaDriverUnknown", ComponentsName));
                    break;
                case StepKind.NvidiaRestore:
                    lines.Add(Loc.Get("Setup.ConfirmNvidiaRestore"));
                    break;
                case StepKind.NvidiaProfile:
                    lines.Add(!HasProfile ? Loc.Get("Setup.ConfirmNvidiaNoProfile")
                        : HasProfileFit ? Loc.Format("Setup.ConfirmNvidiaProfileFitted", ProfileName)
                        : Loc.Format("Setup.ConfirmNvidiaProfile", ProfileName));
                    break;
            }
        }

        if (rows.Count(r => r.NeedsRestart) is var restart and > 0)
        {
            notes.Add(Loc.Plural("Optimize.ConfirmRestart", restart));
        }

        if (rows.Count(r => r.NeedsAdmin) is var admin and > 0)
        {
            notes.Add(Loc.Plural(Loc.AdminKey("Optimize.ConfirmAdmin"), admin));
        }

        if (steps.Any(s => s.Kind == StepKind.NvidiaDriver))
        {
            notes.Add(Loc.Get("Setup.ConfirmDriverNote"));
        }

        if (configError is not null)
        {
            notes.Add(Loc.Get("Apps.SavedUnreadable"));
        }

        notes.Add(Loc.Get("Setup.ConfirmRecord"));
        if (!ConfirmDialog.Ask(Loc.Plural("Setup.ConfirmTitle", steps.Count), Loc.Get("Setup.ConfirmMessage"), lines, string.Join(" ", notes), Loc.Get("Setup.Apply")))
        {
            return;
        }

        if (Services.PreviewHardware)
        {
            // The design preview's GPU is a stand-in: its NVIDIA steps are shown, never run.
            steps = [.. steps.Where(s => s.Kind < StepKind.NvidiaBackup)];
            if (steps.Count == 0)
            {
                return;
            }
        }

        var started = DateTimeOffset.UtcNow;
        var nvidia = _summary?.Nvidia is { } gpu && steps.Any(s => s.Kind == StepKind.NvidiaDriver)
            ? NvidiaTarget.From(gpu.Name, gpu.Release ?? "", _lookup?.Release?.Version, CurrentConfig())
            : null;
        var setup = PendingSetup.Create(SetupRun.NewId(started, "setup"), SetupRun.Stamp(started), Selected?.Preset.Id,
            _services.Settings.ConfigureAfterInstall, steps, nvidia);
        if (Selected is { } outcome && steps.Any(s => s.Kind == StepKind.Windows && s.Items.Any(id => outcome.Preset.OperationIds.Contains(id, StringComparer.OrdinalIgnoreCase))))
        {
            _services.Settings.Outcome = outcome.Preset.Id;
            _services.SaveSettings();
        }

        _startedHere.Add(setup.RunId);
        await RunAsync(setup, bundle);
    }

    private async Task ContinueAsync()
    {
        if (_pending is not { } setup || _busy || _services.Changing)
        {
            return;
        }

        _startedHere.Add(setup.RunId);

        // The source the setup was confirmed with, not the one Apps is set to now.
        var (bundle, error) = _services.BundleFor(setup.ConfigSource);
        if (error is not null)
        {
            _services.Log($"Setup: the saved configuration couldn't be read ({error}), so apps aren't set up from it.");
        }

        await RunAsync(setup, bundle);
    }

    /// <summary>Runs the setup's pending steps in order, then shows where it stands. One run at a time.</summary>
    private async Task RunAsync(PendingSetup setup, Mesura.Core.Config.ConfigBundleFile? bundle)
    {
        if (_busy || _services.Changing)
        {
            return;
        }

        var cancel = new CancellationTokenSource();
        _cancel = cancel;
        Raise(nameof(Running));
        Busy = true;
        Progress = Loc.Get("Apps.Starting");
        var change = _services.BeginChange();
        StepKind? current = null;
        SetupSitting sitting;
        try
        {
            var token = cancel.Token;
            sitting = await Task.Run(() => SetupWorkflowRunner.Run(setup, _services.SetupContext(bundle), (kind, line) =>
            {
                if (line.Length > 0)
                {
                    _services.Log($"  [{RunFollowUp.SetupStepName(kind)}] {line}");
                }

                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (!cancel.IsCancellationRequested)
                    {
                        // The step in the user's language; the tools' own lines (English) are in the session log.
                        Progress = Loc.Format("Setup.ProgressStep", RunActions.SetupStepName(kind));
                    }

                    // The card of the step that runs now says so; the one before it goes quiet until the run's result.
                    if (current != kind)
                    {
                        foreach (var section in Sections.Where(s => current is { } previous && s.Key == SectionOf(previous) && s.Key != SectionOf(kind)))
                        {
                            section.ShowRun("", Tone.Neutral);
                        }

                        current = kind;
                        foreach (var section in Sections.Where(s => s.Key == SectionOf(kind)))
                        {
                            section.ShowRun(Loc.Get("Setup.State.Running"), Tone.Info);
                        }
                    }
                });
            }, token));
        }
        catch
        {
            // Whatever the runner saved is the truth: show the unfinished setup as the store has it.
            _pending = SetupWorkflowRunner.Pending(_services.Store) ?? _pending;
            UpdatePending();
            throw;
        }
        finally
        {
            change.Dispose();
            cancel.Dispose();
            if (_cancel == cancel)
            {
                _cancel = null;
            }

            Raise(nameof(Running));
            Progress = "";
            Busy = false;
        }

        _pending = sitting.Setup.Finished ? null : sitting.Setup;
        _services.Log($"Setup {sitting.Setup.RunId}: " + string.Join("; ", sitting.Setup.Steps.Select(s => $"{RunFollowUp.SetupStepName(s.Kind)} {s.State}{(s.Detail.Length > 0 ? " (" + s.Detail + ")" : "")}")));
        var recorded = sitting.RecordError is null && _services.Store.ReadRunRecord(sitting.Setup.RunId) is not null;
        if (recorded)
        {
            _services.Recorded(sitting.Run);
        }
        else if (sitting.Run.Apps.Count > 0)
        {
            _apps.Refresh.Execute(null); // the apps it installed are installed, recorded or not
        }

        ShowResult(sitting);
        UpdatePending();
        if (WaitingForDriver)
        {
            _driverNow = null;
            _watch.Start();
        }
        else
        {
            _watch.Stop();
        }

        await Task.WhenAll(ReadRuntimesAsync(), ReadNvidiaToolsAsync());
    }

    private void ShowResult(SetupSitting sitting)
    {
        var setup = sitting.Setup;
        SetupRun? record = null;
        try
        {
            record = _services.Store.ReadRunRecord(setup.RunId) is { } json ? SetupRun.FromJson(json) : null;
        }
        catch (FormatException)
        {
            record = null;
        }

        var run = record ?? sitting.Run;
        var lines = RunActions.FollowUps(_services, run).Select(f => f.Text).ToList();
        lines.AddRange(sitting.KeptSettings.Select(id => Loc.Format("Apps.KeptSettings", RunActions.AppName(_services, id))));
        var technical = new List<string> { RunActions.TechnicalLines(run) };
        if (sitting.RecordError is { } error)
        {
            lines.Add(Loc.Get("Common.RecordFailed"));
            technical.Add(error);
        }

        if (sitting.SaveError is { } saveError)
        {
            lines.Add(Loc.Get("Setup.SaveFailed"));
            technical.Add(saveError);
        }

        var failed = setup.Steps.Count(s => s.State == StepState.Failed);
        var (tone, title) = setup.Next is { State: StepState.Waiting }
            ? (Tone.Info, Loc.Get("Setup.ResultWaiting"))
            : !setup.Finished ? (Tone.Warn, Loc.Get("Setup.ResultStopped"))
            : failed > 0 ? (Tone.Warn, Loc.Plural("Setup.ResultPartial", failed))
            : (Tone.Ok, Loc.Get("Setup.ResultDone"));
        Result.Show(tone, title, string.Join("\n", lines.Where(l => l.Length > 0).Distinct()),
            record is not null && setup.Finished && RunActions.CanUndo(run) ? () => _ = UndoAsync(run) : null,
            record is not null ? () => _openActivity(run.Id) : null, string.Join("\n", technical.Where(t => t.Length > 0)));
    }

    private async Task UndoAsync(SetupRun run)
    {
        if (_services.Changing || !ConfirmDialog.Ask(Loc.Get("Undo.ConfirmTitle"), Loc.Get("Setup.UndoMessage"), confirm: Loc.Get("Undo.Action")))
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
    /// While the driver step waits, the installed driver is read every few seconds. In the sitting that started the step
    /// the setup carries on by itself (the user confirmed the whole plan) once the driver reads newer than before on two
    /// readings in a row and NVCleanstall has been closed, so the restore never races NVIDIA's installer; otherwise, and
    /// after a restart, the user says when it is done.
    /// </summary>
    private async Task WatchDriverAsync()
    {
        if (_watching || _finishing || _busy || _services.Changing || !WaitingForDriver || _pending is not { } setup)
        {
            return;
        }

        _watching = true;
        try
        {
            var reading = await Task.Run(() => SystemSummaryReader.ReadNvidia()?.FirstOrDefault()?.Release);
            if (reading is null || !ReferenceEquals(_pending, setup) || !WaitingForDriver || _finishing || _busy || _services.Changing)
            {
                return;
            }

            var settled = reading == _driverNow;
            _driverNow = reading;
            Raise(nameof(WaitingWatch));
            if (settled && NvidiaDriver.IsNewer(reading, setup.Nvidia?.DriverAtStart) && _startedHere.Contains(setup.RunId) && !NvCleanstallOpen())
            {
                await FinishDriverAsync(skipped: false);
            }
        }
        finally
        {
            _watching = false;
        }
    }

    private static bool NvCleanstallOpen()
    {
        var running = System.Diagnostics.Process.GetProcessesByName("NVCleanstall");
        foreach (var process in running)
        {
            process.Dispose();
        }

        return running.Length > 0;
    }

    /// <summary>
    /// Records how the driver step ended, then carries the setup on (the restore is part of the plan the user confirmed).
    /// One completion at a time: the watcher and both buttons are held off until it has saved.
    /// </summary>
    private async Task FinishDriverAsync(bool skipped)
    {
        if (_finishing || _busy || _services.Changing || !WaitingForDriver || _pending is not { } setup)
        {
            return;
        }

        _finishing = true;
        _watch.Stop();
        RaiseCommands();
        try
        {
            var now = skipped ? null : await Task.Run(() => SystemSummaryReader.ReadNvidia()?.FirstOrDefault()?.Release);
            if (!ReferenceEquals(_pending, setup))
            {
                return;
            }

            if (!skipped && (now is null || !NvidiaDriver.IsNewer(now, setup.Nvidia?.DriverAtStart))
                && !ConfirmDialog.Ask(Loc.Get("Setup.DriverUnchangedTitle"), Loc.Format("Setup.DriverUnchanged", now ?? "?"), confirm: Loc.Get("Setup.DriverMarkDone")))
            {
                _watch.Start();
                return;
            }

            var sitting = SetupWorkflowRunner.FinishDriver(setup, _services.SetupContext(null), now, skipped);
            _pending = sitting.Setup;
            if ((sitting.SaveError ?? sitting.RecordError) is { } error)
            {
                _services.Log($"Setup: the driver step's end couldn't be saved or recorded: {error}");
            }

            UpdatePending();
            if (await Task.Run(SystemSummaryReader.ReadNvidia) is { } adapters && _summary is { } summary)
            {
                _summary = summary with { NvidiaAdapters = adapters };
                Raise(nameof(Facts));
            Raise(nameof(ShowRuntimes));
                UpdateNvidia();
            }
        }
        finally
        {
            _finishing = false;
            RaiseCommands();
        }

        if (!WaitingForDriver)
        {
            await ContinueAsync();
        }
    }

    private void Reopen()
    {
        if (_nvCleanstall is { } path && _services.Nvidia.LaunchNvCleanstall(path) is { } error)
        {
            Result.Show(Tone.Danger, Loc.Get("Setup.NvCleanstallFailed"), technical: error);
        }
    }

    private void Discard()
    {
        if (_pending is null || !ConfirmDialog.Ask(Loc.Get("Setup.DiscardTitle"), Loc.Get("Setup.DiscardMessage"), confirm: Loc.Get("Setup.Discard")))
        {
            return;
        }

        try
        {
            SetupWorkflowRunner.Discard(_services.Store);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            Result.Show(Tone.Danger, Loc.Get("Common.Unexpected"), technical: e.Message);
            return;
        }

        _watch.Stop();
        _pending = null;
        UpdatePending();
        Rebuild();
    }

    private static string Started(PendingSetup setup) =>
        DateTimeOffset.TryParse(setup.StartedUtc, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var started)
            ? started.ToLocalTime().ToString("d MMM, HH:mm", Loc.Instance.Culture)
            : setup.StartedUtc;

    private void Fail(Exception e)
    {
        Busy = false;
        _services.Log($"Setup: {e}");
        Result.Show(Tone.Danger, Loc.Get("Common.Unexpected"), technical: e.Message);
    }

    private void RaiseCommands()
    {
        Apply?.RaiseCanExecuteChanged();
        Refresh?.RaiseCanExecuteChanged();
        Cancel?.RaiseCanExecuteChanged();
        ContinueSetup?.RaiseCanExecuteChanged();
        DiscardSetup?.RaiseCanExecuteChanged();
        DriverFinished?.RaiseCanExecuteChanged();
        SkipDriver?.RaiseCanExecuteChanged();
        ReopenNvCleanstall?.RaiseCanExecuteChanged();
    }
}
