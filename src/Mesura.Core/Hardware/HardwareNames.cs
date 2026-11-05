using System.Text.RegularExpressions;

namespace Mesura.Core.Hardware;

/// <summary>Hardware names as a person says them.</summary>
public static partial class HardwareNames
{
    /// <summary>
    /// A processor's or graphics card's name without what only a driver says: "11th Gen Intel(R) Core(TM) i7-1165G7 @ 2.80GHz" becomes "Intel Core i7-1165G7",
    /// "Intel(R) Iris(R) Xe Graphics" becomes "Intel Iris Xe Graphics". Nothing is added or guessed; a name with none of those parts comes back as it is.
    /// </summary>
    public static string Short(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var text = name.Replace("(R)", "", StringComparison.Ordinal).Replace("(TM)", "", StringComparison.Ordinal).Replace(" CPU", "", StringComparison.Ordinal);
        if (text.IndexOf(" @ ", StringComparison.Ordinal) is var at and > 0)
        {
            text = text[..at];
        }

        text = Generation().Replace(text, "");
        return Spaces().Replace(text, " ").Trim();
    }

    [GeneratedRegex(@"^\d+(st|nd|rd|th) Gen ")]
    private static partial Regex Generation();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
