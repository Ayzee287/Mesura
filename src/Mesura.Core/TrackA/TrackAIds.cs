using System.Globalization;
using System.Text.RegularExpressions;

namespace Mesura.Core.TrackA;

/// <summary>
/// A Track A cohort PC's id, <c>PC-NNN</c> (architecture §4.7): assigned by the owner from the enrolment list, never
/// derived from the machine. No serial, GUID, MAC, LUID or PnP id can produce or influence it. Track B ids
/// (<c>PCB-NNN</c>) are not Track A's.
/// </summary>
public static partial class CohortId
{
    public static bool IsValid(string? value) => value is not null && Pattern().IsMatch(value);

    /// <exception cref="ArgumentException">The value isn't <c>PC-NNN</c>.</exception>
    public static string Require(string? value, string what) =>
        IsValid(value) ? value! : throw new ArgumentException($"{what} '{value}' is not a Track A cohort id (PC-NNN, e.g. PC-001).", what);

    [GeneratedRegex("^PC-[0-9]{3}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

/// <summary>
/// One capture of a cohort PC: <c>session-yyyyMMddTHHmmssZ</c>, the UTC time the session started. It is made from the
/// clock alone, so it says when, never which machine: the cohort id says which PC, and one PC has many sessions.
/// </summary>
public static partial class SessionId
{
    public static string At(DateTimeOffset startedAt)
    {
        if (startedAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("A session id is made from UTC time.", nameof(startedAt));
        }

        return "session-" + startedAt.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
    }

    public static bool IsValid(string? value) =>
        value is not null && Pattern().IsMatch(value)
        && DateTime.TryParseExact(value["session-".Length..], "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out _);

    /// <exception cref="ArgumentException">The value isn't a session id.</exception>
    public static string Require(string? value, string what) =>
        IsValid(value) ? value! : throw new ArgumentException($"{what} '{value}' is not a session id (session-yyyyMMddTHHmmssZ).", what);

    [GeneratedRegex("^session-[0-9]{8}T[0-9]{6}Z\\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
