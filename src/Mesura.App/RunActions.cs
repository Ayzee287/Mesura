using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Mesura.Core.Apps;
using Mesura.Core.Config;
using Mesura.Core.Setup;
using Mesura.Core.Windows;
using Mesura.Setup.Windows.Config;

namespace Mesura.App;

/// <summary>
/// A page's result line after a run: what happened, in its tone, with Undo and Details when they apply. Raw messages
/// (Windows, winget or file errors, in English) go in <see cref="Technical"/>, behind a disclosure, never in the text.
/// </summary>
public sealed class Banner : Observable
{
    private string _title = "";
    private string _detail = "";
    private string _technical = "";
    private string _tone = Mesura.App.Tone.Neutral;
    private Action? _undo;
    private Action? _details;

    public Banner()
    {
        Undo = new RelayCommand(() => _undo?.Invoke(), () => _undo is not null);
        Details = new RelayCommand(() => _details?.Invoke(), () => _details is not null);
        Close = new RelayCommand(Hide);
    }

    public string Title
    {
        get => _title;
        private set => Set(ref _title, value);
    }

    public string Detail
    {
        get => _detail;
        private set => Set(ref _detail, value);
    }

    public string Tone
    {
        get => _tone;
        private set => Set(ref _tone, value);
    }

    /// <summary>The raw messages behind the result, for the "Technical details" disclosure; empty when there are none.</summary>
    public string Technical
    {
        get => _technical;
        private set => Set(ref _technical, value);
    }

    public bool Visible => _title.Length > 0;

    public bool CanUndo => _undo is not null;

    public bool HasDetails => _details is not null;

    public RelayCommand Undo { get; }

    public RelayCommand Details { get; }

    public RelayCommand Close { get; }

    public void Show(string tone, string title, string detail = "", Action? undo = null, Action? details = null, string? technical = null)
    {
        Tone = tone;
        Detail = detail;
        Technical = technical ?? "";
        _undo = undo;
        _details = details;
        Title = title;
        Raise(nameof(Visible));
        Raise(nameof(CanUndo));
        Raise(nameof(HasDetails));
        Undo.RaiseCanExecuteChanged();
        Details.RaiseCanExecuteChanged();
    }

    public void Hide() => Show(Mesura.App.Tone.Neutral, "");

    /// <summary>
    /// Called when the user leaves the page: a result that went well (or was only information) has been seen and no
    /// longer describes what the page shows next time, so it goes; a warning or a failure stays until dismissed.
    /// </summary>
    public void Settle()
    {
        if (_tone is Mesura.App.Tone.Ok or Mesura.App.Tone.Neutral or Mesura.App.Tone.Info or Mesura.App.Tone.Accent)
        {
            Hide();
        }
    }
}

/// <summary>
/// A page that reacts to being shown (it may read its state again) and to being left (its settled results go; see
/// <see cref="Banner.Settle"/>).
/// </summary>
public interface IPageState
{
    void Enter()
    {
    }

    void Leave();
}

/// <summary>
/// The runs every page starts — apply Windows settings, undo a run, restore app settings — with their recording, so
/// Optimize, All settings and Activity do them the same way. Each changes the PC only after the page confirmed it.
/// </summary>
public static class RunActions
{
    /// <summary>The conflicting groups among these operations, in the user's language, or empty.</summary>
    public static IReadOnlyList<string> Conflicts(Services services, IReadOnlyList<WindowsOperation> ops) =>
        ConflictLines(WindowsConfigurator.ConflictGroups(ops, services.Reader));

    /// <summary>One sentence per conflicting group, naming its entries in the user's language.</summary>
    public static IReadOnlyList<string> ConflictLines(IReadOnlyList<IReadOnlyList<WindowsOperation>> groups) =>
        [.. groups.Select(g => Loc.Format("Common.Conflict", Names([.. g.Select(o => $"«{Loc.Name(o)}»")])))];

    /// <summary>Applies the operations and records the run (when it changed or failed something).</summary>
    public static async Task<(SetupRun Run, bool Recorded, string? RecordError)> ApplyWindowsAsync(Services services, IReadOnlyList<WindowsOperation> ops)
    {
        using var change = services.BeginChange();
        var started = DateTimeOffset.UtcNow;
        var outcomes = await Task.Run(() =>
        {
            services.Reader.Refresh(); // before-images from the system as it is now
            return services.Windows.Apply(ops, CancellationToken.None);
        });
        var id = SetupRun.NewId(started, "windows-apply");
        var run = SetupRun.Create(id, "windows-apply", null, SetupRun.Stamp(started), SetupRun.Stamp(DateTimeOffset.UtcNow), [], [], outcomes);
        var recorded = outcomes.Any(o => o.Changes.Count > 0 || !o.Ok);
        var error = recorded ? Dialogs.TryRecord(services, run) : null;
        if (!recorded)
        {
            services.NoteSession(run); // a recorded run is noted when it is recorded
        }
        services.Log($"Windows apply: {outcomes.Count(o => o.Status == WindowsOpStatus.Applied)} of {outcomes.Count} applied"
            + (recorded ? $"; recorded as {id}." : "."));
        foreach (var o in outcomes)
        {
            services.Log($"  {o.Status}: {o.Name} — {o.Detail}");
        }

        return (run, recorded, error);
    }

    /// <summary>Whether a run has anything Mesura can undo (Windows changes, app settings it replaced).</summary>
    public static bool CanUndo(SetupRun run) =>
        run.WindowsSteps.Any(w => w.Changes.Count > 0) || run.Config.Any(c => c.Written.Count > 0);

    /// <summary>
    /// Undoes a run: Windows settings back to their recorded values (a setting changed again since is left alone) and
    /// app settings back from the run's backups (a running app is skipped). App installs aren't undone. The undo is
    /// recorded as its own run, so it can be undone in turn.
    /// </summary>
    /// <returns>
    /// The undo run, or <c>null</c> when there was nothing left to put back; then the apps whose settings file changed
    /// since the run (left as they are) are named.
    /// </returns>
    public static async Task<(SetupRun? Run, string? RecordError, IReadOnlyList<string> ChangedSince)> UndoAsync(Services services, SetupRun run)
    {
        using var change = services.BeginChange();
        var started = DateTimeOffset.UtcNow;
        var steps = run.WindowsSteps.Where(s => s.Changes.Count > 0).ToList();
        var config = run.Config.Where(c => c.Written.Count > 0).ToList();
        var operation = steps.Count > 0 && config.Count > 0 ? "undo" : steps.Count > 0 ? "windows-revert" : "config-restore";
        var id = SetupRun.NewId(started, operation);
        var (windows, restored) = await Task.Run(() =>
        {
            services.Reader.Refresh(); // compare against the system as it is now
            var w = steps.Count > 0 ? services.Windows.Revert(steps) : [];
            var r = config.Select(c => ConfigCatalog.ById(c.PortId) is { } port
                    ? ConfigService.RestoreBackups(port, run.Id, c.Written, services.Store, id)
                    : new ConfigImportOutcome(c.PortId, c.AppName, ConfigImportStatus.Failed, [], [], [], [], "This app has no configuration support in this build."))
                .ToList();
            return (w, r);
        });

        if (windows.All(o => o.Status == WindowsOpStatus.RevertSkipped)
            && restored.All(o => o.Written.Count == 0 && o.Status is ConfigImportStatus.Unchanged or ConfigImportStatus.ChangedSince))
        {
            services.Log($"Undo of {run.Id}: nothing to put back."
                + string.Concat(restored.Where(o => o.Status == ConfigImportStatus.ChangedSince).Select(o => $" {o.AppName}: {o.Detail}")));
            return (null, null, [.. restored.Where(o => o.Status == ConfigImportStatus.ChangedSince).Select(o => o.AppName)]);
        }

        var undo = SetupRun.Create(id, operation, null, SetupRun.Stamp(started), SetupRun.Stamp(DateTimeOffset.UtcNow), [], restored, windows);
        var error = Dialogs.TryRecord(services, undo);
        services.Log($"Undo of {run.Id}: recorded as {id}.");
        return (undo, error, []);
    }

    /// <summary>
    /// Undoes a run (after the page asked) and shows how it went on the page's banner: done, partly done with what is
    /// left, or nothing to put back, naming the app settings left as they are because they changed since.
    /// </summary>
    /// <returns>The undo run, or <c>null</c> when nothing was put back.</returns>
    public static async Task<SetupRun?> UndoAndShowAsync(Services services, SetupRun run, Banner banner, Action<string?> openActivity)
    {
        SetupRun? undo;
        string? error;
        IReadOnlyList<string> changedSince;
        try
        {
            (undo, error, changedSince) = await UndoAsync(services, run);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Started from a banner's Undo, nothing else would show this: say it, rather than lose it.
            services.Log($"Undo of {run.Id} stopped: {e}");
            banner.Show(Tone.Danger, Loc.Get("Common.Unexpected"), technical: e.Message);
            return null;
        }

        if (undo is null)
        {
            banner.Show(Tone.Neutral, Loc.Get("Undo.NothingLeft"),
                string.Join("\n", changedSince.Select(app => Loc.Format("FollowUp.ConfigChangedSince", app))));
            return null;
        }

        // Settings no longer at the value the run wrote were left as they are: that is a partial undo, said as such.
        var leftAsIs = undo.WindowsSteps.Count(s => s.Status == WindowsOpStatus.RevertSkipped);
        var problems = undo.WindowsSteps.Count(s => !s.Ok) + leftAsIs
            + undo.Config.Count(c => c.Status is ConfigImportStatus.Failed or ConfigImportStatus.SkippedAppRunning or ConfigImportStatus.ChangedSince);
        var lines = FollowUps(services, undo).Select(f => f.Text).ToList();
        if (leftAsIs > 0)
        {
            lines.Insert(0, Loc.Plural("Undo.LeftAsIs", leftAsIs));
        }

        banner.Show(problems == 0 ? Tone.Ok : Tone.Warn, problems == 0 ? Loc.Get("Undo.Done") : Loc.Get("Undo.Partial"),
            string.Join("\n", lines.Append(error ?? "").Where(t => t.Length > 0)),
            details: () => openActivity(undo.Id), technical: TechnicalLines(undo));
        return undo;
    }

    /// <summary>
    /// Imports a configuration bundle for some apps, or previews it (<paramref name="apply"/> false), and records an
    /// applied import.
    /// </summary>
    public static async Task<(SetupRun? Run, IReadOnlyList<ConfigImportOutcome> Outcomes, string? RecordError)> ImportAsync(
        Services services, ConfigBundleFile bundle, IReadOnlyList<ConfigPortDefinition> ports, bool apply)
    {
        using var change = apply ? services.BeginChange() : null;
        var started = DateTimeOffset.UtcNow;
        var id = SetupRun.NewId(started, "config-import");
        var outcomes = await Task.Run(() => ConfigService.Import(bundle, ports, apply ? services.Store : null, apply ? id : null));
        if (!apply)
        {
            return (null, outcomes, null);
        }

        var recorded = outcomes.Any(o => o.Written.Count > 0 || o.Status != ConfigImportStatus.Unchanged);
        var run = SetupRun.Create(id, "config-import", null, SetupRun.Stamp(started), SetupRun.Stamp(DateTimeOffset.UtcNow), [], outcomes);
        var error = recorded ? Dialogs.TryRecord(services, run) : null;
        foreach (var o in outcomes)
        {
            services.Log($"Settings import: {o.AppName}: {o.Status}, {o.Applied.Count} setting(s), {o.Written.Count} file(s) written. {o.Detail}");
        }

        return (recorded ? run : null, outcomes, error);
    }

    /// <summary>The name of a Windows step in the user's language (the record keeps the English name).</summary>
    public static string StepName(Services services, WindowsOpOutcome step) =>
        services.WindowsCatalog.Catalog?.ById(step.OperationId) is { } op ? Loc.Name(op) : step.Name;

    public static string AppName(Services services, string id) =>
        services.Apps.Catalog?.ById(id)?.Name ?? VcRuntimes.ById(id)?.Package.Name ?? id;

    /// <summary>A setup step's name in the user's language.</summary>
    public static string SetupStepName(StepKind kind) => Loc.Get($"Setup.Step.{kind}");

    /// <summary>
    /// One line for a setup step in a run: its name and how it ended, with the driver version or the NVIDIA profile (and
    /// whether it was fitted to this PC) when there is one.
    /// </summary>
    public static string SetupStepLine(SetupStepOutcome step)
    {
        ArgumentNullException.ThrowIfNull(step);
        var state = Loc.Get($"Setup.State.{step.State}");
        return step switch
        {
            { Kind: StepKind.NvidiaDriver, State: StepState.Done, Value: { Length: > 0 } version } => $"{SetupStepName(step.Kind)} · {state} · {version}",
            { Kind: StepKind.NvidiaProfile, Value: { } id } when Mesura.Core.Nvidia.NvidiaProfiles.ById(id) is { } profile =>
                $"{SetupStepName(step.Kind)} · {state} · {profile.Name}" + (step.Adjustments is { Count: > 0 } ? " · " + Loc.Get("Activity.ProfileFitted") : ""),
            _ => $"{SetupStepName(step.Kind)} · {state}",
        };
    }

    /// <summary>The settings of an NVIDIA profile import that were changed to fit this PC, one line each.</summary>
    public static IEnumerable<string> AdjustmentLines(SetupStepOutcome step) =>
        (step.Adjustments ?? []).Select(a => Loc.Format("Activity.ProfileAdjustment", a.Setting, a.OriginalShort, a.Imported ?? Loc.Get("Activity.ProfileLeftOut")));

    /// <summary>The word for a setup step's state, as the pills colour it.</summary>
    public static string SetupStateWord(StepState state) => state switch
    {
        StepState.Done => "done",
        StepState.Skipped => "n/a",
        StepState.Failed => "failed",
        StepState.Waiting => "waiting",
        _ => "neutral",
    };

    /// <summary>What is left after a run, in the user's language (the same items as <see cref="RunFollowUp"/>).</summary>
    public static IReadOnlyList<(string Tone, string Text)> FollowUps(Services services, SetupRun run)
    {
        var items = new List<(string, string)>();
        foreach (var app in run.Apps)
        {
            var name = AppName(services, app.AppId);
            switch (app.Status)
            {
                case InstallStatus.Failed:
                    items.Add((Tone.Danger, Loc.Format("FollowUp.InstallFailed", name, WingetReason(app.Detail))));
                    break;
                case InstallStatus.WingetUnavailable:
                    items.Add((Tone.Warn, Loc.Format("FollowUp.NoWinget", name)));
                    break;
                case InstallStatus.Cancelled:
                    items.Add((Tone.Warn, Loc.Format("FollowUp.Cancelled", name)));
                    break;
                case InstallStatus.SucceededRebootRequired:
                    items.Add((Tone.Info, Loc.Format("FollowUp.RestartToFinish", name)));
                    break;
            }
        }

        foreach (var config in run.Config)
        {
            switch (config.Status)
            {
                case ConfigImportStatus.SkippedAppRunning:
                    items.Add((Tone.Warn, Loc.Format("FollowUp.AppRunning", config.AppName)));
                    break;
                case ConfigImportStatus.Failed:
                    items.Add((Tone.Danger, Loc.Format("FollowUp.ConfigFailed", config.AppName)));
                    break;
                case ConfigImportStatus.ChangedSince:
                    items.Add((Tone.Info, Loc.Format("FollowUp.ConfigChangedSince", config.AppName)));
                    break;
            }

            if (config.DestinationMissing.Count > 0)
            {
                items.Add((Tone.Warn, Loc.Format("FollowUp.StartAppOnce", config.AppName)));
            }

            if (config.WaitingOnScope.Count > 0)
            {
                items.Add((Tone.Warn, Loc.Format("FollowUp.SignInOnce", config.AppName)));
            }
        }

        var elevation = run.WindowsSteps.Where(w => w.Status == WindowsOpStatus.NeedsElevation).ToList();
        if (elevation.Count > 0)
        {
            items.Add((Tone.Warn, Loc.Plural("FollowUp.NeedsAdmin", elevation.Count)));
        }

        foreach (var step in run.WindowsSteps.Where(w => w.Status is WindowsOpStatus.Failed or WindowsOpStatus.Blocked or WindowsOpStatus.Unknown))
        {
            items.Add((Tone.Danger, Loc.Format($"FollowUp.Step{step.Status}", StepName(services, step))));
        }

        foreach (var step in run.SetupSteps)
        {
            switch (step.State)
            {
                case StepState.Failed:
                    items.Add((Tone.Danger, Loc.Format("FollowUp.SetupStepFailed", SetupStepName(step.Kind))));
                    break;
                case StepState.Waiting or StepState.Pending:
                    items.Add((Tone.Warn, Loc.Format("FollowUp.SetupUnfinished", SetupStepName(step.Kind))));
                    break;
            }
        }

        var applied = run.WindowsSteps.Where(w => w.Status is WindowsOpStatus.Applied or WindowsOpStatus.Reverted).ToList();
        foreach (var (activation, key) in new[] { (Activation.Reboot, "FollowUp.Restart"), (Activation.SignOut, "FollowUp.SignOut"), (Activation.ExplorerRestart, "FollowUp.Explorer") })
        {
            var these = applied.Where(w => w.Activation == activation).Select(w => StepName(services, w)).ToList();
            if (these.Count > 0)
            {
                items.Add((Tone.Info, Loc.Format(key, Names(these))));
            }
        }

        return items;
    }

    /// <summary>
    /// The raw messages behind a run's problems (winget, file and Windows errors, in English), one line each, for the
    /// "Technical details" disclosure; empty when nothing failed.
    /// </summary>
    public static string TechnicalLines(SetupRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var lines = new List<string>();
        lines.AddRange(run.Apps.Where(a => a.Status is InstallStatus.Failed or InstallStatus.WingetUnavailable or InstallStatus.Cancelled && a.Detail.Length > 0)
            .Select(a => $"{a.AppId}: {a.Detail}"));
        lines.AddRange(run.Config.Where(c => c.Status is ConfigImportStatus.Failed or ConfigImportStatus.ChangedSince && c.Detail.Length > 0)
            .Select(c => $"{c.PortId}: {c.Detail}"));
        lines.AddRange(run.WindowsSteps.Where(w => !w.Ok || w.Status == WindowsOpStatus.RevertSkipped).Select(w => $"{w.OperationId}: {w.Status} — {w.Detail}"));
        lines.AddRange(run.SetupSteps.Where(s => s.State == StepState.Failed && s.Detail.Length > 0).Select(s => $"{s.Kind}: {s.Detail}"));
        return string.Join("\n", lines);
    }

    /// <summary>
    /// A failed install's reason in the user's language, from the winget exit code in its detail (the code stays in the
    /// technical details); a code Mesura doesn't know reads as a winget error.
    /// </summary>
    public static string WingetReason(string detail)
    {
        var match = System.Text.RegularExpressions.Regex.Match(detail ?? "", @"0x(8A15[0-9A-F]{4})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var key = $"Winget.{match.Groups[1].Value.ToUpperInvariant()}";
            var text = Loc.Get(key);
            if (text != key)
            {
                return text;
            }
        }

        return Loc.Get("Winget.Other");
    }

    /// <summary>
    /// A recorded UTC time in the user's words: "Today, 15:51", "Yesterday, 09:02", or the date and time; with
    /// <paramref name="dateOnly"/> just the date. Text that isn't a time is returned as it is.
    /// </summary>
    public static string When(string utc, bool dateOnly = false)
    {
        if (!DateTimeOffset.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return utc;
        }

        var local = parsed.ToLocalTime();
        var culture = Loc.Instance.Culture;
        if (dateOnly)
        {
            return local.ToString("d MMM yyyy", culture);
        }

        var time = local.ToString("t", culture);
        return local.Date == DateTime.Today ? Loc.Format("Activity.Today", time)
            : local.Date == DateTime.Today.AddDays(-1) ? Loc.Format("Activity.Yesterday", time)
            : local.ToString("d MMM yyyy, ", culture) + time;
    }

    /// <summary>Up to three names, then "and N more".</summary>
    public static string Names(IReadOnlyList<string> names) =>
        names.Count <= 3 ? string.Join(", ", names) : Loc.Format("Common.AndMore", string.Join(", ", names.Take(2)), names.Count - 2);

    /// <summary>The app settings an import changed, by their names in the user's language.</summary>
    public static IReadOnlyList<string> SettingNames(ConfigImportOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var port = ConfigCatalog.ById(outcome.PortId);
        return [.. outcome.Applied.Select(id => port?.Settings.FirstOrDefault(s => s.Id == id) is { } setting ? Loc.Setting(setting) : id)];
    }

    /// <summary>One line for an app's settings in a run: what changed (by name), what waits, or what went wrong.</summary>
    public static string ConfigLine(ConfigImportOutcome c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return c switch
        {
            { Status: ConfigImportStatus.Failed } => $"{c.AppName} · {Loc.Get("Activity.ConfigFailed")}",
            { Status: ConfigImportStatus.SkippedAppRunning } => $"{c.AppName} · {Loc.Get("Activity.ConfigRunning")}",
            { Status: ConfigImportStatus.ChangedSince, Written.Count: > 0 } => $"{c.AppName} · {Loc.Get("Activity.ConfigPartlyRestored")}",
            { Status: ConfigImportStatus.ChangedSince } => $"{c.AppName} · {Loc.Get("Activity.ConfigChangedSince")}",
            { Status: ConfigImportStatus.Unchanged, DestinationMissing.Count: > 0 } => $"{c.AppName} · {Loc.Get("Activity.ConfigNotYet")}",
            { Status: ConfigImportStatus.Unchanged } => $"{c.AppName} · {Loc.Get("Activity.ConfigAlready")}",
            { Applied.Count: > 0 } => $"{c.AppName} · {Loc.Plural("Activity.ConfigChanged", c.Applied.Count, Names(SettingNames(c)))}",
            _ => $"{c.AppName} · {Loc.Plural("Activity.ConfigWritten", c.Written.Count)}",
        };
    }

    /// <summary>
    /// What a run that went well still needs from the user to take effect, as an <c>Activity.Status.*</c> key
    /// (Restart, SignOut, Explorer), or <c>null</c> when nothing does. Once Windows has restarted since the run nothing
    /// is left (a restart also signs out and restarts Explorer); a sign-out alone isn't detected, so that reminder stays
    /// until the next restart. Windows' fast startup isn't a restart: the reminder stays until a real one.
    /// </summary>
    public static string? FinishKey(SetupRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var booted = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        if (DateTimeOffset.TryParse(run.FinishedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var finished) && finished < booted)
        {
            return null;
        }

        var applied = run.WindowsSteps.Where(w => w.Status is WindowsOpStatus.Applied or WindowsOpStatus.Reverted).ToList();
        return applied.Any(w => w.Activation == Activation.Reboot) || run.Apps.Any(a => a.Status == InstallStatus.SucceededRebootRequired) ? "Activity.Status.Restart"
            : applied.Any(w => w.Activation == Activation.SignOut) ? "Activity.Status.SignOut"
            : applied.Any(w => w.Activation == Activation.ExplorerRestart) ? "Activity.Status.Explorer"
            : null;
    }
}
