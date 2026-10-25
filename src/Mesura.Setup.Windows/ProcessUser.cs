using System.Security.Principal;
using Mesura.Core.Windows;

namespace Mesura.Setup.Windows;

/// <summary>
/// Whether this process runs as the user signed in to the session, elevated or not. App settings (and Mesura's own
/// preferences and history) live in the process account's profile, so the app and the CLI read and write them only
/// when it does; a standard user elevated with another administrator's password must never get that account's app
/// settings, or have theirs written there.
/// </summary>
public static class ProcessUser
{
    public static bool Is(UserContext user)
    {
        ArgumentNullException.ThrowIfNull(user);
        using var identity = WindowsIdentity.GetCurrent();
        return user.Resolved && string.Equals(identity.User?.Value, user.Sid, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The same, for a process that may have no signed-in user at all (a remote shell, a scheduled task): unelevated,
    /// its own account is the only one involved, so that counts as the user's; elevated, it can't be told, so it doesn't.
    /// </summary>
    public static bool Is(UserContext user, bool elevated)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.Resolved ? Is(user) : !elevated;
    }

    /// <summary>This process's account, for messages (DOMAIN\name).</summary>
    public static string Account
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.Name;
        }
    }
}
