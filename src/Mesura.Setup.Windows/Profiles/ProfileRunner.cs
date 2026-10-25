using Mesura.Core.Apps;
using Mesura.Core.Config;
using Mesura.Core.Setup;
using Mesura.Core.Windows;
using Mesura.Setup.Windows.Config;

namespace Mesura.Setup.Windows.Profiles;

/// <summary>
/// What a profile resolves to on this machine, before anything runs: the app plan, the config bundle and the Windows
/// operations.
/// </summary>
/// <param name="BundleError">Why the profile's config bundle could not be read, if it couldn't; the apps still run.</param>
public sealed record ProfilePlan(
    SetupProfile Profile,
    IReadOnlyList<AppPlanItem> Apps,
    ConfigBundleFile? Bundle,
    IReadOnlyList<ConfigPortDefinition> Ports,
    string? BundleError,
    IReadOnlyList<WindowsOperation> Windows)
{
    /// <summary>
    /// Apps Mesura can't detect (Vencord) that an earlier Mesura run on this PC installed: their installers aren't run
    /// again on every re-run; the Apps tab runs one again on request.
    /// </summary>
    public IReadOnlySet<string> InstalledEarlier { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>winget isn't available, so no app state could be read and nothing can be installed.</summary>
    public bool WingetMissing => Apps.Where(a => a.Package.Detectable && a.Package.Source != AppSource.Manual).ToList() is { Count: > 0 } detectable
        && detectable.All(a => a.State == InstallState.Unknown);

    /// <summary>The words for an app the plan won't install because an earlier run did.</summary>
    public const string EarlierDetail =
        "Installed by an earlier Mesura run on this PC; Mesura can't detect it, so its installer isn't run again (the Apps tab runs it again on request).";
}

/// <summary>A profile read from this machine, the bundle to save beside it (or none), and what that bundle holds.</summary>
public sealed record ProfileCapture(SetupProfile Profile, string? BundleJson, IReadOnlyList<AppExport> Settings);

/// <summary>
/// Runs a setup profile with the existing operations, in order: install its apps (<see cref="AppInstallService"/>),
/// import its config bundle (<see cref="ConfigService"/>), apply its Windows operations
/// (<see cref="WindowsConfigurator"/>), then record the run in the history. It adds no logic of its own beyond that
/// order; each step reports exactly what its operation reported.
/// </summary>
public static class ProfileRunner
{
    /// <summary>
    /// Reads this machine into a profile (read-only): which catalog apps are installed, which Windows operations are
    /// applied, and an export of the supported apps' settings. Nothing is written; the caller saves the profile and,
    /// when there is one, the bundle beside it under <paramref name="bundleFileName"/>.
    /// </summary>
    /// <param name="outcome">
    /// The preset the user chose on Optimize, if any: recorded as the profile's Windows preset when it is still in
    /// place here (<see cref="SetupProfile.PresetSatisfied"/>), so the profile carries the intended outcome rather than
    /// only the settings this PC happened to apply.
    /// </param>
    /// <param name="includeSettings">
    /// Whether to export the apps' settings. The app passes false when it runs as another account than the signed-in
    /// user: the settings it could read would be that account's, not the user's.
    /// </param>
    /// <param name="nvidia">The NVIDIA driver configuration to carry (D032); the default configuration when none is given.</param>
    public static ProfileCapture Capture(
        string name, string bundleFileName, AppCatalog catalog, AppInstallService installer,
        WindowsCatalog windowsCatalog, ISystemReader reader, string createdUtc, string? outcome = null, bool includeSettings = true,
        Mesura.Core.Nvidia.NvidiaDriverConfig? nvidia = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(installer);
        ArgumentNullException.ThrowIfNull(windowsCatalog);
        ArgumentNullException.ThrowIfNull(reader);

        var apps = installer.BuildPlan(catalog.Packages);
        var windows = windowsCatalog.Operations.Select(op => WindowsEvaluator.Evaluate(op, reader)).ToList();
        var export = ConfigService.Export(includeSettings ? ConfigCatalog.All : [], createdUtc);
        var settings = export.Apps.Where(a => a.Bundle.Values.Count > 0).ToList();
        var date = createdUtc.Length >= 10 ? createdUtc[..10] : createdUtc;
        var chosen = outcome is null ? null : windowsCatalog.PresetById(outcome);
        var profile = SetupProfile.FromMachine(name, $"Saved from a PC by Mesura on {date}.", apps, windows,
            settings.Count > 0 ? bundleFileName : null,
            chosen is not null && SetupProfile.PresetSatisfied(chosen, windows) ? [chosen] : null,
            Mesura.Core.Nvidia.NvidiaDriverConfig.Normalize(nvidia));
        return new ProfileCapture(profile, settings.Count > 0 ? export.BundleJson : null, settings);
    }

    /// <param name="history">This PC's history, to find undetectable apps an earlier run installed (null: none known).</param>
    public static ProfilePlan Plan(
        SetupProfile profile, string profileFolder, AppCatalog catalog, AppInstallService installer, WindowsCatalog? windowsCatalog = null,
        SetupStore? history = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(installer);

        var apps = installer.BuildPlan(profile.ResolveApps(catalog));
        var windows = profile.ResolveWindows(windowsCatalog ?? WindowsCatalog.Default);
        var earlier = history is null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : InstalledEarlier(history, apps);
        if (profile.ConfigBundle is null)
        {
            return new ProfilePlan(profile, apps, null, [], null, windows) { InstalledEarlier = earlier };
        }

        var path = Path.GetFullPath(Path.Combine(profileFolder, profile.ConfigBundle));
        try
        {
            var bundle = ConfigBundleFile.Load(File.ReadAllText(path));
            var ports = profile.ResolveConfigPorts().Where(p => bundle.ForApp(p.Id) is not null).ToList();
            return new ProfilePlan(profile, apps, bundle, ports, null, windows) { InstalledEarlier = earlier };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            return new ProfilePlan(profile, apps, null, [], $"Could not read the config bundle '{profile.ConfigBundle}': {e.Message}", windows)
            {
                InstalledEarlier = earlier,
            };
        }
    }

    /// <summary>
    /// The undetectable apps among <paramref name="apps"/> that a recorded run on this PC (an install or a profile run)
    /// installed successfully. The history lives in this user's local app data, so a fresh install starts with none.
    /// </summary>
    private static HashSet<string> InstalledEarlier(SetupStore history, IReadOnlyList<AppPlanItem> apps)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Only an app whose state is really unknown: one with its own detector (Vencord) that reads "not installed" is
        // run again, since that is the truth (a Discord update removed the patch).
        var undetectable = apps.Where(a => a.State == InstallState.Unknown && !a.Package.Detectable && a.Package.Source != AppSource.Manual)
            .Select(a => a.Package.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (undetectable.Count == 0)
        {
            return found;
        }

        foreach (var id in history.ListRuns())
        {
            try
            {
                if (history.ReadRunRecord(id) is not { } json)
                {
                    continue;
                }

                found.UnionWith(SetupRun.FromJson(json).Apps
                    .Where(a => undetectable.Contains(a.AppId) && a.Status is InstallStatus.Succeeded or InstallStatus.SucceededRebootRequired)
                    .Select(a => a.AppId));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
            {
                // An unreadable record says nothing either way.
            }
        }

        return found;
    }

    /// <summary>What the config step would do now, without writing anything.</summary>
    /// <param name="locate">Where each app's config files are (tests pass temporary files); the real locations by default.</param>
    public static IReadOnlyList<ConfigImportOutcome> PreviewConfig(
        ProfilePlan plan, Func<ConfigPortDefinition, IReadOnlyList<LocatedConfigFile>>? locate = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Bundle is null ? [] : ConfigService.Import(plan.Bundle, plan.Ports, store: null, runId: null, locate);
    }

    /// <summary>
    /// Installs the plan's apps, imports its config, applies its Windows operations (when a configurator is given) and
    /// writes the run to the history. If the install is cancelled nothing after it starts. An app a fresh install has
    /// just started is skipped by the config step (its settings would be overwritten on exit) and is reported so;
    /// re-running the profile then applies it, since every step is idempotent. A re-run that finds everything already
    /// done changes nothing and isn't recorded (<see cref="SetupRun.ChangedNothing"/>), so History holds only runs that
    /// did something or still need something.
    /// </summary>
    /// <param name="locate">Where each app's config files are (tests pass temporary files); the real locations by default.</param>
    /// <param name="isRunning">Whether an app's process runs (tests decide); the real check by default.</param>
    /// <param name="record">
    /// Whether to write the run to the history here. The app passes false and records it itself, so a failed write is
    /// reported beside the run's outcome instead of losing it.
    /// </param>
    public static SetupRun Run(
        ProfilePlan plan,
        AppInstallService installer,
        SetupStore store,
        Func<DateTimeOffset> clock,
        Action<AppPackage, string>? onProgress,
        CancellationToken cancellationToken,
        WindowsConfigurator? windows = null,
        Func<ConfigPortDefinition, IReadOnlyList<LocatedConfigFile>>? locate = null,
        Func<string, bool>? isRunning = null,
        bool record = true)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(installer);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);

        var started = clock();
        var id = SetupRun.NewId(started, "profile");
        // An undetectable app an earlier run installed isn't run again; it is reported in its place, in plan order.
        var ran = installer.Install([.. plan.Apps.Where(a => !plan.InstalledEarlier.Contains(a.Package.Id))], skipInstalled: true, onProgress, cancellationToken)
            .ToDictionary(o => o.AppId, StringComparer.OrdinalIgnoreCase);
        var installs = plan.Apps.Select(a => ran.TryGetValue(a.Package.Id, out var outcome)
            ? outcome
            : new InstallOutcome(a.Package.Id, a.Package.WingetId, InstallStatus.AlreadyInstalled, ProfilePlan.EarlierDetail)).ToList();
        var config = plan.Bundle is null || cancellationToken.IsCancellationRequested
            ? []
            : ConfigService.Import(plan.Bundle, plan.Ports, store, id, locate, isRunning);
        var windowsSteps = windows is null || plan.Windows.Count == 0 || cancellationToken.IsCancellationRequested
            ? []
            : windows.Apply(plan.Windows, cancellationToken);

        var run = SetupRun.Create(id, "profile", plan.Profile.Name, SetupRun.Stamp(started), SetupRun.Stamp(clock()), installs, config, windowsSteps);
        if (record && !run.ChangedNothing)
        {
            store.WriteRunRecord(id, run.ToJson());
        }

        return run;
    }
}
