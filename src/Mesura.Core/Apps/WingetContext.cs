namespace Mesura.Core.Apps;

/// <summary>How winget is started for one command.</summary>
public enum WingetLaunch
{
    /// <summary>In Mesura's own process context (elevated when Mesura is).</summary>
    Direct,

    /// <summary>As the user signed in to this Windows session, unelevated, with that user's environment.</summary>
    AsSignedInUser,

    /// <summary>Not at all: the user it would run as can't be established, so nothing is started.</summary>
    Refused,
}

/// <summary>
/// Decides whose context winget runs in (architecture §4.8, redesign 2026-10 §7). Mesura normally runs elevated, and an
/// elevated process can belong to another account than the signed-in user (a standard user who typed an
/// administrator's password). winget in that context would put per-user apps into the administrator's profile, and
/// installers that refuse elevation (Spotify) fail. So: per-user installers always run as the signed-in user,
/// unelevated; everything runs as the signed-in user when Mesura's account isn't theirs; and when the signed-in user's
/// context isn't reachable, nothing runs.
/// </summary>
public static class WingetContext
{
    /// <param name="userKnown">Whether the user signed in to this session could be established.</param>
    /// <param name="sameUser">Whether Mesura runs as that user (elevated or not).</param>
    /// <param name="elevated">Whether Mesura runs elevated.</param>
    /// <param name="context">The package's context; detection and probes use <see cref="AppRunContext.Any"/>.</param>
    /// <param name="signedInUserReachable">
    /// Whether a process can be started as the signed-in user (the desktop shell runs as them, and Mesura holds the
    /// privilege to start a process with their token, which an elevated administrator does).
    /// </param>
    /// <returns>How to launch, and why it is refused when it is.</returns>
    public static (WingetLaunch Launch, string Reason) Decide(
        bool userKnown, bool sameUser, bool elevated, AppRunContext context, bool signedInUserReachable)
    {
        if (!userKnown)
        {
            // Without a signed-in user (a service or remote shell) the only user is the one running Mesura: fine
            // unelevated, but an elevated process can't tell whose profile an install would land in.
            return elevated
                ? (WingetLaunch.Refused, "Mesura couldn't tell which user is signed in, so it doesn't install apps from an administrator process.")
                : (WingetLaunch.Direct, "");
        }

        if (sameUser && (!elevated || context == AppRunContext.Any))
        {
            return (WingetLaunch.Direct, "");
        }

        return signedInUserReachable
            ? (WingetLaunch.AsSignedInUser, "")
            : (WingetLaunch.Refused, sameUser
                ? "This app installs for the signed-in user only, and Mesura couldn't start winget as you without administrator rights (the Windows shell isn't reachable). Start Mesura without elevation to install it."
                : "Mesura runs as another account than the signed-in user and couldn't start winget as the signed-in user, so it installs nothing rather than install into the wrong profile.");
    }
}
