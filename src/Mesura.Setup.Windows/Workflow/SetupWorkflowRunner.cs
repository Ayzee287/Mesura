using Mesura.Core.Apps;
using Mesura.Core.Config;
using Mesura.Core.Nvidia;
using Mesura.Core.Setup;
using Mesura.Core.Windows;
using Mesura.Setup.Windows.Config;
using Mesura.Setup.Windows.Nvidia;

namespace Mesura.Setup.Windows.Workflow;

/// <summary>What a setup works with: the operations it composes, the stores, and where config files are.</summary>
/// <param name="ConfigBundle">The bundle the apps step sets newly installed apps up from (Mesura's defaults or the user's saved configuration), or <c>null</c> for none.</param>
/// <param name="RefreshReader">Re-reads the system before the Windows step (its before-images come from the system as it is then).</param>
/// <param name="Locate">Where each app's config files are (tests pass temporary files); the real locations by default.</param>
/// <param name="IsRunning">Whether an app's process runs (tests decide); the real check by default.</param>
/// <param name="ReadBattery">
/// Whether this PC has a system battery now (<c>null</c> when that can't be read), for fitting the NVIDIA profile to it;
/// without it the battery counts as unread.
/// </param>
public sealed record SetupContext(
    AppInstallService Installer,
    AppCatalog Apps,
    WindowsCatalog WindowsCatalog,
    WindowsConfigurator? Windows,
    SetupStore Store,
    INvidiaTools Nvidia,
    Func<DateTimeOffset> Clock,
    ConfigBundleFile? ConfigBundle,
    Action? RefreshReader = null,
    Func<ConfigPortDefinition, IReadOnlyList<LocatedConfigFile>>? Locate = null,
    Func<string, bool>? IsRunning = null,
    Func<bool?>? ReadBattery = null);

/// <summary>One sitting of a setup: where it stands now, its history record so far, and anything to tell the user.</summary>
/// <param name="KeptSettings">Apps this sitting installed that already had settings on this PC, which were left as they were.</param>
/// <param name="RecordError">Why the history record couldn't be written, if it couldn't.</param>
/// <param name="SaveError">
/// Why the setup's progress couldn't be saved, if it couldn't: the sitting stopped there, so nothing ran unrecorded.
/// </param>
public sealed record SetupSitting(PendingSetup Setup, SetupRun Run, IReadOnlyList<string> KeptSettings, string? RecordError, string? SaveError = null);

/// <summary>
/// Runs a setup (D031, D032): the steps the user confirmed, in order — runtimes, apps and their set-up, Windows settings,
/// NVIDIA profile backup, NVIDIA driver, NVIDIA profile restore, NVIDIA profile import — with the existing operations (the winget installer,
/// <see cref="ConfigService"/>, <see cref="WindowsConfigurator"/>) and the NVIDIA tools. After every step the progress is
/// written to the setup store, so a restart or a closed window loses nothing and repeats nothing. It stops at the NVIDIA
/// driver step, which the user carries out in NVCleanstall; <see cref="FinishDriver"/> records how that ended and the
/// next sitting carries on. Every sitting adds to one history record.
/// </summary>
public static class SetupWorkflowRunner
{
    /// <summary>The name of the NVIDIA profile backup in the run's folder.</summary>
    public const string BackupFile = "nvidia-profiles-before.nip";

    /// <summary>
    /// Runs every pending step until the setup is finished, a step waits on the user, or <paramref name="cancellationToken"/>
    /// fires (the step it stopped in stays pending, to run again).
    /// </summary>
    /// <param name="operation">The history record's operation: <c>setup</c>, or <c>apps-install</c> for an install from Apps.</param>
    /// <param name="persist">
    /// Whether progress is kept as the unfinished setup. An install from Apps is one step that never waits, so it isn't,
    /// and it leaves an unfinished setup from Optimize as it was.
    /// </param>
    public static SetupSitting Run(PendingSetup setup, SetupContext context, Action<StepKind, string>? onProgress, CancellationToken cancellationToken,
        string operation = "setup", bool persist = true)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(context);
        var installs = new List<InstallOutcome>();
        var config = new List<ConfigImportOutcome>();
        var windows = new List<WindowsOpOutcome>();
        var steps = new List<SetupStepOutcome>();
        var kept = new List<string>();
        var adjusted = new List<ProfileAdjustment>();

        string? recordError = null;
        string? saveError = null;
        SetupRun Sitting() => SetupRun.Create(setup.RunId, operation, null, setup.StartedUtc, SetupRun.Stamp(context.Clock()), installs, config, windows, steps);

        // Kept before the first step too, so a setup interrupted in its first step (cancelled, or Mesura closed) continues.
        if (persist && !setup.Finished && (saveError = TrySave(setup, context.Store)) is not null)
        {
            return new SetupSitting(setup, Sitting(), kept, null, saveError);
        }

        while (setup.Next is { State: StepState.Pending } step && !cancellationToken.IsCancellationRequested)
        {
            onProgress?.Invoke(step.Kind, "");
            StepState state;
            string detail;
            string? value;
            try
            {
                (state, detail, value) = step.Kind switch
                {
                    StepKind.Runtimes => Runtimes(step, context, installs, onProgress, cancellationToken),
                    StepKind.Apps => Apps(step, setup.RunId, context, installs, config, kept, onProgress, cancellationToken),
                    StepKind.Windows => Windows(step, context, windows, cancellationToken),
                    StepKind.NvidiaBackup => Backup(setup.RunId, context, installs, onProgress, cancellationToken),
                    StepKind.NvidiaDriver => Driver(setup, context, installs, onProgress, cancellationToken),
                    StepKind.NvidiaRestore => Restore(setup, context, cancellationToken),
                    _ => ImportProfile(setup, context, adjusted, cancellationToken),
                };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
            {
                // One step's failure is recorded and the next steps still run, as a failed app install doesn't stop a batch.
                (state, detail, value) = (StepState.Failed, e.Message, null);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break; // stopped part-way: the step runs again when the setup continues (what it finished is skipped then)
            }

            setup = setup.With(step.Kind, state, detail, value);
            if (step.Kind >= StepKind.NvidiaBackup)
            {
                steps.Add(new SetupStepOutcome(step.Kind, state, detail, value,
                    step.Kind == StepKind.NvidiaProfile && adjusted.Count > 0 ? [.. adjusted] : null));
            }

            // Recorded after every step, then the progress saved: a crash or a power cut later in the sitting loses
            // neither what this step did nor the fact that it is done.
            recordError = Record(setup, Sitting(), context.Store) ?? recordError;
            if (persist && (saveError = TrySave(setup, context.Store)) is not null)
            {
                break;
            }
        }

        var run = Sitting();
        return new SetupSitting(setup, run, kept, Record(setup, run, context.Store) ?? recordError, saveError);
    }

    /// <summary>
    /// Records how the NVIDIA driver step ended: done when the installed driver changed (or the user says it is done),
    /// skipped when the user chose to skip it. The setup is saved; the next <see cref="Run"/> carries on.
    /// </summary>
    /// <param name="installedNow">The driver as NVIDIA numbers it now, if it could be read.</param>
    public static SetupSitting FinishDriver(PendingSetup setup, SetupContext context, string? installedNow, bool skipped)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(context);
        var atStart = setup.Nvidia?.DriverAtStart ?? "";
        var (state, detail) = skipped
            ? (StepState.Skipped, "Skipped by the user; the driver wasn't changed by this setup.")
            : installedNow is { Length: > 0 } now && now != atStart
                ? (StepState.Done, $"Driver {Describe(atStart)} -> {now}.")
                : (StepState.Done, $"Marked done by the user; the driver reads {Describe(installedNow ?? atStart)}.");
        setup = setup.With(StepKind.NvidiaDriver, state, detail, installedNow);
        var run = SetupRun.Create(setup.RunId, "setup", null, setup.StartedUtc, SetupRun.Stamp(context.Clock()), [], [], [],
            [new SetupStepOutcome(StepKind.NvidiaDriver, state, detail, installedNow)]);
        var recordError = Record(setup, run, context.Store);
        return new SetupSitting(setup, run, [], recordError, TrySave(setup, context.Store));
    }

    /// <summary>
    /// Stops a setup: its unfinished steps are left undone, the record keeps what was done, and a step it recorded as
    /// waiting (the driver) is marked discarded there, so Activity doesn't keep asking to continue a setup that is gone.
    /// </summary>
    public static void Discard(SetupStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (Pending(store) is { } setup && store.ReadRunRecord(setup.RunId) is { } json)
        {
            try
            {
                var run = SetupRun.FromJson(json);
                if (run.SetupSteps.Any(s => s.State is StepState.Waiting or StepState.Pending))
                {
                    store.WriteRunRecord(setup.RunId, (run with
                    {
                        Steps = [.. run.SetupSteps.Select(s => s.State is StepState.Waiting or StepState.Pending
                            ? s with { State = StepState.Skipped, Detail = "The setup was discarded before this step finished." }
                            : s)],
                    }).ToJson());
                }
            }
            catch (FormatException)
            {
                // An unreadable record is left as it is.
            }
        }

        store.ClearPendingSetup();
    }

    /// <summary>The unfinished setup in the store, or <c>null</c> (none, or a file this build can't read).</summary>
    public static PendingSetup? Pending(SetupStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        try
        {
            return store.ReadPendingSetup() is { } json ? PendingSetup.FromJson(json) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    private static string Describe(string version) => version.Length > 0 ? version : "unknown";

    /// <summary>Saves the setup's progress (or removes it once finished); an error to show, or <c>null</c>.</summary>
    private static string? TrySave(PendingSetup setup, SetupStore store)
    {
        try
        {
            if (setup.Finished)
            {
                store.ClearPendingSetup();
            }
            else
            {
                store.WritePendingSetup(setup.ToJson());
            }

            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }

    /// <summary>Adds this sitting to the setup's history record (one record per setup); an error to show, or <c>null</c>.</summary>
    private static string? Record(PendingSetup setup, SetupRun sitting, SetupStore store)
    {
        try
        {
            SetupRun? earlier = null;
            if (store.ReadRunRecord(setup.RunId) is { } json)
            {
                earlier = SetupRun.FromJson(json);
            }

            var run = earlier is null ? sitting : earlier.Then(sitting);
            if (earlier is null && run.ChangedNothing)
            {
                return null; // nothing done or waiting yet: no record, as a profile re-run that finds everything done leaves none
            }

            store.WriteRunRecord(setup.RunId, run.ToJson());
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            return e.Message;
        }
    }

    private static (StepState, string, string?) Runtimes(SetupStep step, SetupContext context, List<InstallOutcome> installs,
        Action<StepKind, string>? onProgress, CancellationToken cancellationToken)
    {
        var packages = step.Items.Select(VcRuntimes.ById).OfType<VcRuntime>().Select(r => r.Package).ToList();

        // A runtime whose state can't be read now (the registry wasn't readable) is never installed blind: it may be there.
        var plan = context.Installer.BuildPlan(packages);
        var unknown = plan.Where(p => p.State == InstallState.Unknown)
            .Select(p => new InstallOutcome(p.Package.Id, p.Package.WingetId, InstallStatus.Failed, "Whether it is installed couldn't be read, so it wasn't installed."));
        var outcomes = context.Installer.Install([.. plan.Where(p => p.State != InstallState.Unknown)], skipInstalled: true,
            (package, line) => onProgress?.Invoke(StepKind.Runtimes, $"{package.Name}: {line.Trim()}"), cancellationToken).Concat(unknown).ToList();
        installs.AddRange(outcomes);
        return Summarise(outcomes, "runtime");
    }

    private static (StepState, string, string?) Apps(SetupStep step, string runId, SetupContext context, List<InstallOutcome> installs,
        List<ConfigImportOutcome> config, List<string> kept, Action<StepKind, string>? onProgress, CancellationToken cancellationToken)
    {
        var packages = step.Items.Select(context.Apps.ById).OfType<AppPackage>().ToList();
        var bundle = context.ConfigBundle;
        var ports = bundle is null
            ? []
            : packages.Select(p => ConfigCatalog.ById(p.Id)).OfType<ConfigPortDefinition>().Where(p => bundle.Apps.ContainsKey(p.Id)).ToList();

        // Settings already on this PC (left by an earlier install, or kept by an uninstaller) are the user's: the set-up
        // after an install never overwrites them; they change only from Configurations, with a preview.
        var locate = context.Locate ?? ConfigLocator.Locate;
        var hadSettings = ports.Where(p => locate(p).Any(f => f.Exists)).Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var outcomes = Install(context, packages, StepKind.Apps, onProgress, cancellationToken);
        installs.AddRange(outcomes);

        // Then set up the apps this run installed that had no settings yet; an app found already installed isn't set up.
        var ready = outcomes.Where(o => o.Status is InstallStatus.Succeeded or InstallStatus.SucceededRebootRequired)
            .Select(o => o.AppId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var configure = ports.Where(p => ready.Contains(p.Id) && !hadSettings.Contains(p.Id)).ToList();
        kept.AddRange(ports.Where(p => ready.Contains(p.Id) && hadSettings.Contains(p.Id)).Select(p => p.Id));

        // Set up even when the rest was cancelled: an app this run installed gets the set-up the user confirmed now, since a
        // later sitting finds it already installed and wouldn't set it up.
        if (bundle is not null && configure.Count > 0)
        {
            config.AddRange(ConfigService.Import(bundle, configure, context.Store, runId, context.Locate, context.IsRunning));
        }

        var (state, detail, value) = Summarise(outcomes, "app");
        return config.Any(c => c.Status == ConfigImportStatus.Failed) && state == StepState.Done
            ? (StepState.Failed, detail + " Some app settings couldn't be set up.", value)
            : (state, detail, value);
    }

    private static (StepState, string, string?) Windows(SetupStep step, SetupContext context, List<WindowsOpOutcome> windows, CancellationToken cancellationToken)
    {
        if (context.Windows is null)
        {
            return (StepState.Failed, "Windows settings can't be changed from here.", null);
        }

        var ops = step.Items.Select(context.WindowsCatalog.ById).OfType<WindowsOperation>().ToList();
        context.RefreshReader?.Invoke();
        var outcomes = context.Windows.Apply(ops, cancellationToken);
        windows.AddRange(outcomes);
        var applied = outcomes.Count(o => o.Status == WindowsOpStatus.Applied);
        return outcomes.All(o => o.Ok)
            ? (StepState.Done, $"{applied} of {outcomes.Count} Windows setting(s) applied.", null)
            : (StepState.Failed, $"{applied} of {outcomes.Count} applied; {outcomes.Count(o => !o.Ok)} didn't.", null);
    }

    private static (StepState, string, string?) Backup(string runId, SetupContext context, List<InstallOutcome> installs,
        Action<StepKind, string>? onProgress, CancellationToken cancellationToken)
    {
        if (Inspector(context, installs, onProgress, cancellationToken) is not { } inspector)
        {
            return (StepState.Failed, "NVIDIA Profile Inspector couldn't be installed or found, so the NVIDIA profiles weren't backed up.", null);
        }

        if (!inspector.CanMerge)
        {
            return (StepState.Failed, $"NVIDIA Profile Inspector {inspector.Version} is older than {ProfileInspector.MergeImport} and couldn't put a backup back "
                + "without replacing profiles. Update it (winget upgrade --id Orbmu2k.nvidiaProfileInspector), then run the setup again.", null);
        }

        onProgress?.Invoke(StepKind.NvidiaBackup, "Exporting your NVIDIA profiles");
        var export = context.Nvidia.Export(inspector, cancellationToken);
        if (export.Content is null)
        {
            return (StepState.Failed, export.Error ?? "The NVIDIA profiles couldn't be exported.", null);
        }

        IReadOnlyList<NipProfile> profiles;
        try
        {
            profiles = NipFile.Parse(NipFile.Decode(export.Content));
        }
        catch (FormatException e)
        {
            return (StepState.Failed, e.Message, null);
        }

        if (NipFile.SettingCount(profiles) == 0)
        {
            return (StepState.Skipped, "No customised NVIDIA profiles: nothing to back up.", null);
        }

        context.Store.KeepRunFile(runId, BackupFile, export.Content);
        return (StepState.Done, $"Backed up {NipFile.SettingCount(profiles)} setting(s) in {profiles.Count} profile(s).", BackupFile);
    }

    private static (StepState, string, string?) Driver(PendingSetup setup, SetupContext context, List<InstallOutcome> installs,
        Action<StepKind, string>? onProgress, CancellationToken cancellationToken)
    {
        // The plan was "back up, then the driver": without the backup a driver install (a clean one above all) could reset
        // the user's profiles with no copy left, so NVCleanstall isn't opened.
        if (setup.Step(StepKind.NvidiaBackup) is { State: StepState.Failed })
        {
            return (StepState.Failed, "The NVIDIA profile backup failed, so NVCleanstall wasn't opened: a driver install could reset your profiles "
                + "with no copy left. Fix what the backup step says and run the setup again, or install the driver yourself.", null);
        }

        var path = context.Nvidia.FindNvCleanstall();
        if (path is null && context.Apps.ById("nvcleanstall") is { } package)
        {
            var outcomes = Install(context, [package], StepKind.NvidiaDriver, onProgress, cancellationToken);
            installs.AddRange(outcomes);
            path = context.Nvidia.FindNvCleanstall();
        }

        if (path is null)
        {
            return (StepState.Failed, "NVCleanstall couldn't be installed or found.", null);
        }

        return context.Nvidia.LaunchNvCleanstall(path) is { } error
            ? (StepState.Failed, error, null)
            : (StepState.Waiting, $"NVCleanstall started; the driver was {Describe(setup.Nvidia?.DriverAtStart ?? "")}.", null);
    }

    private static (StepState, string, string?) Restore(PendingSetup setup, SetupContext context, CancellationToken cancellationToken)
    {
        if (setup.Step(StepKind.NvidiaBackup) is { State: StepState.Failed })
        {
            return (StepState.Skipped, "The backup failed and the driver step didn't run: nothing to put back.", null);
        }

        if (setup.Step(StepKind.NvidiaBackup)?.Value is not { } name || context.Store.RunFilePath(setup.RunId, name) is not { } path)
        {
            return (StepState.Skipped, "No customised NVIDIA profiles were backed up: nothing to put back.", null);
        }

        if (context.Nvidia.FindProfileInspector() is not { } inspector)
        {
            return (StepState.Failed, "NVIDIA Profile Inspector isn't there any more, so the profiles weren't put back.", null);
        }

        var backup = NipFile.Parse(NipFile.Decode(File.ReadAllBytes(path)));
        if (context.Nvidia.MergeImport(inspector, path, cancellationToken) is { } error)
        {
            return (StepState.Failed, error, null);
        }

        // Verified, not assumed: NVIDIA Profile Inspector exits with 0 whether or not the import worked.
        var readback = context.Nvidia.Export(inspector, cancellationToken);
        if (readback.Content is null)
        {
            return (StepState.Failed, "The profiles were imported, but couldn't be read back to check them: " + readback.Error, null);
        }

        var missing = NipFile.Missing(backup, NipFile.Parse(NipFile.Decode(readback.Content)));
        return missing.Count == 0
            ? (StepState.Done, $"Put back and checked {NipFile.SettingCount(backup)} setting(s).", null)
            : (StepState.Failed, $"{missing.Count} setting(s) aren't in place after the import: {string.Join(", ", missing.Take(5))}.", null);
    }

    /// <summary>
    /// Imports the NVIDIA profile the configuration names (D032), after the driver step — a driver installed, or none
    /// needed — and the restore of the user's own profiles: Mesura's own file, checked against its SHA-256 and kept
    /// unchanged in the run's folder; a copy fitted to this PC when it has a battery or it can't be told
    /// (<see cref="NvidiaProfilePortability"/>), kept beside it; that is merged in (other profiles and settings stay),
    /// then read back and compared setting by setting, because NVIDIA Profile Inspector exits with 0 whether or not an
    /// import worked. It never runs without the backup taken first, and a missing profile is a failure to fix, not a
    /// silent skip.
    /// </summary>
    /// <param name="adjusted">Receives the settings changed to fit this PC, for the record.</param>
    private static (StepState, string, string?) ImportProfile(PendingSetup setup, SetupContext context, List<ProfileAdjustment> adjusted,
        CancellationToken cancellationToken)
    {
        adjusted.Clear();
        if (setup.Step(StepKind.NvidiaBackup) is { State: StepState.Failed })
        {
            return (StepState.Failed, "The NVIDIA profile backup failed, so no profile was imported over your profiles. "
                + "Fix what the backup step says and run the setup again.", null);
        }

        if (setup.Nvidia?.Profile is not { } reference)
        {
            return (StepState.Failed, "The NVIDIA configuration names no NVIDIA profile, so none was imported. "
                + "Choose one in Setup's NVIDIA card, then run the setup again.", null);
        }

        var (bytes, error) = NvidiaProfiles.Load(reference);
        if (bytes is null || NvidiaProfiles.ById(reference.Id) is not { } profile)
        {
            return (StepState.Failed, error ?? $"The NVIDIA profile '{reference.Id}' couldn't be read.", null);
        }

        if (context.Nvidia.FindProfileInspector() is not { } inspector)
        {
            return (StepState.Failed, "NVIDIA Profile Inspector isn't there any more, so the NVIDIA profile wasn't imported.", null);
        }

        // Fitted to the kind of PC (a battery or none) read now.
        var portable = NvidiaProfilePortability.Adapt(bytes, context.ReadBattery?.Invoke());
        var original = context.Store.KeepRunFile(setup.RunId, profile.FileName, bytes);
        var path = portable.Adjusted ? context.Store.KeepRunFile(setup.RunId, profile.AdjustedFileName, portable.Content) : original;
        adjusted.AddRange(portable.Adjustments);
        var file = portable.Adjusted
            ? $"{profile.Name}, fitted to this PC ({profile.AdjustedFileName}, SHA-256 "
                + $"{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(portable.Content))[..16]}…; the original {profile.FileName} is kept unchanged beside it). "
                + string.Join(" ", portable.Adjustments.Select(a => $"{a.Setting}: {a.OriginalShort} -> {a.Imported ?? "left out"}. {a.Reason}"))
            : $"{profile.Name}, unchanged ({profile.FileName}).";
        if (context.Nvidia.MergeImport(inspector, path, cancellationToken) is { } importError)
        {
            return (StepState.Failed, $"{importError} File: {file}", null);
        }

        var readback = context.Nvidia.Export(inspector, cancellationToken);
        if (readback.Content is null)
        {
            return (StepState.Failed, $"The import ran, but couldn't be read back to check it: {readback.Error} File: {file}", null);
        }

        var missing = NipFile.Missing(portable.Profiles, NipFile.Parse(NipFile.Decode(readback.Content)));
        if (missing.Count == 0)
        {
            return (StepState.Done, $"Imported and checked {NipFile.SettingCount(portable.Profiles)} setting(s) of {file}", profile.Id);
        }

        return (StepState.Failed, $"{missing.Count} setting(s) aren't in place after the import: {string.Join(", ", missing.Take(8))}"
            + (missing.Count > 8 ? ", …" : "") + ". The installed driver may not know a setting, and NVIDIA Profile Inspector then skips it without saying so."
            + $" File: {file}", profile.Id);
    }

    private static ProfileInspector? Inspector(SetupContext context, List<InstallOutcome> installs, Action<StepKind, string>? onProgress, CancellationToken cancellationToken)
    {
        if (context.Nvidia.FindProfileInspector() is { } found)
        {
            return found;
        }

        if (context.Apps.ById("nvpi") is not { } package)
        {
            return null;
        }

        installs.AddRange(Install(context, [package], StepKind.NvidiaBackup, onProgress, cancellationToken));
        return context.Nvidia.FindProfileInspector();
    }

    private static IReadOnlyList<InstallOutcome> Install(SetupContext context, IReadOnlyList<AppPackage> packages, StepKind kind,
        Action<StepKind, string>? onProgress, CancellationToken cancellationToken)
    {
        // What to skip is decided from the state now, not from when the plan was shown.
        var plan = context.Installer.BuildPlan(packages);
        return context.Installer.Install(plan, skipInstalled: true, (package, line) => onProgress?.Invoke(kind, $"{package.Name}: {line.Trim()}"), cancellationToken);
    }

    private static (StepState, string, string?) Summarise(IReadOnlyList<InstallOutcome> outcomes, string noun)
    {
        var installed = outcomes.Count(o => o.Status is InstallStatus.Succeeded or InstallStatus.SucceededRebootRequired);
        var failed = outcomes.Count(o => o.Status is InstallStatus.Failed or InstallStatus.WingetUnavailable);
        var detail = $"{installed} {noun}(s) installed, {outcomes.Count(o => o.Status == InstallStatus.AlreadyInstalled)} already there"
            + (failed > 0 ? $", {failed} failed." : ".");
        return (failed > 0 ? StepState.Failed : StepState.Done, detail, null);
    }
}
