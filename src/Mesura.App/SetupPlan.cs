using Mesura.Core.Apps;
using Mesura.Core.Nvidia;
using Mesura.Core.Setup;

namespace Mesura.App;

/// <summary>
/// A card of Setup's plan (D031): an icon, a title, one line of state, a count, one switch and details on demand; while a
/// setup runs it also shows how its step stands.
/// </summary>
public sealed class PlanSection : Observable
{
    private readonly string _titleKey;
    private readonly Action<PlanSection> _changed;
    private bool _included;
    private bool _expanded;
    private bool _available;
    private string _line = "";
    private string _badge = "";
    private string _badgeTone = Tone.Neutral;
    private string _runState = "";
    private string _runTone = Tone.Neutral;

    public PlanSection(string key, string iconKey, string titleKey, bool included, Action<PlanSection> changed)
    {
        Key = key;
        _titleKey = titleKey;
        _included = included;
        _changed = changed;
        Glyph = System.Windows.Application.Current?.TryFindResource(iconKey) as string ?? "";
    }

    /// <summary><c>windows</c>, <c>apps</c>, <c>runtimes</c> or <c>nvidia</c>.</summary>
    public string Key { get; }

    public string Glyph { get; }

    public string Title => Loc.Get(_titleKey);

    /// <summary>Whether the setup takes this card's step (the user's switch).</summary>
    public bool Included
    {
        get => _included;
        set
        {
            if (_included != value)
            {
                _included = value;
                Raise();
                Raise(nameof(Active));
                _changed(this);
            }
        }
    }

    /// <summary>Whether the card has anything to do on this PC; when it hasn't, its switch is hidden.</summary>
    public bool Available => _available;

    /// <summary>Included and with something to do: the step is in the plan.</summary>
    public bool Active => _included && _available;

    public bool Expanded
    {
        get => _expanded;
        set => Set(ref _expanded, value);
    }

    public string Line => _line;

    public string Badge => _badge;

    public string BadgeTone => _badgeTone;

    /// <summary>How the card's step stands in the setup that is running or waiting (empty otherwise).</summary>
    public string RunState => _runState;

    public string RunTone => _runTone;

    public void Update(string line, string badge, string badgeTone, bool available)
    {
        _line = line;
        _badge = badge;
        _badgeTone = badgeTone;
        _available = available;
        Raise(string.Empty);
    }

    public void ShowRun(string state, string tone)
    {
        _runState = state;
        _runTone = tone;
        Raise(nameof(RunState));
        Raise(nameof(RunTone));
    }

    public void Relocalize() => Raise(string.Empty);
}

/// <summary>A Visual C++ runtime on the Runtimes card: installed (with its version) or missing, and ticked or not.</summary>
public sealed class RuntimeRow(VcRuntime runtime, string? installed, bool included, Action changed) : Observable
{
    private bool _included = included;

    public VcRuntime Runtime { get; } = runtime;

    public string? InstalledVersion { get; } = installed;

    public bool Installed => InstalledVersion is not null;

    public string Name => $"Visual C++ {Runtime.Year} · {Runtime.Arch}";

    public string Note => Runtime.Supported ? Loc.Get("Setup.RuntimeSupported") : Loc.Get("Setup.RuntimeLegacy");

    /// <summary>Only a missing runtime can be ticked; an installed one is never reinstalled, upgraded or removed.</summary>
    public bool CanInclude => !Installed;

    public bool Included
    {
        get => _included && CanInclude;
        set
        {
            if (CanInclude && _included != value)
            {
                _included = value;
                Raise();
                changed();
            }
        }
    }

    public string StateText => Installed
        ? (InstalledVersion!.Length > 0 ? Loc.Format("Setup.RuntimeInstalled", InstalledVersion) : Loc.Get("Apps.Installed"))
        : Loc.Get("Setup.RuntimeMissing");

    public string StateTone => Installed ? Tone.Ok : Tone.Neutral;

    public void Relocalize() => Raise(string.Empty);
}

/// <summary>An NVIDIA driver component on the NVIDIA card: kept or left out, by NVCleanstall's label, with a short note.</summary>
public sealed class ComponentRow(NvidiaComponent component, bool included, Action changed) : Observable
{
    private bool _included = included || component.Required;

    public NvidiaComponent Component { get; } = component;

    /// <summary>NVCleanstall's own label, so the checklist reads as its window does.</summary>
    public string Name => Component.Label;

    public string Rule => Loc.Get($"Nvidia.Rule.{Component.Id}");

    public bool Required => Component.Required;

    public bool CanChange => !Component.Required;

    public bool Included
    {
        get => _included;
        set
        {
            if (CanChange && _included != value)
            {
                _included = value;
                Raise();
                changed();
            }
        }
    }

    public void Relocalize() => Raise(string.Empty);
}

/// <summary>One of NVCleanstall's installation tweaks on the NVIDIA card: ticked or not, by its label, with a short note.</summary>
public sealed class TweakRow(NvidiaTweak tweak, bool included, Action changed) : Observable
{
    private bool _included = included;

    public NvidiaTweak Tweak { get; } = tweak;

    public string Name => Tweak.Label;

    public string Rule => Loc.Get($"Nvidia.Tweak.{Tweak.Id}");

    public bool Included
    {
        get => _included;
        set
        {
            if (_included != value)
            {
                _included = value;
                Raise();
                changed();
            }
        }
    }

    public void Relocalize() => Raise(string.Empty);
}

/// <summary>A step of an unfinished setup, as the Continue card lists it.</summary>
public sealed record StepChip(string Name, string State, string Tone);

/// <summary>How a setup step stands, in the user's words and the pill's tone.</summary>
public static class StepStates
{
    public static StepChip Chip(SetupStep step) =>
        new(RunActions.SetupStepName(step.Kind), Loc.Get($"Setup.State.{step.State}"), Tone.Of(RunActions.SetupStateWord(step.State)));
}
