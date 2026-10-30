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
