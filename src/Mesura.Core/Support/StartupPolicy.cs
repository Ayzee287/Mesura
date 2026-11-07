namespace Mesura.Core.Support;

/// <summary>What the window does about administrator rights at the moment it starts (or the tray's window first opens).</summary>
public enum StartupAction
{
    /// <summary>Carry on as this process is: no prompt of any kind.</summary>
    Continue,

    /// <summary>Restart elevated now: Windows' own prompt is the only question.</summary>
    Elevate,

    /// <summary>The user asked to be asked each time (Settings): show the choice, then restart elevated or carry on.</summary>
    Ask,

    /// <summary>A copy in a folder any program can change never elevates (docs/security.md). Say so once instead of silently running without administrator rights.</summary>
    ExplainSharedFolder,
}

/// <summary>
/// The start-up decision about administrator rights, apart from any window or process so that it can be tested (0.7, D049).
/// <para>
/// Since 0.7 the default is that Mesura asks Windows for administrator rights straight away when it starts: no choice dialog first, one
/// Windows prompt, and a declined prompt leaves Mesura running unelevated (it is never asked again in that run). What was chosen
/// on purpose stays: <c>normal</c> (never ask) and <c>ask</c> (the older dialog) saved in Settings or by "Remember my choice".
/// </para>
/// <para>
/// The decision is made once per window start, and each guard exists to stop a loop or a second prompt: an elevation restart
/// (<c>--relaunched</c>) never restarts again, a process that is already elevated never asks, <c>--no-elevate</c> (scripts, tests, the
/// CLI's own launches) never asks, the tray started at sign-in never asks (its window does, once, when it first opens), and a folder
/// any program can change never elevates.
/// </para>
/// </summary>
public static class StartupPolicy
{
    public const string Ask = "ask";
    public const string Normal = "normal";
    public const string Admin = "admin";

    /// <summary>
    /// The mode in effect. A saved mode wins; with none saved, the 0.1-0.3 switch decides: on (the default, also for every user who never
    /// touched it) is <c>admin</c>, off (turned off on purpose) is <c>normal</c>.
    /// </summary>
    public static string EffectiveMode(string? savedMode, bool legacyElevateAtStart) =>
        savedMode is Ask or Normal or Admin ? savedMode : legacyElevateAtStart ? Admin : Normal;

    /// <param name="savedMode">What Settings holds (<c>null</c> when nothing was ever chosen).</param>
    /// <param name="legacyElevateAtStart">The older on/off switch, used only when no mode is saved.</param>
    /// <param name="folderProtected">The program folder can be changed by administrators only (Program Files). False for a per-user copy and for a folder anyone can write to.</param>
    /// <param name="alreadyElevated">This process already has administrator rights.</param>
    /// <param name="relaunched">This process is the elevated copy an elevation restart started (<c>--relaunched</c>).</param>
    /// <param name="noElevateArgument">The launch said <c>--no-elevate</c>.</param>
    /// <param name="trayStart">Started with <c>--tray</c> at sign-in: no window yet, so nothing to ask.</param>
    public static StartupAction Decide(string? savedMode, bool legacyElevateAtStart, bool folderProtected, bool alreadyElevated, bool relaunched, bool noElevateArgument, bool trayStart)
    {
        if (trayStart || relaunched || noElevateArgument || alreadyElevated)
        {
            return StartupAction.Continue;
        }

        var mode = EffectiveMode(savedMode, legacyElevateAtStart);
        if (!folderProtected)
        {
            // A default (nothing saved) that cannot be honoured here is explained once, as the old "ask" was; a saved "normal" is respected silently.
            return mode == Ask || (mode == Admin && savedMode is null) ? StartupAction.ExplainSharedFolder : StartupAction.Continue;
        }

        return mode switch
        {
            Admin => StartupAction.Elevate,
            Normal => StartupAction.Continue,
            _ => StartupAction.Ask,
        };
    }
}
