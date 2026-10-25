using Mesura.Cli.Apps;
using Mesura.Core.Apps;
using Mesura.Core.Setup;
using Mesura.Core.Windows;
using Mesura.Platform.Windows;
using Mesura.Platform.Windows.Windows;
using Mesura.Setup.Windows;
using Mesura.Setup.Windows.Apps;
using Mesura.Setup.Windows.Profiles;
using Mesura.Setup.Windows.Windows;

namespace Mesura.Cli.Setup;

/// <summary>
/// The <c>profile</c> and <c>history</c> verbs. <c>profile run</c> previews a setup profile (apps to install, settings
/// to import) and, with <c>--apply</c>, runs it and records the run; <c>history</c> lists and shows recorded runs.
/// </summary>
internal static class ProfileCli
{
    public const string Usage = """
          mesura profile save --out <name.profile.json>
          mesura profile run --in <profile.json> [--apply]
          mesura history list
          mesura history show --id <run id>
        """;

    public const string Help = """
        profile save     Reads this PC (read-only) and saves it as a profile: the catalog apps installed
                         here, the Windows operations applied here, and an export of the supported apps'
                         settings as <name>.settings.json beside the profile. The same as Save profile in
                         the app.
        profile run      Reads a setup profile and previews it: the apps it would install or skip, the
                         settings its config bundle would import and the Windows operations it would
                         apply. With --apply it installs the apps, imports the settings (apps that are
                         running are skipped), applies the Windows operations, and records the run.
                         Re-running a profile is safe: done steps are skipped.
        history list     Lists the recorded runs (installs, imports, Windows changes, profiles), newest
                         first. Read-only.
        history show     Shows one recorded run step by step. Read-only.
        """;

    private const int Ok = 0;
    private const int Failed = 1;
    private const int UsageCode = 2;

    public static int RunProfile(string[] args)
    {
        if (args is ["save", "--out", var outPath])
        {
            return SaveProfile(outPath);
        }

        string? path = null;
        var apply = false;
        if (args is not ["run", ..])
        {
            Console.Error.WriteLine(Usage);
            return args.Length == 0 || args is ["--help"] or ["-h"] ? Ok : UsageCode;
        }

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--in" when i + 1 < args.Length:
                    path = args[++i];
                    break;
                case "--apply":
                    apply = true;
                    break;
                default:
                    Console.Error.WriteLine($"profile: unexpected argument '{args[i]}'.");
                    return UsageCode;
            }
        }

        if (path is null)
        {
            Console.Error.WriteLine("profile run needs --in <profile.json>.");
            return UsageCode;
        }

        if (!AppsCli.TryLoadCatalog(out var catalog, out _) || !WindowsCli.TryLoadCatalog(out var windowsCatalog, out _))
        {
            return Failed;
        }

        SetupProfile profile;
        try
        {
            profile = SetupProfile.Parse(File.ReadAllText(path), catalog, windowsCatalog);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            Console.Error.WriteLine($"Could not read the profile: {e.Message}");
            return Failed;
        }

        var reader = new SystemStateReader();
        var installer = AppDetectors.Installer(reader.User, reader.IsElevated);
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var plan = ProfileRunner.Plan(profile, folder, catalog, installer, windowsCatalog, new SetupStore());
        var configurator = new WindowsConfigurator(reader, new WindowsWriter(reader.User));

        Console.WriteLine($"Profile: {profile.Name}{(profile.Description.Length > 0 ? $" - {profile.Description}" : "")}");
        if (!ProcessUser.Is(reader.User, reader.IsElevated) && (plan.Bundle is not null || plan.BundleError is not null))
        {
            // App settings live in this process account's profile, which isn't the signed-in user's: leave them out.
            Console.Error.WriteLine($"App settings are skipped: Mesura runs as {ProcessUser.Account}, not as the signed-in user"
                + $" ({(reader.User.Resolved ? reader.User.Account : "unknown")}), so the settings it could write would be that account's. Run it as that user to restore them.");
            plan = plan with { Bundle = null, Ports = [], BundleError = null };
        }
        // A missing settings file stops a run (it would import nothing and look done), but a preview still shows the
        // apps and Windows settings, with the problem in step 2.
        if (plan.BundleError is not null && apply)
        {
            Console.Error.WriteLine(plan.BundleError + " Put a readable settings file beside the profile, then run it again.");
            return Failed;
        }

        // Two of its Windows operations setting one setting two ways would both be refused at the end of the run, after
        // the apps and settings steps: say so up front, and don't start the run.
        var conflicts = WindowsConfigurator.Conflicts(plan.Windows, reader);
        var conflictText = "The profile's Windows settings set one setting two ways; remove one of each pair:"
            + string.Concat(conflicts.Select(c => Environment.NewLine + "  " + c));
        if (conflicts.Count > 0 && apply)
        {
            Console.Error.WriteLine(conflictText);
            return Failed;
        }

        if (!apply)
        {
            Console.WriteLine("Preview (nothing changed).");
            Console.WriteLine("1. Apps:");
            if (plan.WingetMissing)
            {
                Console.WriteLine("  winget isn't available, so install state can't be read and nothing can be installed. Install App Installer, then run again.");
            }

            SetupOutput.WritePlan(plan.Apps, skipInstalled: true, plan.InstalledEarlier);
            Console.WriteLine("2. Settings (as they would import right now; apps installed in step 1 may need a first start):");
            if (plan.BundleError is not null)
            {
                Console.WriteLine($"  {plan.BundleError} The profile can't run until its settings file beside it can be read.");
            }
            else if (plan.Bundle is null)
            {
                Console.WriteLine("  (none in this profile)");
            }
            else
            {
                SetupOutput.WriteConfig(ProfileRunner.PreviewConfig(plan), preview: true);
            }
            Console.WriteLine("3. Windows settings:");
            SetupOutput.WriteScope(reader);
            if (plan.Windows.Count == 0)
            {
                Console.WriteLine("  (none selected)");
            }

            SetupOutput.WriteWindowsPreview(configurator.Preview(plan.Windows), reader.IsElevated);
            if (conflicts.Count > 0)
            {
                Console.WriteLine(conflictText);
            }

            Console.WriteLine(plan.BundleError is null && conflicts.Count == 0
                ? "Re-run with --apply to run the profile."
                : "Fix what is named above, then re-run with --apply.");
            return plan.BundleError is null && conflicts.Count == 0 ? Ok : Failed;
        }

        Console.WriteLine("Running.");
        SetupOutput.WritePlan(plan.Apps, skipInstalled: true, plan.InstalledEarlier);
        var run = SetupOutput.WithCancel((progress, token) =>
            ProfileRunner.Run(plan, installer, new SetupStore(), () => DateTimeOffset.UtcNow, progress, token, configurator));
        Write(run);
        SetupOutput.WriteFollowUps(run, catalog);
        Console.Error.WriteLine(run.ChangedNothing
            ? "Everything in the profile is already in place; nothing needed doing, so nothing was recorded."
            : $"Recorded as history run {run.Id}." + RevertHint(run));
        return run.Ok ? Ok : Failed;
    }

    /// <summary>Saves this PC as a profile plus its settings bundle, side by side (read-only on the PC itself).</summary>
    private static int SaveProfile(string outPath)
    {
        if (!AppsCli.TryLoadCatalog(out var catalog, out _) || !WindowsCli.TryLoadCatalog(out var windowsCatalog, out _))
        {
            return Failed;
        }

        var full = Path.GetFullPath(outPath);
        var folder = Path.GetDirectoryName(full)!;
        if (!Directory.Exists(folder))
        {
            Console.Error.WriteLine($"The folder {folder} doesn't exist.");
            return Failed;
        }

        var file = Path.GetFileName(full);
        var stem = file.EndsWith(".profile.json", StringComparison.OrdinalIgnoreCase) ? file[..^".profile.json".Length] : Path.GetFileNameWithoutExtension(file);
        var bundleName = stem + ".settings.json";
        var reader = new SystemStateReader();
        var sameUser = ProcessUser.Is(reader.User, reader.IsElevated);
        if (!sameUser)
        {
            Console.Error.WriteLine($"App settings aren't saved: Mesura runs as {ProcessUser.Account}, not as the signed-in user, so the settings it could read would be that account's.");
        }

        var capture = ProfileRunner.Capture(stem, bundleName, catalog, AppDetectors.Installer(reader.User, reader.IsElevated), windowsCatalog,
            reader, SetupRun.Stamp(DateTimeOffset.UtcNow), includeSettings: sameUser);
        if (!capture.Profile.SelectsAnything)
        {
            Console.Error.WriteLine("Nothing to save: no catalog app is installed, no Windows operation is applied and no app settings were found.");
            return Failed;
        }

        if (capture.BundleJson is { } bundle)
        {
            LocalStore.WriteUserExport(Path.Combine(folder, bundleName), System.Text.Encoding.UTF8.GetBytes(bundle));
        }

        LocalStore.WriteUserExport(full, System.Text.Encoding.UTF8.GetBytes(capture.Profile.ToJson()));
        Console.WriteLine($"Profile: {full}");
        if (capture.BundleJson is not null)
        {
            Console.WriteLine($"Settings: {Path.Combine(folder, bundleName)} ({string.Join(", ", capture.Settings.Select(s => s.Port.AppName))})");
        }

        Console.WriteLine($"Apps ({capture.Profile.Apps.Count}): {string.Join(", ", capture.Profile.Apps).DefaultIfEmpty("none")}");
        Console.WriteLine($"Windows operations ({capture.Profile.Windows.Count}): {string.Join(", ", capture.Profile.Windows).DefaultIfEmpty("none")}");
        return Ok;
    }

    public static int RunHistory(string[] args)
    {
        var store = new SetupStore();
        switch (args)
        {
            case ["list"]:
                var ids = store.ListRuns();
                if (ids.Count == 0)
                {
                    Console.WriteLine($"No runs recorded yet ({store.HistoryFolder}).");
                }

                foreach (var id in ids)
                {
                    var run = TryRead(store, id);
                    Console.WriteLine(run is null
                        ? $"{id}  (unreadable record)"
                        : $"{id}  {StepWords.Run(RunFollowUp.Status(run)),-15}  {run.ProfileName ?? run.Operation}  " +
                          $"apps: {SetupOutput.Summary(run.Apps).DefaultIfEmpty("-")}  config: {run.Config.Count} app(s)  " +
                          $"windows: {run.WindowsSteps.Count(w => w.Changes.Count > 0)} changed");
                }

                return Ok;
            case ["show", "--id", var runId]:
                var shown = TryRead(store, runId);
                if (shown is null)
                {
                    Console.Error.WriteLine($"No readable run '{runId}' (see 'history list').");
                    return Failed;
                }

                Console.WriteLine($"{shown.Id}: {shown.Operation}{(shown.ProfileName is null ? "" : $" '{shown.ProfileName}'")}, {shown.StartedUtc} to {shown.FinishedUtc}"
                    + $" ({StepWords.Run(RunFollowUp.Status(shown))})");
                Write(shown);
                SetupOutput.WriteFollowUps(shown);
                if (shown.Config.Any(c => c.Written.Count > 0))
                {
                    Console.WriteLine($"Backups of replaced files: {Path.Combine(store.FindRunFolder(shown.Id)!, "backups")}");
                }

                if (RevertHint(shown) is { Length: > 0 } hint)
                {
                    Console.WriteLine(hint.Trim());
                }

                return Ok;
            default:
                Console.Error.WriteLine(Usage);
                return args.Length == 0 ? Ok : UsageCode;
        }
    }

    /// <summary>How to undo a run's Windows changes (only those: installed apps and imported settings stay), or empty.</summary>
    private static string RevertHint(SetupRun run) => run.WindowsSteps.Any(w => w.Changes.Count > 0)
        ? $" Undo its Windows changes with 'mesura windows revert --run {run.Id} --apply' (installed apps and imported settings stay)."
        : "";

    private static void Write(SetupRun run)
    {
        if (run.Apps.Count > 0)
        {
            Console.WriteLine("Apps:");
            SetupOutput.WriteInstalls(run.Apps);
            Console.WriteLine($"  {SetupOutput.Summary(run.Apps)}");
        }

        if (run.Config.Count > 0)
        {
            Console.WriteLine("Settings:");
            SetupOutput.WriteConfig(run.Config, preview: false);
        }

        if (run.WindowsSteps.Count > 0)
        {
            Console.WriteLine("Windows settings:");
            SetupOutput.WriteWindows(run.WindowsSteps);
        }
    }

    private static SetupRun? TryRead(SetupStore store, string id)
    {
        try
        {
            return store.ReadRunRecord(id) is { } json ? SetupRun.FromJson(json) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    private static string DefaultIfEmpty(this string text, string fallback) => text.Length == 0 ? fallback : text;
}
