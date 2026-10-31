namespace Mesura.Core.Apps;

/// <summary>Which winget source a package comes from.</summary>
public enum AppSource
{
    /// <summary>The community <c>winget</c> repository.</summary>
    Winget,

    /// <summary>The Microsoft Store (<c>msstore</c>) source.</summary>
    MsStore,

    /// <summary>Not installable through winget; the user follows <see cref="AppPackage.Url"/> by hand.</summary>
    Manual,
}

/// <summary>Whose context an application's installer must run in.</summary>
public enum AppRunContext
{
    /// <summary>Any: an elevated Mesura runs it elevated (no second prompt) when it runs as the signed-in user.</summary>
    Any,

    /// <summary>
    /// The signed-in user, unelevated: a per-user installer (winget manifest <c>Scope: user</c>) or one that refuses to
    /// run elevated (<c>ElevationRequirement: elevationProhibited</c>), which an elevated process would otherwise run
    /// as administrator or put into the wrong profile.
    /// </summary>
    SignedInUser,
}

/// <summary>Whether an application is installed, as detected from <c>winget list</c>.</summary>
public enum InstallState
{
    Installed,
    NotInstalled,

    /// <summary>Detection could not be run or its output was not understood; never treated as either state.</summary>
    Unknown,
}

/// <summary>
/// One installable application in the catalog. The <see cref="WingetId"/> values are real, verified ids; nothing is
/// downloaded or run from this record — it is data the install service acts on.
/// </summary>
/// <param name="Id">Stable short id used by bundles and the CLI, e.g. <c>brave</c>.</param>
/// <param name="WingetId">The exact winget package id, e.g. <c>Brave.Brave</c>.</param>
/// <param name="Name">Human name.</param>
/// <param name="Category">Grouping for display, e.g. <c>Browser</c>, <c>Gaming</c>, <c>Media</c>.</param>
/// <param name="Source">Which winget source to install from.</param>
/// <param name="Note">Any caveat (e.g. a related config port, or a manual follow-up).</param>
/// <param name="Url">For a <see cref="AppSource.Manual"/> entry, where the user gets it.</param>
/// <param name="Detectable">
/// False when winget cannot see the package once installed (e.g. an installer that patches another app), so its state
/// is reported as <see cref="InstallState.Unknown"/> rather than a misleading "not installed", unless a
/// <paramref name="Detector"/> reads it another way.
/// </param>
/// <param name="Listed">
/// False for an entry kept only so saved profiles and the CLI still resolve it; the app doesn't list it among the apps
/// to install (NVCleanstall is offered in the NVIDIA section instead).
/// </param>
/// <param name="Context">Whose context the installer runs in (<see cref="AppRunContext"/>).</param>
/// <param name="Detector">
/// The name of a detector that reads the install state from the app's own files when winget can't (Vencord's patch of
/// Discord), or empty. <see cref="AppInstallService"/> is given the detectors by name.
/// </param>
/// <param name="Customizes">
/// The app this package modifies (Vencord patches Discord), or empty. Apps offers it on that app's card rather than as an
/// app of its own (D031).
/// </param>
/// <param name="InstallLocation">
/// Whether this package's installer takes a folder (winget's <c>--location</c>): true only where the installer's type or manifest
/// provides the switch (NSIS, Inno Setup, a manifest-declared option, a portable package). False for every installer that doesn't
/// (Squirrel, most vendor exes, Chrome's MSI): choosing a folder for those would be ignored, so it is not offered.
/// </param>
/// <param name="InstallFolder">The folder the user chose for this install (set for one run, never in the catalog); <c>null</c> means the installer's own default.</param>
/// <param name="Upgradable">
/// Whether Mesura offers to update this app through winget. True only for an app whose winget manifest updates it in place
/// reliably and which has no updater of its own to fight with; false for apps that update themselves (browsers, chat clients,
/// game stores), install per user through a stub, or ship as a portable file. Updating is never offered for those: a button that
/// might do nothing, or run a second updater next to the app's own, is worse than none.
/// </param>
/// <param name="Uninstallable">
/// Whether Mesura offers to uninstall this app through winget. True only for an app whose uninstall was run for real, elevated, and
/// checked afterwards (an MSI package, or a vendor installer whose uninstaller winget drives correctly); false for everything else, including
/// apps winget reports as uninstalled while they remain (Firefox, seen 2026-10-30), apps with their own uninstaller flow (Steam, Discord) and
/// anything whose uninstall would touch more than the app (Vencord patches Discord). Uninstalling needs Mesura to run as administrator: an MSI
/// uninstall run without it fails (winget exit 0x8A150030, installer code 1603), so it is never offered without.
/// </param>
public sealed record AppPackage(
    string Id,
    string WingetId,
    string Name,
    string Category,
    AppSource Source,
    string Note = "",
    string Url = "",
    bool Detectable = true,
    bool Listed = true,
    AppRunContext Context = AppRunContext.Any,
    string Detector = "",
    string Customizes = "",
    bool InstallLocation = false,
    string? InstallFolder = null,
    bool Upgradable = false,
    bool Uninstallable = false)
{
    /// <summary>The <c>--source</c> argument for this package.</summary>
    public string SourceArgument => Source == AppSource.MsStore ? "msstore" : "winget";
}

/// <summary>A named group of applications a user installs together (e.g. a fresh-PC set).</summary>
public sealed record AppBundle(string Id, string Name, string Description, IReadOnlyList<string> AppIds);

/// <summary>One line of an install plan: a package and whether it is already present.</summary>
public sealed record AppPlanItem(AppPackage Package, InstallState State);

/// <summary>Whether winget offers a newer version of an installed application.</summary>
public enum UpdateState
{
    /// <summary>winget has a newer version (the versions are in <see cref="UpdateCheck"/> when they could be read).</summary>
    Available,

    /// <summary>winget was asked and offers nothing newer (or doesn't know the app).</summary>
    UpToDate,

    /// <summary>The question couldn't be answered (winget missing, refused, a source error); never read as up to date.</summary>
    Unknown,
}

/// <summary>The answer to "is there an update for this app": the state and, when winget printed them, the installed and available versions.</summary>
public sealed record UpdateCheck(AppPackage Package, UpdateState State, string? InstalledVersion = null, string? AvailableVersion = null);

/// <summary>What happened to one application during a batch install.</summary>
public enum InstallStatus
{
    Succeeded,

    /// <summary>Installed; Windows must restart before it is usable.</summary>
    SucceededRebootRequired,

    AlreadyInstalled,

    /// <summary>A manual entry: nothing was run; the user installs it from <see cref="AppPackage.Url"/>.</summary>
    Manual,

    /// <summary>winget returned a non-zero exit code; <see cref="InstallOutcome.Detail"/> carries it.</summary>
    Failed,

    /// <summary>Cancelled by the caller before or during this item.</summary>
    Cancelled,

    /// <summary>winget itself is unavailable, so nothing was attempted.</summary>
    WingetUnavailable,
}

/// <summary>The result of attempting one application.</summary>
public sealed record InstallOutcome(string AppId, string WingetId, InstallStatus Status, string Detail)
{
    public bool Ok => Status is InstallStatus.Succeeded or InstallStatus.SucceededRebootRequired or InstallStatus.AlreadyInstalled;
}
