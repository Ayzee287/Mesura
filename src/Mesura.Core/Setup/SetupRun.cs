using System.Text.Json;
using System.Text.Json.Serialization;
using Mesura.Core.Apps;
using Mesura.Core.Windows;
using Mesura.Core.Support;

namespace Mesura.Core.Setup;

/// <summary>What happened to one application's settings during a config import.</summary>
public enum ConfigImportStatus
{
    /// <summary>Settings were written (or, in a preview, would be).</summary>
    Applied,

    /// <summary>The app's files already held every value; nothing was written.</summary>
    Unchanged,

    /// <summary>The app was running, so nothing was written; close it and re-run.</summary>
    SkippedAppRunning,

    /// <summary>Reading or writing the app's files failed; <see cref="ConfigImportOutcome.Detail"/> says why.</summary>
    Failed,

    /// <summary>
    /// An undo found the file changed since the run it undoes wrote it (or that run is too old to tell), so it left the
    /// file as it is rather than overwrite the later changes.
    /// </summary>
    ChangedSince,
}

/// <summary>
/// A config file the import replaced, by its port source id (no path), the name of its backup copy, and the SHA-256
/// (hex) of the bytes it wrote, so an undo puts the backup back only while the file is still exactly as the run left
/// it. Records from before 2026-10-23 have no fingerprint.
/// </summary>
public sealed record WrittenConfigFile(string SourceId, string BackupName, string? Sha256 = null);

/// <summary>
/// The result of importing one application's settings: which settings applied, which waited on a scope (e.g. a Steam
/// account not signed in yet) or on a missing destination file, and which files were replaced. It holds setting ids
/// and source ids only — no values and no paths — so a history record stays private and portable.
/// </summary>
public sealed record ConfigImportOutcome(
    string PortId,
    string AppName,
    ConfigImportStatus Status,
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> WaitingOnScope,
    IReadOnlyList<string> DestinationMissing,
    IReadOnlyList<WrittenConfigFile> Written,
    string Detail = "");

/// <summary>One recorded application install.</summary>
public sealed record AppRunEntry(string AppId, string WingetId, InstallStatus Status, string Detail)
{
    public static AppRunEntry From(InstallOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return new AppRunEntry(outcome.AppId, outcome.WingetId, outcome.Status, outcome.Detail);
    }
}

/// <summary>
/// One entry of the setup history: a run that changed (or tried to change) this machine — an app install batch, a
/// config import, or a whole profile. Previews are not recorded, because they change nothing. Times are supplied by
/// the caller (Core reads no clock).
/// </summary>
public sealed record SetupRun(
    string Kind,
    int SchemaVersion,
    string Id,
    string Operation,
    string? ProfileName,
    string StartedUtc,
    string FinishedUtc,
    IReadOnlyList<AppRunEntry> Apps,
    IReadOnlyList<ConfigImportOutcome> Config,
    IReadOnlyList<WindowsOpOutcome>? Windows = null,
    IReadOnlyList<SetupStepOutcome>? Steps = null)
{
    /// <summary>The Windows configuration steps; records written before that layer existed have none.</summary>
    [JsonIgnore]
    public IReadOnlyList<WindowsOpOutcome> WindowsSteps => Windows ?? [];

    /// <summary>A setup's other steps (the NVIDIA steps, D031); records written before setups existed have none.</summary>
    [JsonIgnore]
    public IReadOnlyList<SetupStepOutcome> SetupSteps => Steps ?? [];

    public const string FileKind = "Mesura.SetupRun";
    public const int CurrentSchema = 1;

    public static SetupRun Create(
        string id, string operation, string? profileName, string startedUtc, string finishedUtc,
        IEnumerable<InstallOutcome> apps, IEnumerable<ConfigImportOutcome> config, IEnumerable<WindowsOpOutcome>? windows = null,
        IEnumerable<SetupStepOutcome>? steps = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(apps);
        ArgumentNullException.ThrowIfNull(config);
        var setupSteps = steps?.ToList();
        return new SetupRun(FileKind, CurrentSchema, id, operation, profileName, startedUtc, finishedUtc,
            [.. apps.Select(AppRunEntry.From)], [.. config], [.. windows ?? []], setupSteps is { Count: > 0 } ? setupSteps : null);
    }

    /// <summary>
    /// A setup that continued in a later sitting, as one record: this record's start, the later finish, the earlier
    /// entries followed by the later ones. A later entry for the same app, app settings, Windows operation or step replaces
    /// the earlier one (a retried install, the NVIDIA driver step that waited and then finished), except when the earlier
    /// one changed something and the later one only found it done: a step re-run after an interruption reads what the first
    /// sitting applied as already applied or installed, and the first sitting's before-images, backups and "installed"
    /// stay, so Undo and the restart reminders still cover them.
    /// </summary>
    public SetupRun Then(SetupRun later)
    {
        ArgumentNullException.ThrowIfNull(later);
        static List<T> Merge<T>(IEnumerable<T> earlier, IReadOnlyList<T> later, Func<T, string> key, Func<T, T, bool> keepEarlier)
        {
            var merged = earlier.ToList();
            foreach (var entry in later)
            {
                var index = merged.FindIndex(e => key(e) == key(entry));
                if (index < 0)
                {
                    merged.Add(entry);
                }
                else if (!keepEarlier(merged[index], entry))
                {
                    merged.RemoveAt(index);
                    merged.Add(entry);
                }
            }

            return merged;
        }

        var steps = Merge(SetupSteps, later.SetupSteps, s => s.Kind.ToString(), (_, _) => false);
        return this with
        {
            FinishedUtc = later.FinishedUtc,
            Apps = Merge(Apps, later.Apps, a => a.AppId, (e, l) =>
                e.Status is InstallStatus.Succeeded or InstallStatus.SucceededRebootRequired && l.Status == InstallStatus.AlreadyInstalled),
            Config = Merge(Config, later.Config, c => c.PortId, (e, l) => e.Written.Count > 0 && l.Written.Count == 0
                && l.Status is ConfigImportStatus.Unchanged or ConfigImportStatus.ChangedSince),
            Windows = Merge(WindowsSteps, later.WindowsSteps, w => w.OperationId, (e, l) => e.Changes.Count > 0 && l.Changes.Count == 0
                && l.Status is WindowsOpStatus.AlreadyApplied or WindowsOpStatus.Inapplicable),
            Steps = steps.Count > 0 ? steps : null,
        };
    }

    /// <summary>A run id: the UTC start time, sortable, then the operation, e.g. <c>20261018T101500Z-profile</c>.</summary>
    public static string NewId(DateTimeOffset startedUtc, string operation) =>
        $"{startedUtc.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}-{operation}";

    /// <summary>A round-trip UTC time stamp for a record.</summary>
    public static string Stamp(DateTimeOffset time) =>
        time.UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Whether every step ended well (installed, already there, manual, applied, unchanged or reverted).</summary>
    [JsonIgnore]
    public bool Ok =>
        Apps.All(a => a.Status is InstallStatus.Succeeded or InstallStatus.SucceededRebootRequired
            or InstallStatus.AlreadyInstalled or InstallStatus.Manual)
        && Config.All(c => c.Status is ConfigImportStatus.Applied or ConfigImportStatus.Unchanged or ConfigImportStatus.ChangedSince)
        && WindowsSteps.All(w => w.Ok)
        && SetupSteps.All(s => s.Ok);

    /// <summary>
    /// Whether the run changed nothing and nothing waits or failed: every app was already installed (or is installed by
    /// hand), every import found its settings already in place, and every Windows step was already done or doesn't
    /// apply here. Such a re-run isn't recorded, so History lists only runs that did something or need something.
    /// </summary>
    [JsonIgnore]
    public bool ChangedNothing =>
        Apps.All(a => a.Status is InstallStatus.AlreadyInstalled or InstallStatus.Manual)
        && Config.All(c => (c.Status is ConfigImportStatus.Unchanged or ConfigImportStatus.ChangedSince)
            && c.DestinationMissing.Count == 0 && c.WaitingOnScope.Count == 0)
        && WindowsSteps.All(w => w.Status is WindowsOpStatus.AlreadyApplied or WindowsOpStatus.Inapplicable
            or WindowsOpStatus.Manual or WindowsOpStatus.Observation)
        && SetupSteps.All(s => s.State == StepState.Skipped);

    public string ToJson() => JsonSerializer.Serialize(this, SetupRunJsonContext.Default.SetupRun);

    /// <exception cref="FormatException">Not a setup run record, or a newer schema.</exception>
    public static SetupRun FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        SetupRun? run;
        try
        {
            run = JsonSerializer.Deserialize(json, SetupRunJsonContext.Default.SetupRun);
        }
        catch (JsonException e)
        {
            throw new FormatException("The history record is not valid JSON.", e);
        }

        if (run is null || !LegacyIdentity.IsKind(run.Kind, FileKind) || run.SchemaVersion > CurrentSchema)
        {
            throw new FormatException("This file is not a setup history record this build can read.");
        }

        return run;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(SetupRun))]
internal sealed partial class SetupRunJsonContext : JsonSerializerContext;
