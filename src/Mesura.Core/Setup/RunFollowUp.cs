using Mesura.Core.Apps;
using Mesura.Core.Windows;

namespace Mesura.Core.Setup;

/// <summary>Who has to act on a follow-up.</summary>
public enum FollowUpKind
{
    /// <summary>Something failed and needs looking at.</summary>
    Problem,

    /// <summary>The run did all it could; the user does one thing and runs it again (start an app, sign in, elevate).</summary>
    RunAgain,

    /// <summary>Done, but it takes effect only after the user restarts, signs out or restarts Explorer.</summary>
    Finish,

    /// <summary>A step Mesura doesn't do (an app installed by hand).</summary>
    ByHand,
}

/// <summary>One thing left to do after a run, in words for the user.</summary>
public sealed record FollowUp(FollowUpKind Kind, string Text);

/// <summary>
/// What is left after an install, an import, a Windows apply or a whole profile run: what failed, what waits on the user
/// (start an app once, close it, sign in to Steam, restart Mesura as administrator) before running it again, and what
/// takes effect only after a restart or sign-out. One function, so the app and the CLI say the same thing.
/// </summary>
public static class RunFollowUp
{
    /// <param name="appName">The display name for an app id (the record keeps ids only).</param>
    public static IReadOnlyList<FollowUp> For(SetupRun run, Func<string, string> appName)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(appName);
        var items = new List<FollowUp>();

        foreach (var app in run.Apps)
        {
            var name = appName(app.AppId);
            switch (app.Status)
            {
                case InstallStatus.Failed:
                    items.Add(new(FollowUpKind.Problem, $"{name} wasn't installed: {app.Detail}"));
                    break;
                case InstallStatus.WingetUnavailable:
                    items.Add(new(FollowUpKind.RunAgain, $"{name}: winget isn't available. Install App Installer, then run again."));
                    break;
                case InstallStatus.Cancelled:
                    items.Add(new(FollowUpKind.RunAgain, $"{name} wasn't installed: the run was cancelled. Run again to install it."));
                    break;
                case InstallStatus.Manual:
                    items.Add(new(FollowUpKind.ByHand, $"{name}: {app.Detail}"));
                    break;
                case InstallStatus.SucceededRebootRequired:
                    items.Add(new(FollowUpKind.Finish, $"Restart Windows to finish installing {name}."));
                    break;
            }
        }

        foreach (var config in run.Config)
        {
            switch (config.Status)
            {
                case ConfigImportStatus.SkippedAppRunning:
                    items.Add(new(FollowUpKind.RunAgain, $"{config.AppName} was running, so its settings weren't written. Close it, then run again."));
                    break;
                case ConfigImportStatus.Failed:
                    items.Add(new(FollowUpKind.Problem, $"{config.AppName}'s settings couldn't be imported: {config.Detail}"));
                    break;
                case ConfigImportStatus.ChangedSince:
                    items.Add(new(FollowUpKind.ByHand,
                        $"{config.AppName}'s settings file changed after that run (or the run is too old to check), so it was left as it is; the earlier copy is in the run's backups."));
                    break;
            }

            if (config.DestinationMissing.Count > 0)
            {
                items.Add(new(FollowUpKind.RunAgain, $"{config.AppName} hasn't created its settings file yet. Start it once and close it, then run again."));
            }

            if (config.WaitingOnScope.Count > 0)
            {
                items.Add(new(FollowUpKind.RunAgain, $"{config.AppName}: some settings wait for an account that hasn't signed in on this PC yet. Sign in once, then run again."));
            }
        }

        var elevation = run.WindowsSteps.Where(w => w.Status == WindowsOpStatus.NeedsElevation).ToList();
        if (elevation.Count > 0)
        {
            items.Add(new(FollowUpKind.RunAgain,
                $"{elevation.Count} Windows setting(s) change the whole PC and need administrator rights ({Names(elevation)}). Run again as administrator."));
        }

        foreach (var step in run.WindowsSteps.Where(w => w.Status is WindowsOpStatus.Failed or WindowsOpStatus.Blocked or WindowsOpStatus.Unknown))
        {
            items.Add(new(FollowUpKind.Problem, $"{step.Name}: {step.Detail}"));
        }

        foreach (var step in run.SetupSteps)
        {
            switch (step.State)
            {
                case StepState.Failed:
                    items.Add(new(FollowUpKind.Problem, $"{SetupStepName(step.Kind)}: {step.Detail}"));
                    break;
                case StepState.Waiting or StepState.Pending:
                    items.Add(new(FollowUpKind.RunAgain, $"{SetupStepName(step.Kind)} isn't finished yet. Continue the setup in Mesura."));
                    break;
            }
        }

        var applied = run.WindowsSteps.Where(w => w.Status is WindowsOpStatus.Applied or WindowsOpStatus.Reverted).ToList();
        AddFinish(items, applied, Activation.Reboot, "Restart Windows to finish");
        AddFinish(items, applied, Activation.SignOut, "Sign out and back in to finish");
        AddFinish(items, applied, Activation.ExplorerRestart, "Restart Explorer (or sign out) to see");
        return items;
    }

    /// <summary>How the run went as a whole: a failure needs attention; otherwise anything to run again makes it incomplete.</summary>
    public static RunStatus Status(SetupRun run)
    {
        var items = For(run, id => id);
        return items.Any(i => i.Kind == FollowUpKind.Problem) ? RunStatus.NeedsAttention
            : items.Any(i => i.Kind == FollowUpKind.RunAgain) ? RunStatus.Incomplete
            : RunStatus.Succeeded;
    }

    /// <summary>A setup step's name in English, for the CLI and the log.</summary>
    public static string SetupStepName(StepKind kind) => kind switch
    {
        StepKind.Runtimes => "Visual C++ runtimes",
        StepKind.Apps => "Apps",
        StepKind.Windows => "Windows settings",
        StepKind.NvidiaBackup => "NVIDIA profile backup",
        StepKind.NvidiaDriver => "NVIDIA driver",
        StepKind.NvidiaRestore => "NVIDIA profile restore",
        _ => "NVIDIA profile import",
    };

    private static void AddFinish(List<FollowUp> items, List<WindowsOpOutcome> applied, Activation activation, string lead)
    {
        var these = applied.Where(w => w.Activation == activation).ToList();
        if (these.Count > 0)
        {
            items.Add(new(FollowUpKind.Finish, $"{lead}: {Names(these)}."));
        }
    }

    private static string Names(IReadOnlyCollection<WindowsOpOutcome> steps) =>
        steps.Count <= 3 ? string.Join(", ", steps.Select(s => s.Name)) : $"{string.Join(", ", steps.Take(2).Select(s => s.Name))} and {steps.Count - 2} more";
}
