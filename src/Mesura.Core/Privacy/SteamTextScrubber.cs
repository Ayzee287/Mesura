using System.Text.RegularExpressions;

namespace Mesura.Core.Privacy;

/// <summary>
/// Scrubs Steam library file text before it is kept (architecture §4.4 <c>steam</c>, §13). The user's
/// profile path becomes <c>%USERPROFILE%</c>, and a non-empty value of these keys becomes <c>redacted</c>:
/// <list type="bullet">
/// <item><c>LastOwner</c> (app manifests): the Steam account that installed the app;</item>
/// <item><c>LastPlayed</c> (app manifests): when the app was last played;</item>
/// <item><c>contentid</c> (<c>libraryfolders.vdf</c>): a random number Steam gives each library folder and keeps
/// for its lifetime, so it identifies one user's folder across captures, like a volume GUID;</item>
/// <item><c>label</c> (<c>libraryfolders.vdf</c>): the name the user gave the library, free text.</item>
/// </list>
/// No check needs any of them. An empty value stays empty, because it holds nothing to hide and "none" is
/// itself the fact. VDF keys are case-insensitive. The file is otherwise kept as written.
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

    /// <summary>
    /// A quoted VDF key-value pair whose key is personal and whose value is not empty. The value may hold
    /// backslash escapes (<c>\"</c>), which the whole value is matched across.
    /// </summary>
    [GeneratedRegex(@"(?<key>""(?:LastOwner|LastPlayed|contentid|contentstatsid|label)""[ \t]+"")(?:[^""\\\r\n]|\\.)+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PersonalValue();
}
