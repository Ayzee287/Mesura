using Mesura.Core.Apps;
using Mesura.Core.Windows;

namespace Mesura.Core.Setup;

/// <summary>How a run went as a whole, beyond <see cref="SetupRun.Ok"/>.</summary>
public enum RunStatus
{
    /// <summary>Every step ended well and nothing waits on the user.</summary>
    Succeeded,

    /// <summary>Nothing failed, but some steps wait on the user (start an app once, sign in, elevate) and a re-run does them.</summary>
    Incomplete,

    /// <summary>Something failed and needs looking at.</summary>
    NeedsAttention,
}

/// <summary>
/// One word for each step's outcome, the same in the app (where it picks the colour) and the CLI, so that "the app hasn't
/// created its settings file yet", "a restart is needed", "install it by hand", "not on this PC" and "failed" never read
/// alike.
/// </summary>
public static class StepWords
{
    public static string Install(InstallStatus status) => status switch
    {
        InstallStatus.Succeeded => "installed",
        InstallStatus.SucceededRebootRequired => "restart needed",
        InstallStatus.AlreadyInstalled => "already done",
        InstallStatus.Manual => "manual",
        InstallStatus.Cancelled => "cancelled",
        InstallStatus.WingetUnavailable => "no winget",
        _ => "failed",
    };

    /// <summary>
    /// An import's outcome: an app that hasn't created its settings file yet (never started on this PC) or whose account
    /// hasn't signed in is "waiting", not "failed" and not "already done"; a file that exists but can't be read is "failed".
    /// </summary>
    public static string Config(ConfigImportOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var waiting = outcome.DestinationMissing.Count > 0 || outcome.WaitingOnScope.Count > 0;
        return outcome.Status switch
        {
            ConfigImportStatus.Applied when waiting => "partly applied",
            ConfigImportStatus.Applied => "applied",
            ConfigImportStatus.Unchanged when waiting => "waiting",
            ConfigImportStatus.Unchanged => "already done",
            ConfigImportStatus.SkippedAppRunning => "waiting",
            ConfigImportStatus.ChangedSince => "left as is",
            _ => "failed",
        };
    }

    public static string Windows(WindowsOpStatus status) => status switch
    {
        WindowsOpStatus.Applied => "applied",
        WindowsOpStatus.AlreadyApplied => "already done",
        WindowsOpStatus.Inapplicable => "not applicable",
        WindowsOpStatus.NeedsElevation => "needs admin",
        WindowsOpStatus.Blocked => "blocked",
        WindowsOpStatus.Manual => "manual",
        WindowsOpStatus.Observation => "guided",
        WindowsOpStatus.Reverted => "reverted",
        WindowsOpStatus.RevertSkipped => "left as is",
        WindowsOpStatus.Unknown => "unknown",
        _ => "failed",
    };

    public static string Run(RunStatus status) => status switch
    {
        RunStatus.Succeeded => "succeeded",
        RunStatus.Incomplete => "incomplete",
        _ => "needs attention",
    };
}
