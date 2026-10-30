using Mesura.Core.Setup;
using Mesura.Core.Windows;
using Mesura.Platform.Windows.Windows;
using Mesura.Setup.Windows;
using Mesura.Setup.Windows.Windows;

namespace Mesura.Cli.Setup;

/// <summary>
/// The <c>windows</c> verbs: list the Windows configuration operations with their state on this PC, show one, preview
/// and apply a selection, and revert a recorded run. Apply and revert preview by default; <c>--apply</c> changes the PC
/// and records the run, with every setting's exact previous value.
/// </summary>
internal static class WindowsCli
{
    public const string Usage = """
          mesura windows list [--category <name>]
          mesura windows presets
          mesura windows show --id <operation>
          mesura windows apply (--id <a,b> | --preset <p>)... [--apply]
          mesura windows revert --run <run id> [--apply]
          mesura windows restore-points
          mesura windows restore-point [--apply]
        """;

    public const string Help = """
        windows list     Lists the Windows configuration operations and each one's state on this PC:
                         applied, not applied, partly applied, not applicable (e.g. the component was
                         removed), manual or for information. Read-only.
        windows presets  Lists the operation presets. Read-only.
        windows show     Shows one operation: what it changes and why, its limitations, its current and
                         target values, and for a manual one the steps. Read-only.
        windows apply    Previews the chosen operations and presets: what would change, what is already
                         done or not applicable, and what needs an elevated terminal. With --apply it
                         changes only the settings that differ, reads each back, and records the run
                         with every previous value. A failed operation puts back what it changed.
        windows revert   Previews restoring a recorded run's previous values; with --apply restores those
                         still holding what the run wrote (a setting changed since is left alone).
        windows restore-points  Lists the System Restore points Windows has (needs an elevated terminal). Read-only.
        windows restore-point   Previews making a System Restore point; with --apply makes one (elevated). 'windows apply
                         --apply' makes one itself, unless the run changes nothing for the whole PC, before it
                         starts; --no-restore-point skips that.
        """;

    private const int Ok = 0;
    private const int Failed = 1;
    private const int UsageCode = 2;

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args is ["--help"] or ["-h"])
        {
            Console.Error.WriteLine(Usage);
            return Ok;
        }

        if (!TryLoadCatalog(out var catalog, out var source))
        {
            return Failed;
        }

        Console.Error.WriteLine($"Windows catalog: {source} ({catalog.Operations.Count} operations, {catalog.Presets.Count} presets).");
        var options = Options(args[1..], out var apply, out var error);
        if (options is null)
        {
            Console.Error.WriteLine(error);
            return UsageCode;
        }

        var reader = new SystemStateReader(new SystemTools());
        return args[0] switch
        {
            "list" => List(catalog, reader, Single(options, "--category")),
            "presets" => Presets(catalog),
            "show" when Single(options, "--id") is { } id => Show(catalog, reader, id),
            "apply" => Apply(catalog, reader, options, apply),
            "revert" when Single(options, "--run") is { } run => Revert(reader, run, apply),
            "restore-points" => RestorePointList(reader),
            "restore-point" => RestorePointCreate(reader, apply),
            _ => Bad($"windows: unknown verb or missing argument. {Usage}"),
        };
    }

    /// <summary>Whether this process runs as administrator (an elevated run never reads an edited catalog: <see cref="CatalogFiles.Windows"/>).</summary>
    private static bool IsAdministrator()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>Loads the edited catalog if there is one (<see cref="CatalogFiles"/>), else the built-in copy.</summary>
    public static bool TryLoadCatalog(out WindowsCatalog catalog, out string source)
    {
        var loaded = CatalogFiles.Windows(IsAdministrator());
        catalog = loaded.Catalog ?? WindowsCatalog.Default;
        source = loaded.Source;
        if (loaded.Error is not null)
        {
            Console.Error.WriteLine(loaded.Error);
            return false;
        }

        if (CatalogFiles.StaleNote(WindowsCatalog.FileName, CatalogFiles.NewSinceCopied(loaded)) is { Length: > 0 } stale)
        {
            Console.Error.WriteLine(stale);
        }

        if (CatalogFiles.SetAsideNote(WindowsCatalog.FileName, loaded) is { Length: > 0 } aside)
        {
            Console.Error.WriteLine(aside);
        }

        return true;
    }

    /// <summary>Resolves operation ids and preset ids to operations, in the order given, each once.</summary>
    public static IReadOnlyList<WindowsOperation>? Resolve(WindowsCatalog catalog, IEnumerable<string> ids, IEnumerable<string> presets, out string? error)
    {
        error = null;
        var result = new List<WindowsOperation>();
        foreach (var p in presets)
        {
            if (catalog.PresetById(p) is not { } preset)
            {
                error = $"Unknown preset '{p}' (see 'windows presets').";
                return null;
            }

            result.AddRange(catalog.Resolve(preset));
        }

        foreach (var id in ids)
        {
            if (catalog.ById(id) is not { } op)
            {
                error = $"Unknown operation '{id}' (see 'windows list').";
                return null;
            }

            result.Add(op);
        }

        return [.. result.DistinctBy(o => o.Id, StringComparer.OrdinalIgnoreCase)];
    }

    private static int List(WindowsCatalog catalog, SystemStateReader reader, string? category)
    {
        Console.Error.WriteLine($"Windows build {reader.Build}.");
        SetupOutput.WriteScope(reader);
        var operations = catalog.Operations.Where(o => category is null || o.Category.Contains(category, StringComparison.OrdinalIgnoreCase));
        foreach (var group in operations.Select(o => WindowsEvaluator.Evaluate(o, reader)).GroupBy(e => e.Operation.Category))
        {
            Console.WriteLine(group.Key);
            foreach (var e in group)
            {
                Console.WriteLine($"  [{StateLabel(e.State),-11}] {e.Operation.Id,-38} {e.Operation.Name} [{SetupOutput.Scope(e.Operation)}]{Flags(e.Operation)}");
            }
        }

        return Ok;
    }

    private static int Presets(WindowsCatalog catalog)
    {
        foreach (var preset in catalog.Presets)
        {
            Console.WriteLine($"{preset.Id,-24} {preset.Name}: {preset.Description}");
            Console.WriteLine($"{"",-24} {string.Join(", ", preset.OperationIds)}");
        }

        return Ok;
    }

    private static int Show(WindowsCatalog catalog, SystemStateReader reader, string id)
    {
        if (catalog.ById(id) is not { } op)
        {
            return Bad($"Unknown operation '{id}' (see 'windows list').");
        }

        var e = WindowsEvaluator.Evaluate(op, reader);
        Console.WriteLine($"{op.Name} ({op.Id}); {op.Category}");
        Console.WriteLine($"  {op.Description}");
        Console.WriteLine($"  Risk: {RiskLabel(op.Risk)}; takes effect: {ActivationLabel(op.Activation)}{(op.NeedsElevation ? "; needs an elevated terminal" : "")}");
        if (op.MinBuild is not null || op.MaxBuild is not null)
        {
            Console.WriteLine($"  Windows builds: {op.MinBuild?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "any"} to {op.MaxBuild?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "current"}");
        }

        if (op.Limitations.Length > 0)
        {
            Console.WriteLine($"  Limitations: {op.Limitations}");
        }

        Console.WriteLine($"  Source: {op.Source}");
        Console.WriteLine($"  State here: {StateLabel(e.State)}. {e.Reason}");
        static string Shown(string value) => value.Length == 0 ? "(empty)" : value;
        foreach (var item in e.Items)
        {
            // Guided entries only read; a real target may be an empty string (a key's default value).
            Console.WriteLine(op.Observation is not null
                ? $"    {item.Target} = {Shown(item.Current)}"
                : $"    {item.Target}: {Shown(item.Current)} -> {Shown(item.Desired)}{(item.Matches ? " (already)" : "")}{(item.Present ? "" : " (not present)")}");
        }

        if (op.Manual is { } manual)
        {
            Console.WriteLine("  Steps:");
            for (var i = 0; i < manual.Steps.Count; i++)
            {
                Console.WriteLine($"    {i + 1}. {manual.Steps[i]}");
            }

            foreach (var link in manual.Links)
            {
                Console.WriteLine($"    See: {link}");
            }
        }

        if (op.Observation is { } observation)
        {
            if (observation.Consequence.Length > 0)
            {
                Console.WriteLine($"  If you turn it off: {observation.Consequence}");
            }

            if (observation.Restore.Length > 0)
            {
                Console.WriteLine($"  To restore: {observation.Restore}");
            }

            Console.WriteLine($"  Change it here: {observation.OpenUri}   (run: start {observation.OpenUri})");
        }

        return Ok;
    }

    private static int Apply(WindowsCatalog catalog, SystemStateReader reader, Dictionary<string, List<string>> options, bool apply)
    {
        var operations = Resolve(catalog, options.GetValueOrDefault("--id") ?? [], options.GetValueOrDefault("--preset") ?? [], out var error);
        if (operations is null || operations.Count == 0)
        {
            return Bad(error ?? "windows apply needs --id <operation> or --preset <preset> (see 'windows list', 'windows presets').");
        }

        if (WindowsConfigurator.Conflicts(operations, reader) is { Count: > 0 } conflicts)
        {
            return Bad("These operations can't run together:" + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", conflicts));
        }

        var configurator = new WindowsConfigurator(reader, new WindowsWriter(reader.User, reader.Tools as SystemTools));
        var preview = configurator.Preview(operations);
        SetupOutput.WriteScope(reader);
        Console.WriteLine(apply ? "Applying:" : "Preview (nothing changed):");
        SetupOutput.WriteWindowsPreview(preview, reader.IsElevated);
        if (!apply)
        {
            Console.WriteLine(preview.Any(p => p.State is OperationState.NotApplied or OperationState.Partial)
                ? "Re-run with --apply to make these changes; each is recorded and can be reverted with 'windows revert'."
                : "Nothing to change.");
            return Ok;
        }

        if (!options.ContainsKey("--no-restore-point") && reader.IsElevated && operations.Any(o => o.Applicable && o.MachineWide && o.NeedsElevation)
            && preview.Any(p => p.State is OperationState.NotApplied or OperationState.Partial))
        {
            // A System Restore point beneath Mesura's own undo, as the window makes: before whole-PC changes, never blocking them.
            var why = new RestorePoints().TryCreate($"Mesura: before {operations.Count} changes").Sentence;
            Console.Error.WriteLine(why is null ? "Made a System Restore point first." : $"No restore point was made: {why}");
        }

        var started = DateTimeOffset.UtcNow;
        // Ctrl+C stops after the current operation, so what was applied is still recorded and revertible.
        var outcomes = SetupOutput.WithCancel((_, token) => configurator.Apply(operations, token));
        var runId = SetupRun.NewId(started, "windows-apply");
        var run = SetupRun.Create(runId, "windows-apply", null, SetupRun.Stamp(started), SetupRun.Stamp(DateTimeOffset.UtcNow), [], [], outcomes);
        if (outcomes.Any(o => o.Changes.Count > 0 || !o.Ok))
        {
            new SetupStore().WriteRunRecord(runId, run.ToJson());
            Console.Error.WriteLine($"Recorded as history run {runId}; undo with 'mesura windows revert --run {runId} --apply'.");
        }

        Console.WriteLine("Result:");
        SetupOutput.WriteWindows(outcomes);
        SetupOutput.WriteFollowUps(run);
        return run.Ok ? Ok : Failed;
    }

    private static int RestorePointList(SystemStateReader reader)
    {
        var points = new RestorePoints().List();
        if (points.Count == 0)
        {
            Console.WriteLine(reader.IsElevated
                ? "No System Restore points (System Protection may be off for this drive)."
                : "No restore points could be read: listing them needs an elevated terminal.");
            return reader.IsElevated ? Ok : Failed;
        }

        foreach (var point in points)
        {
            Console.WriteLine($"  {point.Sequence,5}  {point.Created.LocalDateTime:yyyy-MM-dd HH:mm}  {point.Description}");
        }

        return Ok;
    }

    private static int RestorePointCreate(SystemStateReader reader, bool apply)
    {
        if (!apply)
        {
            Console.WriteLine("Preview (nothing changed): would make a System Restore point named 'Mesura: manual'. Re-run with --apply (elevated terminal).");
            return Ok;
        }

        if (!reader.IsElevated)
        {
            return Bad("windows restore-point --apply needs an elevated terminal (Run as administrator).");
        }

        var why = new RestorePoints().TryCreate("Mesura: manual").Sentence;
        Console.WriteLine(why ?? "Made a System Restore point.");
        return why is null ? Ok : Failed;
    }

    private static int Revert(SystemStateReader reader, string runId, bool apply)
    {
        var store = new SetupStore();
        SetupRun recorded;
        try
        {
            recorded = store.ReadRunRecord(runId) is { } json
                ? SetupRun.FromJson(json)
                : throw new FormatException($"No run '{runId}' (see 'history list').");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            return Bad(e.Message);
        }

        var steps = recorded.WindowsSteps.Where(s => s.Changes.Count > 0).ToList();
        if (steps.Count == 0)
        {
            Console.WriteLine($"Run {runId} changed no Windows settings; nothing to revert.");
            return Ok;
        }

        if (!apply)
        {
            Console.WriteLine($"Preview (nothing changed): run {runId} changed these settings; reverting restores each previous value");
            Console.WriteLine("where the setting still holds what the run wrote:");
            foreach (var step in steps)
            {
                Console.WriteLine($"  {step.Name} ({step.OperationId})");
                foreach (var change in step.Changes)
                {
                    Console.WriteLine($"      {change.Target}: {change.After ?? WindowsEvaluator.NotSet} -> {change.Before ?? WindowsEvaluator.NotSet}");
                }
            }

            Console.WriteLine("Re-run with --apply to revert.");
            return Ok;
        }

        var started = DateTimeOffset.UtcNow;
        _ = TryLoadCatalog(out var trusted, out _);
        var outcomes = new WindowsConfigurator(reader, new WindowsWriter(reader.User, reader.Tools as SystemTools), trusted).Revert(steps);
        if (outcomes.All(o => o.Status == WindowsOpStatus.RevertSkipped))
        {
            // The same rule as the app: nothing was put back, so no empty revert is recorded.
            Console.WriteLine("Nothing to revert: every setting this run changed is already back, or was changed since.");
            return Ok;
        }

        var newId = SetupRun.NewId(started, "windows-revert");
        var run = SetupRun.Create(newId, "windows-revert", null, SetupRun.Stamp(started), SetupRun.Stamp(DateTimeOffset.UtcNow), [], [], outcomes);
        store.WriteRunRecord(newId, run.ToJson());
        Console.WriteLine("Result:");
        SetupOutput.WriteWindows(outcomes);
        SetupOutput.WriteFollowUps(run);
        Console.Error.WriteLine($"Recorded as history run {newId}.");
        return run.Ok ? Ok : Failed;
    }

    private static Dictionary<string, List<string>>? Options(string[] args, out bool apply, out string? error)
    {
        apply = false;
        error = null;
        var options = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--apply")
            {
                apply = true;
            }
            else if (args[i] == "--no-restore-point")
            {
                options["--no-restore-point"] = [];
            }
            else if (args[i] is "--id" or "--preset" or "--category" or "--run" && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                var key = args[i];
                if (!options.TryGetValue(key, out var list))
                {
                    options[key] = list = [];
                }

                list.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
            else
            {
                error = $"windows: unexpected argument '{args[i]}'.";
                return null;
            }
        }

        return options;
    }

    private static string? Single(Dictionary<string, List<string>> options, string key) =>
        options.TryGetValue(key, out var list) && list.Count > 0 ? list[0] : null;

    private static int Bad(string message)
    {
        Console.Error.WriteLine(message);
        return UsageCode;
    }

    private static string Flags(WindowsOperation op) =>
        (op.NeedsElevation ? "  [admin]" : "") + (op.Risk == RiskClass.Undocumented ? "  [undocumented]" : "") + (op.Activation == Activation.Reboot ? "  [restart]" : "");

    public static string StateLabel(OperationState state) => state switch
    {
        OperationState.Applied => "applied",
        OperationState.NotApplied => "-",
        OperationState.Partial => "partial",
        OperationState.Inapplicable => "n/a",
        OperationState.Unknown => "unknown",
        OperationState.Manual => "manual",
        OperationState.Observation => "info",
        OperationState.Blocked => "blocked",
        _ => state.ToString(),
    };

    private static string RiskLabel(RiskClass risk) => risk switch
    {
        RiskClass.Observe => "information only (R0)",
        RiskClass.UserPreference => "user preference, reversible in Settings (R1)",
        RiskClass.SystemConfiguration => "system configuration, reversible (R2)",
        RiskClass.Undocumented => "undocumented setting: treat as an experiment (R3)",
        RiskClass.Manual => "manual procedure",
        _ => risk.ToString(),
    };

    public static string ActivationLabel(Activation activation) => activation switch
    {
        Activation.Immediate => "immediately",
        Activation.ExplorerRestart => "after Explorer restarts or you sign out",
        Activation.SignOut => "after signing out",
        Activation.Reboot => "after a restart",
        _ => activation.ToString(),
    };
}
