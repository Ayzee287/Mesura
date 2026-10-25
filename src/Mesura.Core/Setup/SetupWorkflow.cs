using System.Text.Json;
using System.Text.Json.Serialization;
using Mesura.Core.Nvidia;

namespace Mesura.Core.Setup;

/// <summary>The kinds of step a setup holds, in the order a run takes them (D031).</summary>
public enum StepKind
{
    /// <summary>The missing Visual C++ runtimes, from Microsoft's winget packages.</summary>
    Runtimes,

    /// <summary>The apps the user ticked, then the set-up of those this run installed.</summary>
    Apps,

    /// <summary>The Windows settings of the plan.</summary>
    Windows,

    /// <summary>A copy of the user's customised NVIDIA profiles, before the driver changes.</summary>
    NvidiaBackup,

    /// <summary>The NVIDIA driver, installed by the user in NVCleanstall while Mesura watches the driver version.</summary>
    NvidiaDriver,

    /// <summary>The backed-up NVIDIA profiles merged back after the driver install, then read back to verify.</summary>
    NvidiaRestore,

    /// <summary>
    /// The NVIDIA profile the configuration names, made to fit this PC's GPU, merged in after the restore (whether or not
    /// the driver step installed a driver), then read back to verify (D032).
    /// </summary>
    NvidiaProfile,
}

/// <summary>Where a step stands.</summary>
public enum StepState
{
    /// <summary>Not run yet (or interrupted before it finished, so it runs again: every step is safe to repeat).</summary>
    Pending,

    /// <summary>Started and handed to the user (NVCleanstall); it finishes when the driver version changes or the user says so.</summary>
    Waiting,

    Done,

    /// <summary>Nothing to do here (no NVIDIA profiles to back up, the user skipped the driver).</summary>
    Skipped,

    Failed,
}

/// <summary>One step of a setup: its kind, the items it covers (app, runtime or operation ids), and where it stands.</summary>
/// <param name="Value">What the step produced that a later step needs (the NVIDIA backup's file name, the installed driver version).</param>
public sealed record SetupStep(StepKind Kind, IReadOnlyList<string> Items, StepState State = StepState.Pending, string Detail = "", string? Value = null)
{
    [JsonIgnore]
    public bool Finished => State is StepState.Done or StepState.Skipped or StepState.Failed;
}

/// <summary>
/// What the NVIDIA steps of a setup work towards: the GPU, the driver at the start, the newest one, and the driver
/// configuration confirmed with the plan (components, tweaks, interrupt settings, the profile to import).
/// </summary>
/// <param name="Latest">The newest driver NVIDIA's lookup offered when the plan was made, or <c>null</c> if it couldn't say.</param>
/// <param name="CleanInstall">Whether the checklist asks NVCleanstall for a clean installation.</param>
/// <param name="Tweaks">The NVCleanstall tweaks to tick (setups saved before D032 have none recorded).</param>
public sealed record NvidiaTarget(
    string Gpu,
    string DriverAtStart,
    string? Latest,
    IReadOnlyList<string> Components,
    bool CleanInstall,
    IReadOnlyList<string>? Tweaks = null,
    string InterruptPolicy = NvidiaDriverConfig.DefaultChoice,
    string InterruptPriority = NvidiaDriverConfig.DefaultChoice,
    NvidiaProfileRef? Profile = null)
{
    /// <summary>A target from the GPU, the drivers and a configuration.</summary>
    public static NvidiaTarget From(string gpu, string driverAtStart, string? latest, NvidiaDriverConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var normal = NvidiaDriverConfig.Normalize(config);
        return new NvidiaTarget(gpu, driverAtStart, latest, normal.Components, normal.CleanInstall, normal.Tweaks,
            normal.InterruptPolicy, normal.InterruptPriority, normal.Profile);
    }
}

/// <summary>
/// A setup in progress (<c>Setup\pending-setup.json</c>): the steps the user confirmed, each with its state, written
/// after every step. If the PC restarts or Mesura closes while a step waits on the user, the setup continues from the
/// first step that isn't finished; nothing finished runs again. Every step records into the same history entry
/// (<see cref="RunId"/>), so Activity shows one setup, however many sittings it took.
/// </summary>
public sealed record PendingSetup(
    string Kind,
    int SchemaVersion,
    string RunId,
    string StartedUtc,
    string? Outcome,
    string ConfigSource,
    IReadOnlyList<SetupStep> Steps,
    NvidiaTarget? Nvidia = null)
{
    public const string FileKind = "Mesura.PendingSetup";
    public const int CurrentSchema = 1;

    /// <summary>A new setup; the steps are put into run order and empty ones left out.</summary>
    public static PendingSetup Create(string runId, string startedUtc, string? outcome, string configSource, IEnumerable<SetupStep> steps, NvidiaTarget? nvidia)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(steps);
        return new PendingSetup(FileKind, CurrentSchema, runId, startedUtc, outcome, configSource,
            [.. steps.Where(s => s.Items.Count > 0 || s.Kind >= StepKind.NvidiaBackup).OrderBy(s => s.Kind)], nvidia);
    }

    /// <summary>The step to run next (a waiting one first), or <c>null</c> when every step is finished.</summary>
    [JsonIgnore]
    public SetupStep? Next => Steps.FirstOrDefault(s => s.State == StepState.Waiting) ?? Steps.FirstOrDefault(s => s.State == StepState.Pending);

    [JsonIgnore]
    public bool Finished => Steps.All(s => s.Finished);

    public SetupStep? Step(StepKind kind) => Steps.FirstOrDefault(s => s.Kind == kind);

    /// <summary>The setup with one step's state, detail and value replaced.</summary>
    public PendingSetup With(StepKind kind, StepState state, string detail = "", string? value = null) =>
        this with { Steps = [.. Steps.Select(s => s.Kind == kind ? s with { State = state, Detail = detail, Value = value ?? s.Value } : s)] };

    public string ToJson() => JsonSerializer.Serialize(this, PendingSetupJsonContext.Default.PendingSetup);

    /// <exception cref="FormatException">Not a pending setup this build can read.</exception>
    public static PendingSetup FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        PendingSetup? setup;
        try
        {
            setup = JsonSerializer.Deserialize(json, PendingSetupJsonContext.Default.PendingSetup);
        }
        catch (JsonException e)
        {
            throw new FormatException("The unfinished setup isn't valid JSON.", e);
        }

        if (setup is null || setup.Kind != FileKind || setup.SchemaVersion > CurrentSchema || setup.Steps is null || string.IsNullOrWhiteSpace(setup.RunId))
        {
            throw new FormatException("This file is not an unfinished setup this build can read.");
        }

        return setup;
    }
}

/// <summary>
/// What a setup step ended with, in its history record (<see cref="SetupRun.Steps"/>): the steps that aren't app installs,
/// imports or Windows settings, which are recorded as those. English detail for the CLI and the log; the app words each
/// step from its kind, state and value.
/// </summary>
/// <param name="Adjustments">For the NVIDIA profile import: the settings changed to fit this PC (D032), when any were.</param>
public sealed record SetupStepOutcome(StepKind Kind, StepState State, string Detail, string? Value = null, IReadOnlyList<ProfileAdjustment>? Adjustments = null)
{
    [JsonIgnore]
    public bool Ok => State is StepState.Done or StepState.Skipped;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(PendingSetup))]
internal sealed partial class PendingSetupJsonContext : JsonSerializerContext;
