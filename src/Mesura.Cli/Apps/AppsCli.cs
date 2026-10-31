using Mesura.Cli.Setup;
using Mesura.Core.Apps;
using Mesura.Core.Setup;
using Mesura.Platform.Windows.Windows;
using Mesura.Setup.Windows;
using Mesura.Setup.Windows.Apps;

namespace Mesura.Cli.Apps;

/// <summary>
/// The <c>apps</c> verbs: show the catalog with what is installed, show the bundles, and install a selection. Install
/// previews by default; <c>--apply</c> runs winget for each app in turn, and Ctrl+C stops after the current app is
/// stopped. The catalog is an edited <c>apps.catalog.json</c> when there is one (<see cref="CatalogFiles"/>), else the
/// built-in copy.
/// </summary>
internal static class AppsCli
{
    public const string Usage = """
          mesura apps list
          mesura apps bundles
          mesura apps install (--id <app>[,<app>...] | --bundle <bundle>)... [--reinstall] [--apply]
          mesura apps updates
          mesura apps upgrade (--id <app>[,<app>...] | --all) [--apply]
          mesura apps uninstall --id <app>[,<app>...] [--apply]
        """;

    public const string Help = """
        apps list        Lists the app catalog with each app's install state, detected with winget.
                         Read-only.
        apps bundles     Lists the predefined app bundles and their members. Read-only.
        apps install     Previews installing the chosen apps and bundles: what would be installed,
                         skipped as already installed, or left to do by hand. With --apply it installs
                         them with winget one at a time; a failure never stops the rest, and Ctrl+C
                         cancels cleanly. --reinstall also runs apps that are already installed.
        apps updates     Lists the installed apps Mesura can update and whether winget offers a newer
                         version of each. Read-only.
        apps upgrade     Previews updating the chosen apps (or --all with an update available).
                         With --apply it updates them with winget one at a time, then asks winget
                         again whether an update is still offered. Only apps Mesura updates are
                         accepted; Ctrl+C cancels cleanly.
        apps uninstall   Previews uninstalling the chosen apps. With --apply it uninstalls them with
                         winget, then asks winget again whether each is really gone. Only the apps
                         Mesura has verified are accepted, and --apply needs Mesura to run as
                         administrator (without it nothing is started).
        """;

    private const int Ok = 0;
    private const int Failed = 1;
    private const int UsageCode = 2;

    private sealed record Request(string Verb, IReadOnlyList<string> AppIds, IReadOnlyList<string> BundleIds, bool Reinstall, bool Apply, bool All = false);

    public static int Run(string[] args)
    {
        var request = Parse(args, out var error);
        if (request is null)
        {
            Console.Error.WriteLine(error ?? Usage);
            return error is null ? Ok : UsageCode;
        }

        if (!TryLoadCatalog(out var catalog, out var source))
        {
            return Failed;
        }

        Console.Error.WriteLine($"Catalog: {source} ({catalog.Packages.Count} apps, {catalog.Bundles.Count} bundles).");
        return request.Verb switch
        {
            "list" => List(catalog),
            "bundles" => Bundles(catalog),
            "install" => Install(catalog, request),
            "updates" => Updates(catalog),
            "upgrade" => Upgrade(catalog, request),
            "uninstall" => Uninstall(catalog, request),
            _ => UsageCode,
        };
    }

    /// <summary>The catalog in use (edited copy or built-in), without printing anything; <c>null</c> if the edited copy is broken.</summary>
    public static AppCatalog? TryLoadCatalogQuietly() => CatalogFiles.Apps(Environment.IsPrivilegedProcess).Catalog;

    /// <summary>Loads the edited catalog if there is one (<see cref="CatalogFiles"/>), else the built-in copy.</summary>
    public static bool TryLoadCatalog(out AppCatalog catalog, out string source)
    {
        var loaded = CatalogFiles.Apps(Environment.IsPrivilegedProcess);
        catalog = loaded.Catalog ?? AppCatalog.Default;
        source = loaded.Source;
        if (loaded.Error is not null)
        {
            Console.Error.WriteLine(loaded.Error);
            return false;
        }

        if (CatalogFiles.StaleNote(AppCatalog.FileName, CatalogFiles.NewSinceCopied(loaded)) is { Length: > 0 } stale)
        {
            Console.Error.WriteLine(stale);
        }

        if (CatalogFiles.SetAsideNote(AppCatalog.FileName, loaded) is { Length: > 0 } aside)
        {
            Console.Error.WriteLine(aside);
        }

        return true;
    }

    private static Request? Parse(string[] args, out string? error)
    {
        error = null;
        if (args.Length == 0 || args is ["--help"] or ["-h"])
        {
            return null;
        }

        var verb = args[0];
        var apps = new List<string>();
        var bundles = new List<string>();
        bool reinstall = false, apply = false, all = false;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--id" or "--bundle" when verb == "install" && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal):
                    var target = args[i] == "--id" ? apps : bundles;
                    target.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--id" when verb is "upgrade" or "uninstall" && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal):
                    apps.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--reinstall" when verb == "install":
                    reinstall = true;
                    break;
                case "--all" when verb == "upgrade":
                    all = true;
                    break;
                case "--apply" when verb is "install" or "upgrade" or "uninstall":
                    apply = true;
                    break;
                default:
                    error = $"apps: unexpected argument '{args[i]}'.";
                    return null;
            }
        }

        switch (verb)
        {
            case "list" or "bundles" or "updates":
                return new Request(verb, [], [], false, false);
            case "uninstall" when apps.Count == 0:
                error = "apps uninstall needs --id <app>.";
                return null;
            case "uninstall":
                return new Request(verb, apps, [], false, apply);
            case "upgrade" when apps.Count == 0 && !all:
                error = "apps upgrade needs --id <app> or --all (see 'apps updates').";
                return null;
            case "upgrade" when apps.Count > 0 && all:
                error = "apps upgrade takes --id <app> or --all, not both.";
                return null;
            case "upgrade":
                return new Request(verb, apps, [], false, apply, all);
            case "install" when apps.Count == 0 && bundles.Count == 0:
                error = "apps install needs --id <app> or --bundle <bundle> (see 'apps list' and 'apps bundles').";
                return null;
            case "install":
                return new Request(verb, apps, bundles, reinstall, apply);
            default:
                error = $"Unknown apps verb '{verb}'. Use list, bundles, install, updates, upgrade or uninstall.";
                return null;
        }
    }

    private static int List(AppCatalog catalog)
    {
        var reader = new SystemStateReader();
        var winget = new WingetRunner(reader.User, reader.IsElevated);
        var plan = new AppInstallService(winget, AppDetectors.For(reader.User)).BuildPlan(catalog.Packages);
        if (!winget.IsAvailable())
        {
            Console.Error.WriteLine("winget (App Installer) is not available, so install state is unknown.");
        }
        else
        {
            Console.Error.WriteLine($"winget {winget.Version}.");
        }

        foreach (var group in plan.GroupBy(p => p.Package.Category))
        {
            Console.WriteLine(group.Key);
            foreach (var item in group)
            {
                Console.WriteLine($"  [{StateLabel(item)}] {item.Package.Id,-13} {item.Package.Name}  ({Origin(item.Package)})");
            }
        }

        return Ok;
    }

    private static int Bundles(AppCatalog catalog)
    {
        foreach (var bundle in catalog.Bundles)
        {
            Console.WriteLine($"{bundle.Id,-10} {bundle.Name}: {bundle.Description}");
            Console.WriteLine($"           {string.Join(", ", bundle.AppIds)}");
        }

        return Ok;
    }

    private static int Install(AppCatalog catalog, Request request)
    {
        var selected = new List<AppPackage>();
        foreach (var id in request.BundleIds)
        {
            if (catalog.BundleById(id) is not { } bundle)
            {
                Console.Error.WriteLine($"Unknown bundle '{id}' (see 'apps bundles').");
                return UsageCode;
            }

            selected.AddRange(catalog.Resolve(bundle));
        }

        foreach (var id in request.AppIds)
        {
            if (catalog.ById(id) is not { } package)
            {
                Console.Error.WriteLine($"Unknown app '{id}' (see 'apps list').");
                return UsageCode;
            }

            selected.Add(package);
        }

        var reader = new SystemStateReader();
        var winget = new WingetRunner(reader.User, reader.IsElevated);
        var service = new AppInstallService(winget, AppDetectors.For(reader.User));
        var plan = service.BuildPlan(selected);
        var skipInstalled = !request.Reinstall;

        Console.WriteLine(request.Apply ? "Installing:" : "Preview (nothing installed):");
        SetupOutput.WritePlan(plan, skipInstalled);
        if (!request.Apply)
        {
            Console.WriteLine("Re-run with --apply to install. Installers run silently; some may still ask for elevation.");
            return Ok;
        }

        var started = DateTimeOffset.UtcNow;
        var outcomes = SetupOutput.WithCancel((progress, token) => service.Install(plan, skipInstalled, progress, token));
        var runId = SetupRun.NewId(started, "apps-install");
        var run = SetupRun.Create(runId, "apps-install", null, SetupRun.Stamp(started), SetupRun.Stamp(DateTimeOffset.UtcNow), outcomes, []);
        new SetupStore().WriteRunRecord(runId, run.ToJson());

        Console.WriteLine("Result:");
        SetupOutput.WriteInstalls(run.Apps);
        Console.WriteLine(SetupOutput.Summary(run.Apps));
        SetupOutput.WriteFollowUps(run);
        Console.Error.WriteLine($"Recorded as history run {runId}.");
        return run.Ok ? Ok : Failed;
    }

    private static int Updates(AppCatalog catalog)
    {
        var service = Service();
        var checks = service.FindUpdates(service.BuildPlan(catalog.Packages.Where(p => p.Upgradable)));
        if (checks.Count == 0)
        {
            Console.WriteLine("No installed app that Mesura updates was found.");
            return Ok;
        }

        foreach (var check in checks)
        {
            Console.WriteLine($"  {UpdateLabel(check),-28} {check.Package.Id,-13} {check.Package.Name}");
        }

        return Ok;
    }

    private static string UpdateLabel(UpdateCheck check) => check.State switch
    {
        UpdateState.Available when check.InstalledVersion is { } from && check.AvailableVersion is { } to => $"update {from} -> {to}",
        UpdateState.Available => "update available",
        UpdateState.UpToDate => "up to date",
        _ => "unknown (winget didn't answer)",
    };

    private static AppInstallService Service()
    {
        var reader = new SystemStateReader();
        var winget = new WingetRunner(reader.User, reader.IsElevated);
        return new AppInstallService(winget, AppDetectors.For(reader.User));
    }

    private static int Upgrade(AppCatalog catalog, Request request)
    {
        var chosen = new List<AppPackage>();
        foreach (var id in request.AppIds)
        {
            if (catalog.ById(id) is not { } package)
            {
                Console.Error.WriteLine($"Unknown app '{id}' (see 'apps list').");
                return UsageCode;
            }

            if (!package.Upgradable)
            {
                Console.Error.WriteLine($"Mesura doesn't update '{id}': it updates itself or must be updated by hand (see 'apps updates' for the apps it does update).");
                return UsageCode;
            }

            chosen.Add(package);
        }

        var service = Service();
        var plan = service.BuildPlan(request.All ? catalog.Packages.Where(p => p.Upgradable) : chosen);
        var notInstalled = plan.Where(i => i.State != InstallState.Installed).Select(i => i.Package.Id).ToList();
        if (notInstalled.Count > 0 && !request.All)
        {
            Console.Error.WriteLine($"Not installed (or not detected): {string.Join(", ", notInstalled)}. Nothing to update for them.");
        }

        var checks = service.FindUpdates(plan);
        foreach (var check in checks)
        {
            Console.WriteLine($"  {UpdateLabel(check),-28} {check.Package.Id,-13} {check.Package.Name}");
        }

        var due = checks.Where(c => c.State == UpdateState.Available).Select(c => c.Package).ToList();
        if (due.Count == 0)
        {
            var unknown = checks.Any(c => c.State == UpdateState.Unknown);
            Console.WriteLine(unknown ? "Nothing to update that winget confirmed." : "Nothing to update.");
            return unknown ? Failed : Ok;
        }

        if (!request.Apply)
        {
            Console.WriteLine($"Preview (nothing updated). Re-run with --apply to update {due.Count} app(s). Installers run silently; some ask for elevation.");
            return Ok;
        }

        var started = DateTimeOffset.UtcNow;
        var outcomes = SetupOutput.WithCancel((progress, token) => service.Upgrade(due, progress, token));
        var runId = SetupRun.NewId(started, "apps-update");
        var run = SetupRun.Create(runId, "apps-update", null, SetupRun.Stamp(started), SetupRun.Stamp(DateTimeOffset.UtcNow), outcomes, []);
        new SetupStore().WriteRunRecord(runId, run.ToJson());

        Console.WriteLine("Result:");
        foreach (var e in run.Apps)
        {
            Console.WriteLine($"  {UpdateWord(e.Status),-17} {e.AppId,-13} {e.Detail}");
        }

        Console.Error.WriteLine($"Recorded as history run {runId}.");
        return run.Ok ? Ok : Failed;
    }

    private static int Uninstall(AppCatalog catalog, Request request)
    {
        var chosen = new List<AppPackage>();
        foreach (var id in request.AppIds)
        {
            if (catalog.ById(id) is not { } package)
            {
                Console.Error.WriteLine($"Unknown app '{id}' (see 'apps list').");
                return UsageCode;
            }

            if (!package.Uninstallable)
            {
                Console.Error.WriteLine($"Mesura doesn't uninstall '{id}': it is not one of the apps whose uninstall it has verified. Remove it in Windows' Settings, Installed apps.");
                return UsageCode;
            }

            chosen.Add(package);
        }

        var reader = new SystemStateReader();
        var service = new AppInstallService(new WingetRunner(reader.User, reader.IsElevated), AppDetectors.For(reader.User));
        var plan = service.BuildPlan(chosen);
        foreach (var item in plan)
        {
            Console.WriteLine($"  {(item.State switch { InstallState.Installed => "uninstall", InstallState.NotInstalled => "not installed", _ => "state unknown" }),-26} {item.Package.Id,-13} {item.Package.Name}");
        }

        var due = plan.Where(i => i.State == InstallState.Installed).Select(i => i.Package).ToList();
        if (due.Count == 0)
        {
            var unknown = plan.Any(i => i.State == InstallState.Unknown);
            Console.WriteLine(unknown ? "Nothing to uninstall that winget confirmed." : "Nothing to uninstall.");
            return unknown ? Failed : Ok;
        }

        if (!reader.IsElevated)
        {
            Console.Error.WriteLine("Uninstalling needs Mesura to run as administrator (run this from an administrator terminal). " + (request.Apply ? "Nothing was started." : "This is only a preview."));
            return request.Apply ? Failed : Ok;
        }

        if (!request.Apply)
        {
            Console.WriteLine($"Preview (nothing uninstalled). Re-run with --apply to uninstall {due.Count} app(s). The app's own settings and documents are normally kept.");
            return Ok;
        }

        var started = DateTimeOffset.UtcNow;
        var outcomes = SetupOutput.WithCancel((progress, token) => service.Uninstall(due, progress, token));
        var runId = SetupRun.NewId(started, "apps-uninstall");
        var run = SetupRun.Create(runId, "apps-uninstall", null, SetupRun.Stamp(started), SetupRun.Stamp(DateTimeOffset.UtcNow), outcomes, []);
        new SetupStore().WriteRunRecord(runId, run.ToJson());

        Console.WriteLine("Result:");
        foreach (var e in run.Apps)
        {
            Console.WriteLine($"  {UninstallWord(e.Status),-17} {e.AppId,-13} {e.Detail}");
        }

        Console.Error.WriteLine($"Recorded as history run {runId}.");
        return run.Ok ? Ok : Failed;
    }

    private static string UninstallWord(InstallStatus status) => status switch
    {
        InstallStatus.Succeeded => "uninstalled",
        InstallStatus.SucceededRebootRequired => "restart needed",
        InstallStatus.AlreadyInstalled => "not installed",
        InstallStatus.Cancelled => "cancelled",
        InstallStatus.WingetUnavailable => "no winget",
        _ => "FAILED",
    };

    private static string UpdateWord(InstallStatus status) => status switch
    {
        InstallStatus.Succeeded => "updated",
        InstallStatus.SucceededRebootRequired => "restart needed",
        InstallStatus.AlreadyInstalled => "up to date",
        InstallStatus.Cancelled => "cancelled",
        InstallStatus.WingetUnavailable => "no winget",
        _ => "FAILED",
    };

    private static string Origin(AppPackage p) => SetupOutput.Origin(p);

    private static string StateLabel(AppPlanItem item) => item.State switch
    {
        InstallState.Installed => "installed",
        InstallState.NotInstalled => "  -      ",
        _ when item.Package.Source == AppSource.Manual => "manual   ",
        _ when !item.Package.Detectable => "no detect",
        _ => "unknown  ",
    };
}
