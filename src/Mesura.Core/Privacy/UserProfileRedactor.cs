namespace Mesura.Core.Privacy;

/// <summary>
/// Rewrites the user's profile path as <c>%USERPROFILE%</c> in collected text (architecture §4.4
/// <c>steam</c>, §13), so the account name in it never reaches a snapshot. It matches the path with
/// backslashes, with doubled backslashes (as escaped in VDF and JSON text) and with forward slashes,
/// case-insensitively, and only where the path ends (so <c>C:\Users\Ann</c> never rewrites part of
/// <c>C:\Users\Anna</c>).
/// </summary>
public sealed class UserProfileRedactor
{
    public const string Placeholder = "%USERPROFILE%";

    private readonly string[] _forms;

    public UserProfileRedactor(string profilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profilePath);

        var path = profilePath.TrimEnd('\\', '/');
        if (path.Length < 3)
        {
            // A root such as "C:" would rewrite every path on the drive.
            throw new ArgumentException($"'{profilePath}' is too short to be a user profile path.", nameof(profilePath));
        }

        _forms = [.. new[]
        {
            path,
            path.Replace("\\", "\\\\", StringComparison.Ordinal),
            path.Replace('\\', '/'),
        }.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(form => form.Length)];
    }

    public string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        foreach (var form in _forms)
        {
            text = ReplaceAtBoundaries(text, form);
        }

        return text;
    }

    private static string ReplaceAtBoundaries(string text, string form)
    {
        var index = text.IndexOf(form, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return text;
        }

        var result = new System.Text.StringBuilder(text.Length);
        var copiedUpTo = 0;

        while (index >= 0)
        {
            var end = index + form.Length;
            if (end == text.Length || !IsPathNameCharacter(text[end]))
            {
                result.Append(text, copiedUpTo, index - copiedUpTo).Append(Placeholder);
                copiedUpTo = end;
            }

            index = text.IndexOf(form, end, StringComparison.OrdinalIgnoreCase);
        }

        return result.Append(text, copiedUpTo, text.Length - copiedUpTo).ToString();
    }

    /// <summary>A character that would continue the profile folder's name rather than end it.</summary>
    private static bool IsPathNameCharacter(char c) =>
        c is not ('\\' or '/' or '"' or '\'' or ';' or ',' or '|' or '<' or '>' or '*' or '?' or ':') && !char.IsWhiteSpace(c) && !char.IsControl(c);
}
