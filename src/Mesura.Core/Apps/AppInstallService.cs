namespace Mesura.Core.Apps;

/// <summary>The result of launching one winget command: whether it started, its exit code and its captured output.</summary>
public sealed record WingetRunResult(bool Launched, int ExitCode, string Output = "", string Refusal = "")
{
    public static readonly WingetRunResult NotLaunched = new(false, 0);

    /// <summary>winget wasn't started on purpose: the user it would run as can't be established (the reason says why).</summary>
    public static WingetRunResult Refused(string reason) => new(false, 0, "", reason);
}

/// <summary>
/// Launches winget. The one implementation (<c>WingetRunner</c> in the platform layer) owns the process launch;
/// this abstraction keeps the install sequencing in <see cref="AppInstallService"/> pure and testable.
/// </summary>
public interface IWingetRunner
{
    /// <summary>Whether winget is present and runnable on this machine.</summary>
    bool IsAvailable();

    /// <summary>
    /// Why the last availability check didn't start winget on purpose (the user it would run as couldn't be established,
    /// or Windows wouldn't start it as that user), or empty when winget simply isn't there or runs.
    /// </summary>
    string Refusal => "";

    /// <summary>Runs <c>winget list --exact --id</c> for one package. Safe to call concurrently.</summary>
    WingetRunResult List(AppPackage package);

    /// <summary>Runs <c>winget install</c> for a package, streaming output lines to <paramref name="onOutput"/>.</summary>
    WingetRunResult Install(AppPackage package, Action<string>? onOutput, CancellationToken cancellationToken);

    /// <summary>Runs <c>winget list --exact --id --upgrade-available</c> for one package: whether winget offers a newer version. Safe to call concurrently.</summary>
    WingetRunResult ListUpgradeAvailable(AppPackage package) => WingetRunResult.NotLaunched;

    /// <summary>Runs <c>winget upgrade --exact --id</c> for one package, streaming output lines to <paramref name="onOutput"/>.</summary>
    WingetRunResult Upgrade(AppPackage package, Action<string>? onOutput, CancellationToken cancellationToken) => WingetRunResult.NotLaunched;

    /// <summary>
    /// Runs <c>winget uninstall --exact --id</c> for one package, streaming output lines to <paramref name="onOutput"/>. Refused (not launched, with the
    /// reason) unless Mesura runs as administrator: an uninstaller run without it fails or silently does nothing.
    /// </summary>
    WingetRunResult Uninstall(AppPackage package, Action<string>? onOutput, CancellationToken cancellationToken) => WingetRunResult.NotLaunched;
}

/// <summary>
/// Detects which applications are installed and installs a chosen set, one at a time, isolating each so one failure or
/// a cancellation never breaks the rest of the batch. It holds no package data of its own (<see cref="AppCatalog"/>)
/// and launches nothing directly (<see cref="IWingetRunner"/>).
/// </summary>
public sealed class AppInstallService
{
    private readonly IWingetRunner _winget;
    private readonly IReadOnlyDictionary<string, Func<InstallState>> _detectors;

    /// <summary>
    /// The folder the user chose for a package (read when its install starts, so a choice made after the plan was built still counts); consulted only
    /// for a package whose installer takes one (<see cref="AppPackage.InstallLocation"/>).
    /// </summary>
    public Func<AppPackage, string?>? InstallFolderFor { get; set; }

    /// <param name="detectors">
    /// Detectors by name (<see cref="AppPackage.Detector"/>) for apps winget can't see; each reads the app's own files.
    /// </param>
    public AppInstallService(IWingetRunner winget, IReadOnlyDictionary<string, Func<InstallState>>? detectors = null)
    {
        _winget = winget ?? throw new ArgumentNullException(nameof(winget));
        _detectors = detectors ?? new Dictionary<string, Func<InstallState>>();
    }

    /// <summary>
    /// Why winget was refused rather than missing, when it was (another account, the Secondary Logon service off), so
    /// the app shows that reason instead of "winget isn't available"; empty otherwise.
    /// </summary>
    public string WingetRefusal => _winget.Refusal;

    /// <summary>Whether this package's state can be read: by winget, or by its own detector.</summary>
    public bool CanDetect(AppPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return HasDetector(package) || (package.Source != AppSource.Manual && package.Detectable);
    }

    private bool HasDetector(AppPackage package) => package.Detector.Length > 0 && _detectors.ContainsKey(package.Detector);

    /// <summary>
    /// Detects the install state of each package with one exact-id <c>winget list</c> each, a few at a time. A per-id
    /// query is used rather than one full list because only it correlates apps installed outside winget (Brave and
    /// Discord installers, for example) with their winget ids. Everything is <see cref="InstallState.Unknown"/> if
    /// winget is unavailable.
    /// </summary>
    public IReadOnlyList<AppPlanItem> BuildPlan(IEnumerable<AppPackage> packages)
    {
        ArgumentNullException.ThrowIfNull(packages);
        var distinct = packages.DistinctBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList();
        var states = new InstallState[distinct.Count];

        var queryable = distinct.Any(p => !HasDetector(p) && p.Source != AppSource.Manual && p.Detectable) && _winget.IsAvailable();
        Parallel.For(0, distinct.Count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
        {
            var package = distinct[i];
            states[i] = HasDetector(package) ? Detect(package)
                : queryable && package.Source != AppSource.Manual && package.Detectable
                    ? WingetOutput.DetectState(_winget.List(package), package.WingetId)
                    : InstallState.Unknown;
        });

        return [.. distinct.Select((p, i) => new AppPlanItem(p, states[i]))];
    }

    private InstallState Detect(AppPackage package)
    {
        try
        {
            return _detectors[package.Detector]();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return InstallState.Unknown; // the app's files couldn't be read: unknown, never "not installed"
        }
    }

    /// <summary>
    /// Asks winget, once per app, whether a newer version is offered, for the installed apps Mesura updates
    /// (<see cref="AppPackage.Upgradable"/>) and for no others: an app that updates itself, or isn't installed, gets no answer and so no
    /// update action. Needs winget; <see cref="UpdateState.Unknown"/> for each if it is unavailable.
    /// </summary>
    public IReadOnlyList<UpdateCheck> FindUpdates(IEnumerable<AppPlanItem> plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var candidates = plan
            .Where(i => i.State == InstallState.Installed && i.Package.Upgradable && i.Package.Source != AppSource.Manual)
            .Select(i => i.Package)
            .DistinctBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidates.Count == 0)
        {
            return [];
        }

        var wingetAvailable = _winget.IsAvailable();
        var checks = new UpdateCheck[candidates.Count];
        Parallel.For(0, candidates.Count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
        {
            checks[i] = wingetAvailable ? CheckUpdate(candidates[i]) : new UpdateCheck(candidates[i], UpdateState.Unknown);
        });
        return checks;
    }

    private UpdateCheck CheckUpdate(AppPackage package)
    {
        try
        {
            return WingetOutput.ParseUpgradeAvailable(package, _winget.ListUpgradeAvailable(package));
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new UpdateCheck(package, UpdateState.Unknown); // one app's failed question must not break the rest
        }
    }

    /// <summary>
    /// Updates each package in order, one at a time, isolating failures like <see cref="Install"/>. A package Mesura doesn't update is refused
    /// without running anything. A success from winget is checked: after it, winget is asked once more whether an update is still offered, and
    /// if one is, the app is reported as not updated rather than trusting the exit code (an installer can exit 0 and leave the old version, for
    /// example waiting for a restart or for the app to close). <paramref name="onProgress"/> receives each package before it starts and each meaningful output line.
    /// </summary>
    public IReadOnlyList<InstallOutcome> Upgrade(
        IReadOnlyList<AppPackage> packages,
        Action<AppPackage, string>? onProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packages);

        var wingetAvailable = _winget.IsAvailable();
        var outcomes = new List<InstallOutcome>();
        foreach (var package in packages)
        {
            outcomes.Add(UpgradeOne(package, wingetAvailable, onProgress, cancellationToken));
        }

        return outcomes;
    }

    /// <summary>
    /// Uninstalls each package in order, one at a time, isolating failures like <see cref="Install"/>. A package Mesura doesn't uninstall is refused
    /// without running anything. A success from winget is checked: afterwards winget is asked whether the app is still installed, and if it is,
    /// the app is reported as not uninstalled rather than trusting the exit code.
    /// </summary>
    public IReadOnlyList<InstallOutcome> Uninstall(
        IReadOnlyList<AppPackage> packages,
        Action<AppPackage, string>? onProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packages);

        var wingetAvailable = _winget.IsAvailable();
        var outcomes = new List<InstallOutcome>();
        foreach (var package in packages)
        {
            outcomes.Add(UninstallOne(package, wingetAvailable, onProgress, cancellationToken));
        }

        return outcomes;
    }

    private InstallOutcome UninstallOne(AppPackage package, bool wingetAvailable, Action<AppPackage, string>? onProgress, CancellationToken cancellationToken)
    {
        InstallOutcome Outcome(InstallStatus status, string detail) => new(package.Id, package.WingetId, status, detail);

        if (!package.Uninstallable || package.Source == AppSource.Manual)
        {
            return Outcome(InstallStatus.Failed, "Mesura doesn't uninstall this app; remove it in Windows' Settings, Installed apps.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Outcome(InstallStatus.Cancelled, "Cancelled before this app.");
        }

        if (!wingetAvailable)
        {
            return Outcome(InstallStatus.WingetUnavailable, _winget.Refusal is { Length: > 0 } refusal ? refusal : "winget (App Installer) is not available on this machine.");
        }

        onProgress?.Invoke(package, $"Uninstalling {package.Name} ({package.WingetId})");
        try
        {
            var run = _winget.Uninstall(package, line =>
            {
                if (!WingetOutput.IsNoise(line))
                {
                    onProgress?.Invoke(package, line);
                }
            }, cancellationToken);

            if (!run.Launched)
            {
                return Outcome(InstallStatus.Failed, run.Refusal.Length > 0 ? run.Refusal : "winget could not be launched for this package.");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Outcome(InstallStatus.Cancelled, "Cancelled during the uninstall; winget was stopped.");
            }

            var (status, detail) = WingetOutput.DescribeUninstall(run.ExitCode);
            if (status is not (InstallStatus.Succeeded or InstallStatus.SucceededRebootRequired))
            {
                return Outcome(status, detail);
            }

            return WingetOutput.DetectState(_winget.List(package), package.WingetId) switch
            {
                InstallState.Installed => Outcome(InstallStatus.Failed,
                    $"winget finished without an error, but {package.Name} is still installed; close it and try again, or remove it in Windows' Settings. ({WingetOutput.FormatExitCode(run.ExitCode)})"),
                InstallState.Unknown => Outcome(status, $"{detail} Whether it is gone could not be re-checked."),
                _ => Outcome(status, detail),
            };
        }
        catch (OperationCanceledException)
        {
            return Outcome(InstallStatus.Cancelled, "Cancelled during the uninstall; winget was stopped.");
        }
        catch (Exception e)
        {
            return Outcome(InstallStatus.Failed, $"{e.GetType().Name}: {e.Message}");
        }
    }

    private InstallOutcome UpgradeOne(AppPackage package, bool wingetAvailable, Action<AppPackage, string>? onProgress, CancellationToken cancellationToken)
    {
        InstallOutcome Outcome(InstallStatus status, string detail) => new(package.Id, package.WingetId, status, detail);

        if (!package.Upgradable || package.Source == AppSource.Manual)
        {
            return Outcome(InstallStatus.Failed, "Mesura doesn't update this app; it keeps itself up to date or must be updated by hand.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Outcome(InstallStatus.Cancelled, "Cancelled before this app.");
        }

        if (!wingetAvailable)
        {
            return Outcome(InstallStatus.WingetUnavailable, _winget.Refusal is { Length: > 0 } refusal ? refusal : "winget (App Installer) is not available on this machine.");
        }

        onProgress?.Invoke(package, $"Updating {package.Name} ({package.WingetId})");
        try
        {
            var before = CheckUpdate(package);
            var run = _winget.Upgrade(package, line =>
            {
                if (!WingetOutput.IsNoise(line))
                {
                    onProgress?.Invoke(package, line);
                }
            }, cancellationToken);

            if (!run.Launched)
            {
                return Outcome(InstallStatus.Failed, run.Refusal.Length > 0 ? run.Refusal : "winget could not be launched for this package.");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Outcome(InstallStatus.Cancelled, "Cancelled during the update; winget was stopped.");
            }

            var (status, detail) = WingetOutput.DescribeUpgrade(run.ExitCode);
            if (status is not (InstallStatus.Succeeded or InstallStatus.SucceededRebootRequired))
            {
                return Outcome(status, detail);
            }

            var after = CheckUpdate(package);
            if (after.State == UpdateState.Available)
            {
                return Outcome(InstallStatus.Failed,
                    $"winget finished without an error, but still offers {after.AvailableVersion ?? "a newer version"}; close the app or restart Windows, then try again. ({WingetOutput.FormatExitCode(run.ExitCode)})");
            }

            var versions = before.InstalledVersion is { } from && before.AvailableVersion is { } to ? $" {from} to {to}" : "";
            var unconfirmed = after.State == UpdateState.Unknown ? " Whether it took could not be re-checked." : "";
            return Outcome(status, (status == InstallStatus.Succeeded ? $"Updated{versions}." : $"{detail}{versions}") + unconfirmed);
        }
        catch (OperationCanceledException)
        {
            return Outcome(InstallStatus.Cancelled, "Cancelled during the update; winget was stopped.");
        }
        catch (Exception e)
        {
            return Outcome(InstallStatus.Failed, $"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// Installs each package in plan order. Already-installed packages are reported and skipped when
    /// <paramref name="skipInstalled"/> is set; manual entries are reported, never run. The
    /// <paramref name="onProgress"/> callback receives each package before it starts and each meaningful output line.
    /// Cancellation stops before the next package and signals the running one to stop.
    /// </summary>
    public IReadOnlyList<InstallOutcome> Install(
        IReadOnlyList<AppPlanItem> plan,
        bool skipInstalled,
        Action<AppPackage, string>? onProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var wingetAvailable = !plan.Any(i => i.Package.Source != AppSource.Manual) || _winget.IsAvailable();
        var outcomes = new List<InstallOutcome>();
        foreach (var item in plan)
        {
            outcomes.Add(InstallOne(item, skipInstalled, wingetAvailable, onProgress, cancellationToken));
        }

        return outcomes;
    }

    private InstallOutcome InstallOne(
        AppPlanItem item,
        bool skipInstalled,
        bool wingetAvailable,
        Action<AppPackage, string>? onProgress,
        CancellationToken cancellationToken)
    {
        var package = item.Package;
        if (package.InstallLocation && InstallFolderFor?.Invoke(package) is { Length: > 0 } folder)
        {
            package = package with { InstallFolder = folder };
        }

        InstallOutcome Outcome(InstallStatus status, string detail) => new(package.Id, package.WingetId, status, detail);

        if (package.Source == AppSource.Manual)
        {
            return Outcome(InstallStatus.Manual, $"Install by hand from {package.Url}");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Outcome(InstallStatus.Cancelled, "Cancelled before this app.");
        }

        if (skipInstalled && item.State == InstallState.Installed)
        {
            return Outcome(InstallStatus.AlreadyInstalled, "Already installed; skipped.");
        }

        if (!wingetAvailable)
        {
            return Outcome(InstallStatus.WingetUnavailable, _winget.Refusal is { Length: > 0 } refusal ? refusal : "winget (App Installer) is not available on this machine.");
        }

        onProgress?.Invoke(package, $"Installing {package.Name} ({package.WingetId})");
        try
        {
            var run = _winget.Install(package, line =>
            {
                if (!WingetOutput.IsNoise(line))
                {
                    onProgress?.Invoke(package, line);
                }
            }, cancellationToken);

            if (!run.Launched)
            {
                return Outcome(InstallStatus.Failed, run.Refusal.Length > 0 ? run.Refusal : "winget could not be launched for this package.");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Outcome(InstallStatus.Cancelled, "Cancelled during install; winget was stopped.");
            }

            var (status, detail) = WingetOutput.DescribeInstall(run.ExitCode);
            return Outcome(status, detail);
        }
        catch (OperationCanceledException)
        {
            return Outcome(InstallStatus.Cancelled, "Cancelled during install; winget was stopped.");
        }
        catch (Exception e)
        {
            // One package's failure must not break the batch.
            return Outcome(InstallStatus.Failed, $"{e.GetType().Name}: {e.Message}");
        }
    }
}
