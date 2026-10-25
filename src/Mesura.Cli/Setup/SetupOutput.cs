using Mesura.Cli.Apps;
using Mesura.Core.Apps;
using Mesura.Core.Setup;
using Mesura.Core.Windows;

namespace Mesura.Cli.Setup;

/// <summary>Console rendering shared by the <c>apps</c>, <c>config</c>, <c>windows</c>, <c>profile</c> and <c>history</c> verbs.</summary>
internal static class SetupOutput
{
    public static string Origin(AppPackage p) => p.Source == AppSource.Manual ? p.Url : $"{p.SourceArgument}: {p.WingetId}";

    public static void WritePlan(IReadOnlyList<AppPlanItem> plan, bool skipInstalled, IReadOnlySet<string>? installedEarlier = null)
    {
        foreach (var item in plan)
        {
            var action = installedEarlier?.Contains(item.Package.Id) == true ? "skip (installed earlier)" : PlannedAction(item, skipInstalled);
            Console.WriteLine($"  {action,-26} {item.Package.Name} ({Origin(item.Package)})");
            if (item.Package.Note.Length > 0)
            {
                Console.WriteLine($"  {"",-26}   {item.Package.Note}");
            }
        }
    }

    public static void WriteInstalls(IEnumerable<AppRunEntry> entries)
    {
        foreach (var e in entries)
        {
            Console.WriteLine($"  {StepWords.Install(e.Status),-17} {e.AppId,-13} {e.Detail}");
        }
    }

    public static void WriteConfig(IReadOnlyList<ConfigImportOutcome> outcomes, bool preview)
    {
        if (outcomes.Count == 0)
        {
            Console.WriteLine("  (no app in the bundle)");
        }

        foreach (var o in outcomes)
        {
            var what = o.Status switch
            {
                ConfigImportStatus.Applied when preview => $"would write {o.Written.Count} file(s); {o.Applied.Count} setting(s) set",
                ConfigImportStatus.Applied => $"wrote {o.Written.Count} file(s); {o.Applied.Count} setting(s) set (backups kept)",
                ConfigImportStatus.Unchanged when o.DestinationMissing.Count > 0 || o.WaitingOnScope.Count > 0 => "WAITING: nothing written yet (see below)",
                ConfigImportStatus.Unchanged => $"already as in the bundle ({o.Applied.Count} setting(s)); nothing to write",
                ConfigImportStatus.SkippedAppRunning => "SKIPPED: app is running",
                _ => "FAILED",
            };
            Console.WriteLine($"  {o.AppName}: {what}");
            if (o.Detail.Length > 0)
            {
                Console.WriteLine($"      {o.Detail}");
            }

            foreach (var id in o.WaitingOnScope)
            {
                Console.WriteLine($"      waiting (e.g. sign in once, then re-run): {id}");
            }

            foreach (var id in o.DestinationMissing)
            {
                Console.WriteLine($"      destination file not found yet (start the app once): {id}");
            }
        }
    }

    /// <summary>A Windows preview: each operation's state, what would change, and the warnings that matter for it.</summary>
    public static void WriteWindowsPreview(IReadOnlyList<OperationEvaluation> preview, bool elevated)
    {
        foreach (var e in preview)
        {
            var willChange = e.State is OperationState.NotApplied or OperationState.Partial;
            var action = e.State switch
            {
                OperationState.NotApplied or OperationState.Partial when e.Operation.NeedsElevation && !elevated => "needs admin",
                OperationState.NotApplied or OperationState.Partial => "change",
                OperationState.Applied => "already done",
                OperationState.Inapplicable => "not applicable",
                OperationState.Manual => "manual",
                OperationState.Observation => "info only",
                OperationState.Blocked => "BLOCKED",
                _ => "unknown",
            };
            Console.WriteLine($"  {action,-15} {e.Operation.Name} ({e.Operation.Id}) [{Scope(e.Operation)}]");
            if (!willChange)
            {
                if (e.State is OperationState.Inapplicable or OperationState.Unknown or OperationState.Blocked)
                {
                    Console.WriteLine($"  {"",-15}   {e.Reason}");
                }

                continue;
            }

            foreach (var item in e.Items.Where(i => i.Present && !i.Matches))
            {
                Console.WriteLine($"  {"",-15}   {item.Target}: {item.Current} -> {item.Desired}");
            }

            foreach (var item in e.Items.Where(i => !i.Present))
            {
                Console.WriteLine($"  {"",-15}   {item.Target}: not present here, left out");
            }

            var notes = new List<string>();
            if (e.Operation.Activation != Activation.Immediate)
            {
                notes.Add($"takes effect {WindowsCli.ActivationLabel(e.Operation.Activation)}");
            }

            if (e.Operation.Risk == RiskClass.Undocumented)
            {
                notes.Add("undocumented setting: an experiment");
            }

            if (e.Operation.Limitations.Length > 0)
            {
                notes.Add(e.Operation.Limitations);
            }

            if (notes.Count > 0)
            {
                Console.WriteLine($"  {"",-15}   Note: {string.Join("; ", notes)}");
            }
        }
    }

    /// <summary>Where an operation's settings live: this PC, the signed-in user, or both.</summary>
    public static string Scope(WindowsOperation op) => (op.MachineWide, op.PerUser) switch
    {
        (true, true) => "this PC + user",
        (true, false) => "this PC",
        (false, true) => "user",
        _ => "-",
    };

    /// <summary>The scope line a preview starts with: which user per-user settings go to, and whether this run can do machine-wide ones.</summary>
    public static void WriteScope(ISystemReader reader)
    {
        Console.WriteLine(reader.User.Resolved
            ? $"Scope: [user] settings go to {reader.User.Account}, the user signed in to this session; [this PC] settings affect every user."
            : $"Scope: [user] settings are BLOCKED: {reader.User.Reason}");
        Console.WriteLine(reader.IsElevated
            ? "This terminal is elevated: [this PC] operations can run."
            : "This terminal is not elevated: operations marked [admin] (or 'needs admin' in a preview) are listed but won't run.");
    }

    public static void WriteWindows(IEnumerable<WindowsOpOutcome> outcomes)
    {
        foreach (var o in outcomes)
        {
            var label = o.Status switch
            {
                WindowsOpStatus.Applied => "applied",
                WindowsOpStatus.AlreadyApplied => "already done",
                WindowsOpStatus.Inapplicable => "not applicable",
                WindowsOpStatus.NeedsElevation => "NEEDS ADMIN",
                WindowsOpStatus.Blocked => "BLOCKED",
                WindowsOpStatus.Failed => "FAILED",
                WindowsOpStatus.Unknown => "UNKNOWN",
                WindowsOpStatus.Manual => "manual",
                WindowsOpStatus.Observation => "info only",
                WindowsOpStatus.Reverted => "reverted",
                WindowsOpStatus.RevertSkipped => "left as is",
                _ => o.Status.ToString(),
            };
            Console.WriteLine($"  {label,-15} {o.Name} ({o.OperationId}): {o.Detail}");
            foreach (var change in o.Changes)
            {
                Console.WriteLine($"  {"",-15}   {change.Target}: {change.Before ?? WindowsEvaluator.NotSet} -> {change.After ?? WindowsEvaluator.NotSet}");
            }
        }
    }

    /// <summary>"3 installed, 1 already installed, 1 FAILED", counting only the statuses that occurred.</summary>
    /// <summary>What is left after a run (the same list the app shows), or nothing when nothing is.</summary>
    public static void WriteFollowUps(SetupRun run, AppCatalog? catalog = null)
    {
        var names = catalog ?? (AppsCli.TryLoadCatalogQuietly() ?? AppCatalog.Default);
        var items = RunFollowUp.For(run, id => names.ById(id)?.Name ?? id);
        if (items.Count == 0)
        {
            return;
        }

        Console.WriteLine("What's left:");
        foreach (var item in items)
        {
            var tag = item.Kind switch
            {
                FollowUpKind.Problem => "problem  ",
                FollowUpKind.RunAgain => "run again",
                FollowUpKind.Finish => "to finish",
                _ => "by hand  ",
            };
            Console.WriteLine($"  [{tag}] {item.Text}");
        }
    }

    public static string Summary(IEnumerable<AppRunEntry> entries) =>
        string.Join(", ", entries.GroupBy(e => e.Status).OrderBy(g => g.Key).Select(g => $"{g.Count()} {Label(g.Key)}"));

    public static string Label(InstallStatus status) => status switch
    {
        InstallStatus.Succeeded => "installed",
        InstallStatus.SucceededRebootRequired => "installed, reboot",
        InstallStatus.AlreadyInstalled => "already installed",
        InstallStatus.Manual => "manual",
        InstallStatus.Failed => "FAILED",
        InstallStatus.Cancelled => "cancelled",
        InstallStatus.WingetUnavailable => "no winget",
        _ => status.ToString(),
    };

    private static string PlannedAction(AppPlanItem item, bool skipInstalled) => item switch
    {
        { Package.Source: AppSource.Manual } => "manual (not run)",
        { State: InstallState.Installed } when skipInstalled => "skip (installed)",
        { State: InstallState.Installed } => "reinstall",
        { State: InstallState.NotInstalled } => "install",
        { Package.Detectable: false } => "install (can't detect)",
        _ => "install (state unknown)",
    };

    /// <summary>Runs an install batch with Ctrl+C wired to a clean cancel, streaming winget's output to stderr.</summary>
    public static T WithCancel<T>(Func<Action<AppPackage, string>, CancellationToken, T> run)
    {
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true; // let the batch record what happened instead of the process dying mid-install
            Console.Error.WriteLine("Cancelling: stopping the current install; nothing further will start.");
            cancel.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            return run((package, line) => Console.Error.WriteLine($"  [{package.Id}] {line.Trim()}"), cancel.Token);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }
}
