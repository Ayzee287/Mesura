using Mesura.Core.Support;
using Mesura.Setup.Windows;

namespace Mesura.App;

/// <summary>
/// What happens about administrator rights when Mesura starts. Since 0.7 the default is to ask Windows straight away (<see cref="StartupPolicy"/> decides, and
/// is tested); the choice dialog is only for a user who set "ask" in Settings. In a folder only an administrator can change (Program Files) the restart is
/// offered; in a folder any program can change (a per-user install) it never is (docs/security.md, decided 2026-10-30: no override), and says once how to
/// get full access instead.
/// </summary>
public static class StartupChoice
{
    /// <summary>What this start should do about administrator rights (<see cref="StartupPolicy.Decide"/> with this process's facts).</summary>
    public static StartupAction Decide(AppSettings settings, bool alreadyElevated, bool relaunched, bool noElevateArgument, bool trayStart) =>
        StartupPolicy.Decide(settings.StartupMode, settings.ElevateAtStart, ShellLauncher.ElevationBlockedReason is null, alreadyElevated, relaunched, noElevateArgument, trayStart);

    /// <summary>Whether this start should restart elevated. May show a dialog (before the main window exists) and save a remembered answer.</summary>
    public static bool WantsAdministrator(StartupAction action, AppSettings settings, SetupStore store)
    {
        if (action == StartupAction.Elevate)
        {
            return true;
        }

        if (action == StartupAction.ExplainSharedFolder && ShellLauncher.ElevationBlockedReason is { } why)
        {
            var seen = ConfirmDialog.Choose(Loc.Get("Start.SharedTitle"), Loc.Get("Start.SharedMessage"), Loc.Get("Common.Continue"), Loc.Get("Start.HowToGet"),
                Loc.Get("Start.RememberShared"), out var remembered);
            if (seen == false)
            {
                ConfirmDialog.Tell(Loc.Get("Start.HowToGet"), Loc.Format("Admin.PerUserHow", AppContext.BaseDirectory, why));
            }

            Remember(settings, store, seen, remembered, "normal");
            return false;
        }

        if (action != StartupAction.Ask)
        {
            return false;
        }

        var choice = ConfirmDialog.Choose(Loc.Get("Start.Title"), Loc.Get("Start.Message"), Loc.Get("Start.Restart"), Loc.Get("Start.Continue"), Loc.Get("Start.Remember"), out var keep);
        if (choice is not null)
        {
            Remember(settings, store, choice, keep, choice == true ? "admin" : "normal");
        }

        return choice == true;
    }

    private static void Remember(AppSettings settings, SetupStore store, bool? choice, bool remembered, string mode)
    {
        if (!remembered || choice is null)
        {
            return;
        }

        settings.StartupMode = mode;
        settings.ElevateAtStart = mode != "normal";
        settings.Save(store);
    }
}
