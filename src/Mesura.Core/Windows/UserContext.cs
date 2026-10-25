namespace Mesura.Core.Windows;

/// <summary>
/// Whose per-user settings an operation changes. Per-user values (catalog hive <c>HKCU</c>) always go to the user
/// signed in to this Windows session, addressed as <c>HKEY_USERS\&lt;SID&gt;</c>, never to whichever account the
/// process happens to run as. When the two differ (a standard user elevated with another administrator's password),
/// the signed-in user's own hive is still targeted; when that user can't be established, per-user operations are
/// blocked.
/// </summary>
/// <param name="Sid">The target user's SID, or <c>null</c> when it couldn't be established.</param>
/// <param name="Account">The target account for display (<c>DOMAIN\user</c>), or empty.</param>
/// <param name="Reason">Why there is no target, when <see cref="Sid"/> is <c>null</c>.</param>
public sealed record UserContext(string? Sid, string Account, string Reason)
{
    public bool Resolved => Sid is not null;

    /// <summary>
    /// Decides the per-user target from what the platform read: the process's user, the session's signed-in user, and
    /// whether that user's registry hive is loaded (it is while they are signed in).
    /// </summary>
    public static UserContext Resolve(string? processSid, string? sessionSid, string sessionAccount, bool sessionHiveLoaded)
    {
        if (string.IsNullOrWhiteSpace(sessionSid))
        {
            return new UserContext(null, "", "Couldn't tell which user is signed in to this Windows session (for example a service or remote shell).");
        }

        if (string.Equals(processSid, sessionSid, StringComparison.OrdinalIgnoreCase) || sessionHiveLoaded)
        {
            return new UserContext(sessionSid, sessionAccount, "");
        }

        return new UserContext(null, sessionAccount, $"The settings of {sessionAccount}, the signed-in user, aren't reachable from this account.");
    }
}
