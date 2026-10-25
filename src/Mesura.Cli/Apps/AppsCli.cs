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
        """;

    public const string Help = """
        apps list        Lists the app catalog with each app's install state, detected with winget.
                         Read-only.
        apps bundles     Lists the predefined app bundles and their members. Read-only.
        apps install     Previews installing the chosen apps and bundles: what would be installed,
                         skipped as already installed, or left to do by hand. With --apply it installs
                         them with winget one at a time; a failure never stops the rest, and Ctrl+C
                         cancels cleanly. --reinstall also runs apps that are already installed.
        """;

    private const int Ok = 0;
    private const int Failed = 1;
    private const int UsageCode = 2;

    private sealed record Request(string Verb, IReadOnlyList<string> AppIds, IReadOnlyList<string> BundleIds, bool Reinstall, bool Apply);

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
            _ => UsageCode,
        };
    }

    /// <summary>The catalog in use (edited copy or built-in), without printing anything; <c>null</c> if the edited copy is broken.</summary>
    public static AppCatalog? TryLoadCatalogQuietly() => CatalogFiles.Apps().Catalog;

    /// <summary>Loads the edited catalog if there is one (<see cref="CatalogFiles"/>), else the built-in copy.</summary>
    public static bool TryLoadCatalog(out AppCatalog catalog, out string source)
    {
        var loaded = CatalogFiles.Apps();
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
        bool reinstall = false, apply = false;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--id" or "--bundle" when verb == "install" && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal):
                    var target = args[i] == "--id" ? apps : bundles;
                    target.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--reinstall" when verb == "install":
                    reinstall = true;
                    break;
                case "--apply" when verb == "install":
                    apply = true;
                    break;
                default:
                    error = $"apps: unexpected argument '{args[i]}'.";
                    return null;
            }
        }

        switch (verb)
        {
            case "list" or "bundles":
                return new Request(verb, [], [], false, false);
            case "install" when apps.Count == 0 && bundles.Count == 0:
                error = "apps install needs --id <app> or --bundle <bundle> (see 'apps list' and 'apps bundles').";
                return null;
            case "install":
                return new Request(verb, apps, bundles, reinstall, apply);
            default:
                error = $"Unknown apps verb '{verb}'. Use list, bundles or install.";
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
