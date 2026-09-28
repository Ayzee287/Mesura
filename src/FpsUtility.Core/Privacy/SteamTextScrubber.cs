using System.Text.RegularExpressions;

namespace FpsUtility.Core.Privacy;

/// <summary>
/// Scrubs Steam library file text before it is kept (architecture §4.4 <c>steam</c>, §13): the
/// user's profile path becomes <c>%USERPROFILE%</c>, and the values of <c>LastOwner</c> (the Steam
/// account that installed the app) and <c>LastPlayed</c> (when it was last played) become
/// <c>redacted</c>. No check needs either value; the file is otherwise kept as written.
/// </summary>
public sealed partial class SteamTextScrubber
{
    public const string Redacted = "redacted";

    private readonly UserProfileRedactor _profile;

    public SteamTextScrubber(UserProfileRedactor profile)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    public string Scrub(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return _profile.Redact(PersonalValue().Replace(text, $"${{key}}{Redacted}\""));
    }

    /// <summary>A quoted VDF key-value pair whose key is personal; VDF keys are case-insensitive.</summary>
    [GeneratedRegex(@"(?<key>""(?:LastOwner|LastPlayed)""[ \t]+"")[^""\r\n]*""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PersonalValue();
}
