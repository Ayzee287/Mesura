using Mesura.Setup.Windows;

namespace Mesura.App;

/// <summary>
/// What happens about administrator rights when Mesura starts (0.4): a choice, remembered if the user says so. In a folder only an administrator
/// can change (Program Files) it offers to restart elevated; in a folder any program can change (a per-user install) it never does (docs/security.md,
/// decided 2026-10-30: no override), and says how to get full access instead.
/// </summary>
public static class StartupChoice
{
    /// <summary>Whether this start should restart elevated. May show the choice dialog (before the main window exists) and save a remembered answer.</summary>
    public static bool WantsAdministrator(AppSettings settings, SetupStore store)
    {
        var mode = settings.EffectiveStartupMode;
        if (ShellLauncher.ElevationBlockedReason is { } why)
        {
            if (mode == "ask")
            {
                var seen = ConfirmDialog.Choose(Loc.Get("Start.SharedTitle"), Loc.Get("Start.SharedMessage"), Loc.Get("Common.Continue"), Loc.Get("Start.HowToGet"),
                    Loc.Get("Start.RememberShared"), out var remembered);
                if (seen == false)
                {
                    ConfirmDialog.Tell(Loc.Get("Start.HowToGet"), Loc.Format("Admin.PerUserHow", AppContext.BaseDirectory, why));
                }

                Remember(settings, store, seen, remembered, "normal");
            }

            return false;
        }

        switch (mode)
        {
            case "admin":
                return true;
            case "normal":
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
