using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Tasks;
using Mesura.Core.Windows;

namespace Mesura.App;

/// <summary>
/// How much a change can affect the PC, shown apart from the recommendation and the benefit (quality pass, after
/// another tweak tool's per-tweak risk label): low for a preference or a Windows default put back, moderate for a setting of the
/// whole PC, higher for an undocumented knob or something that turns part of Windows off.
/// </summary>
public enum RiskLevel
{
    Low,
    Moderate,
    High,
}

/// <summary>How the app classifies an entry for browsing (redesign §4): never a benefit claim.</summary>
public enum Recommendation
{
    /// <summary>Part of one of the two outcomes Optimize offers.</summary>
    Recommended,

    /// <summary>Documented and reversible, but a matter of preference or of a specific need.</summary>
    Optional,

    /// <summary>Undocumented by Microsoft: chosen one at a time, never in a preset.</summary>
    Advanced,

    /// <summary>A procedure carried out by hand, or a security setting changed in Windows itself.</summary>
    Guide,
}

/// <summary>One catalog entry as the app shows it: its state here, in the user's language, with its details on demand.</summary>
public sealed class SettingRow : Observable
{
    private readonly Services _services;
    private OperationEvaluation _eval;
    private bool _checking = true;
    private bool _selected;

    public SettingRow(Services services, WindowsOperation op, Recommendation recommendation)
    {
        _services = services;
        _eval = new OperationEvaluation(op, OperationState.Unknown, "", []);
        Recommendation = recommendation;
    }

    public WindowsOperation Operation => _eval.Operation;

    public string Id => Operation.Id;

    public Recommendation Recommendation { get; }

    /// <summary>Whether it belongs to the Debloat page (bundled apps, AI features, startup apps) rather than Tweaks.</summary>
    public bool InDebloat => Operation.Category is WindowsCatalog.BundledAppsCategory or WindowsCatalog.AiCategory or WindowsCatalog.StartupCategory;

    public OperationState State => _eval.State;

    public bool Checking => _checking;

    public string Name => Loc.Name(Operation);

    /// <summary>What assistive technology reads for this item: its display name, never the type name.</summary>
    public override string ToString() => Name;

    public string Summary => Loc.Summary(Operation);

    public string Category => Loc.Category(Operation.Category);

    public OperationImpact Impact => Operation.Impact;

    public string ImpactText => Loc.Impact(Operation.Impact);

    /// <summary>The impact's place in the list order (Performance first), for sorting All settings by impact.</summary>
    public int ImpactOrder => (int)Operation.Impact;

    /// <summary>Its place in the catalog, and its catalog category's first place, so lists keep the catalog's order.</summary>
    public int CatalogIndex { get; init; }

    public int CategoryIndex { get; init; }

    /// <summary>
    /// Where it sorts inside its group: what can act here first, advanced (undocumented) entries after, entries that don't
    /// apply to this PC last, so the default view leads with what is useful.
    /// </summary>
    public int SortRank => !_checking && _eval.State == OperationState.Inapplicable ? 2 : Recommendation == Recommendation.Advanced ? 1 : 0;

    public string RecommendationText => Loc.Get($"Recommendation.{Recommendation}");

    /// <summary>Whether it can be ticked: an applicable entry not yet in Mesura's target state.</summary>
    public bool CanSelect => !_checking && Operation.Applicable && _eval.State is OperationState.NotApplied or OperationState.Partial;

    public bool Selected
    {
        get => _selected;
        set => Set(ref _selected, value && CanSelect);
    }

    /// <summary>
    /// The status, in one of the words every page shares: already set, applied just now (this session), available, partly
    /// set, failed or waiting for administrator rights (this session's run), not for this PC, couldn't check, unavailable,
    /// manual step, check in Windows.
    /// </summary>
    public string StatusKey
    {
        get
        {
            if (_checking)
            {
                return "Checking";
            }

            var session = _services.SessionWindows(Id);
            return _eval.State switch
            {
                OperationState.Applied when session == WindowsOpStatus.Applied => "AppliedNow",
                OperationState.Applied when Operation.AppxSettings.Count > 0 => "NotInstalled",
                OperationState.Applied when Operation.Category == WindowsCatalog.StartupCategory => "TurnedOff",
                OperationState.NotApplied when Operation.Category == WindowsCatalog.StartupCategory => "StartsUp",
                OperationState.NotApplied or OperationState.Partial when session == WindowsOpStatus.Failed => "Failed",
                OperationState.NotApplied or OperationState.Partial when session == WindowsOpStatus.NeedsElevation => "NeedsAdmin",
                var state => state.ToString(),
            };
        }
    }

    public string StateText => Loc.Get($"State.{StatusKey}");

    public string StateTone => StatusKey switch
    {
        "Checking" or "Inapplicable" => Tone.Muted,
        "Applied" or "AppliedNow" or "NotInstalled" or "TurnedOff" => Tone.Ok,
        "NotApplied" => Tone.Neutral,
        "Partial" or "NeedsAdmin" or "Blocked" or "Unknown" => Tone.Warn,
        "Failed" => Tone.Danger,
        _ => Tone.Info,
    };

    /// <summary>How much it can affect the PC (shown only when it isn't low).</summary>
    public RiskLevel Risk =>
        !Operation.Applicable ? RiskLevel.Low
        : Operation.Level is { } stated ? stated switch { OperationLevel.Safe => RiskLevel.Low, OperationLevel.Moderate => RiskLevel.Moderate, _ => RiskLevel.High }
        : Operation.Risk == RiskClass.Undocumented || TurnsOff ? RiskLevel.High
        : Operation.Category == WindowsCatalog.DefaultsCategory || !Operation.MachineWide ? RiskLevel.Low
        : RiskLevel.Moderate;

    public bool ShowRisk => Risk != RiskLevel.Low;

    /// <summary>The caution shown in a box in the details, for a higher-risk entry only.</summary>
    public string HighRiskNote => Risk == RiskLevel.High ? Caution : "";

    public string RiskText => Loc.Get($"Risk.{Risk}");

    public string RiskTone => Risk == RiskLevel.High ? Tone.Warn : Tone.Neutral;

    /// <summary>The colour of the risk label on a card: green for low, amber for moderate, red for higher.</summary>
    public string RiskLabelTone => Risk switch { RiskLevel.High => Tone.Danger, RiskLevel.Moderate => Tone.Warn, _ => Tone.Ok };

    /// <summary>The risk label, shown on every card for an entry that can be applied (guides carry none).</summary>
    public bool ShowRiskLabel => Operation.Applicable;

    /// <summary>Guides carry no risk label, so their card names its category instead.</summary>
    public bool ShowCategoryLabel => !Operation.Applicable;

    public bool IsRecommended => Recommendation == Recommendation.Recommended;

    /// <summary>Why it has that risk, in one sentence.</summary>
    public string RiskWhy => Risk switch
    {
        RiskLevel.High => Caution,
        RiskLevel.Moderate => Operation.PowerPlan is not null || Operation.PowerSettings.Count > 0 ? Loc.Get("Caution.Power") : Loc.Get("Risk.ModerateWhy"),
        _ => Operation.Applicable ? Loc.Get("Risk.LowWhy") : "",
    };

    /// <summary>What is known about its benefit: a preference, documented, plausible but unverified, measured.</summary>
    public string BenefitText => Loc.Get($"Benefit.{Operation.Evidence}");

    /// <summary>How the stored value is known to be right; empty when the catalog doesn't say (nothing is claimed).</summary>
    public string ValueBasisText => Operation.Applicable && Operation.Basis is { } basis ? Loc.Get($"ValueBasis.{basis}") : "";

    private bool TurnsOff => Operation.Services.Any(s => s.Start == ServiceStart.Disabled) || Operation.Features.Any(f => !f.Enabled)
        || Operation.Tasks.Any(t => !t.Enabled);

    /// <summary>Why it doesn't apply, is blocked or unread, in a few words (the full reason is in the details).</summary>
    public string ShortReason => _eval.State switch
    {
        OperationState.Inapplicable => InapplicableReason(),
        OperationState.Blocked => Loc.Get("Reason.Blocked"),
        OperationState.Unknown when !_checking => Loc.Get("Reason.Unknown"),
        OperationState.Partial => Loc.Get("Reason.Partial"),
        _ => "",
    };

    private string InapplicableReason()
    {
        var reader = _services.Reader;
        if ((Operation.MinBuild is { } min && reader.Build < min) || (Operation.MaxBuild is { } max && reader.Build > max))
        {
            return Loc.Get("Reason.Build");
        }

        if (Operation.Requirements.Contains(DeviceRequirement.NoBattery) && reader.HasBattery == true)
        {
            return Loc.Get("Reason.Battery");
        }

        if (Operation.Requirements.Contains(DeviceRequirement.NoModernStandby) && reader.ModernStandby == true)
        {
            return Loc.Get("Reason.ModernStandby");
        }

        if (Operation.Requirements.Contains(DeviceRequirement.ProEdition) && reader.PolicyEdition == false)
        {
            return Loc.Get("Reason.Edition");
        }

        if (Operation.Requirements.Contains(DeviceRequirement.Ram16Gb) && reader.PhysicalMemoryBytes < 15UL * 1024 * 1024 * 1024)
        {
            return Loc.Get("Reason.Memory");
        }

        if (Operation.Requirements.Contains(DeviceRequirement.HybridCpu) && reader.HybridCpu == false)
        {
            return Loc.Get("Reason.Hybrid");
        }

        return Loc.Get("Reason.Absent");
    }

    /// <summary>Needs a restart to take effect.</summary>
    public bool NeedsRestart => Operation.Applicable && Operation.Activation == Activation.Reboot;

    /// <summary>"Needs administrator", shown only when Mesura runs without elevation (the prompt was declined, or a per-user install never asks).</summary>
    public bool NeedsAdmin => Operation.Applicable && Operation.NeedsElevation && !_services.Reader.IsElevated;

    /// <summary>Why it needs administrator rights and how to get them (the tag's tooltip): restart as administrator, or, in a per-user install, install for all users.</summary>
    public string NeedsAdminWhy => Loc.Get(Loc.AdminKey("Common.NeedsAdminWhy"));

    /// <summary>A caution where it matters: undocumented, or it turns part of Windows off. Empty otherwise.</summary>
    public string Caution =>
        Operation.Risk == RiskClass.Undocumented ? Loc.Get("Caution.Undocumented")
        : TurnsOff ? Loc.Get("Caution.TurnsOff")
        : Operation.PowerPlan is not null || Operation.PowerSettings.Count > 0 ? Loc.Get("Caution.Power")
        : "";

    // ---- Details (on demand) ---------------------------------------------------------------------------------------

    public string Description => Loc.Description(Operation);

    public string Limitations => Loc.Limitations(Operation);

    public string When => Operation.Applicable ? Loc.Get($"When.{Operation.Activation}") : "";

    public string Scope => (Operation.MachineWide, Operation.PerUser) switch
    {
        (true, true) => Loc.Get("Scope.Both"),
        (true, false) => Loc.Get("Scope.Machine"),
        (false, true) => Loc.Format("Scope.User", _services.Reader.User.Account is { Length: > 0 } a ? a : Loc.Get("Scope.YourAccount")),
        _ => "",
    };

    public IReadOnlyList<string> Steps => Loc.Steps(Operation);

    public string Consequence => Loc.Consequence(Operation);

    public string Restore => Loc.Restore(Operation);

    public string Source => Operation.Source;

    /// <summary>The technical changes: each target, its current value and the value Mesura would set.</summary>
    public IReadOnlyList<string> Changes
    {
        get
        {
            static string Shown(string value) => value.Length == 0 ? "(empty)" : value;
            return [.. _eval.Items.Select(i => Operation.Observation is not null
                ? $"{i.Target} = {Shown(i.Current)}"
                : !i.Present ? $"{i.Target}: {Loc.Get("Details.NotPresent")}"
                : i.Matches ? $"{i.Target}: {Shown(i.Current)} ✓"
                : $"{i.Target}: {Shown(i.Current)} → {Shown(i.Desired)}")];
        }
    }

    /// <summary>The full reason the evaluator gave (English, technical).</summary>
    public string TechnicalReason => _eval.Reason;

    /// <summary>The Windows page or guide to open, when there is one Mesura opens.</summary>
    public string? OpenTarget =>
        Operation.Observation is { } o && ShellLauncher.CanOpen(o.OpenUri) ? o.OpenUri
        : Operation.Manual?.Links.FirstOrDefault(ShellLauncher.CanOpen);

    public string OpenLabel => Operation.Observation is not null ? Loc.Get("Details.OpenInWindows") : Loc.Get("Details.OpenGuide");

    public void Update(OperationEvaluation evaluation)
    {
        _eval = evaluation;
        _checking = false;
        if (!CanSelect)
        {
            _selected = false;
        }

        Raise(string.Empty);
    }

    /// <summary>Re-reads every text (the language changed).</summary>
    public void Relocalize() => Raise(string.Empty);
}

/// <summary>
/// The state of every catalog entry on this PC, shared by Optimize and All settings so both show the same reading. It
/// is read off the UI thread (about a second) and again after every run that changes Windows settings.
/// </summary>
public sealed class WindowsStateModel : Observable
{
    private readonly Services _services;
    private bool _busy;
    private bool _loaded;

    public WindowsStateModel(Services services)
    {
        _services = services;
        if (Catalog is { } catalog)
        {
            // "Recommended" is the first primary preset's list (Recommended), not Maximum performance's: what only Maximum adds is Optional.
            var recommended = (catalog.Presets.FirstOrDefault(p => p.Tier == PresetTier.Primary)?.OperationIds ?? [])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var categoryIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < catalog.Operations.Count; i++)
            {
                var op = catalog.Operations[i];
                var recommendation = !op.Applicable ? Recommendation.Guide
                    : op.Risk == RiskClass.Undocumented ? Recommendation.Advanced
                    : recommended.Contains(op.Id) ? Recommendation.Recommended
                    : Recommendation.Optional;
                categoryIndex.TryAdd(op.Category, i);
                Rows.Add(new SettingRow(services, op, recommendation) { CatalogIndex = i, CategoryIndex = categoryIndex[op.Category] });
            }
        }

        Loc.Instance.LanguageChanged += () =>
        {
            foreach (var row in Rows)
            {
                row.Relocalize();
            }
        };
        services.RunRecorded += operation =>
        {
            if (operation is "windows-apply" or "windows-revert" or "undo" or "profile" or "setup")
            {
                _ = RefreshAsync();
            }
        };
    }

    public WindowsCatalog? Catalog => _services.WindowsCatalog.Catalog;

    public ObservableCollection<SettingRow> Rows { get; } = [];

    public bool Busy
    {
        get => _busy;
        private set => Set(ref _busy, value);
    }

    /// <summary>Whether the state has been read at least once.</summary>
    public bool Loaded
    {
        get => _loaded;
        private set => Set(ref _loaded, value);
    }

    /// <summary>Raised on the UI thread after each reading.</summary>
    public event Action? Updated;

    public SettingRow? Row(string id) => Rows.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public async Task RefreshAsync()
    {
        if (Busy || Rows.Count == 0)
        {
            return;
        }

        Busy = true;
        try
        {
            var reader = _services.Reader;
            var operations = Rows.Select(r => r.Operation).ToList();
            var evaluations = await Task.Run(() =>
            {
                reader.Refresh();
                return operations.Select(op => WindowsEvaluator.Evaluate(op, reader)).ToList();
            });
            for (var i = 0; i < Rows.Count; i++)
            {
                Rows[i].Update(evaluations[i]);
            }

            Loaded = true;
            _services.Log($"Windows: {Rows.Count} settings read; {Rows.Count(r => r.CanSelect)} can be applied ({Services.Describe(_services.WindowsCatalog.Source)}).");
            Updated?.Invoke();
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>The entries of a preset, in its order.</summary>
    public IReadOnlyList<SettingRow> RowsOf(IEnumerable<string> ids) => [.. ids.Select(Row).OfType<SettingRow>()];

    public static string Count(int n) => n.ToString(CultureInfo.CurrentCulture);
}
