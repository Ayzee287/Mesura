using System.Text.RegularExpressions;

namespace FpsUtility.Core.Checks;

/// <summary>
/// The shapes a check result's identifiers may take. Codes are machine tokens, never text for a person: a sentence
/// can't pass these patterns, so a check can't smuggle prose into a result (architecture §4.6).
/// </summary>
internal static partial class Codes
{
    /// <summary>A check, finding, trace, missing or subject code: lower-case kebab-case (<c>refresh-vs-highest-offered</c>, <c>display-1</c>).</summary>
    public static string Kebab(string value, string what) =>
        value is not null && KebabPattern().IsMatch(value) ? value : throw new ArgumentException($"{what} '{value}' is not a lower-case kebab-case code.", what);

    /// <summary>A check id: a domain, a dot, a kebab-case name (<c>display.refresh-below-max</c>).</summary>
    public static string CheckId(string value, string what) =>
        value is not null && CheckIdPattern().IsMatch(value) ? value : throw new ArgumentException($"{what} '{value}' is not a check id.", what);

    /// <summary>A parameter or value name: camelCase (<c>highestOffered</c>).</summary>
    public static string Name(string value, string what) =>
        value is not null && NamePattern().IsMatch(value) ? value : throw new ArgumentException($"{what} '{value}' is not a camelCase name.", what);

    /// <summary>A symbol: an enum member's camelCase name or a snapshot-scoped id (<c>displayPort</c>, <c>gpu-2</c>).</summary>
    public static string Symbol(string value, string what) =>
        value is not null && SymbolPattern().IsMatch(value) ? value : throw new ArgumentException($"{what} '{value}' is not a symbol.", what);

    /// <summary>A condition key: the verdict, then kebab-case segments separated by '/' (<c>problem/external</c>).</summary>
    public static string Condition(string value, string verdict, string what) =>
        value is not null && ConditionPattern().IsMatch(value) && value.StartsWith(verdict + "/", StringComparison.Ordinal)
            ? value
            : throw new ArgumentException($"{what} '{value}' is not a condition key that starts with '{verdict}/'.", what);

    /// <summary>A JSON Pointer (RFC 6901) into the normalised facts, e.g. <c>/displays/value/0/signalRefresh</c>.</summary>
    public static string FactPointer(string value, string what) =>
        value is not null && FactPointerPattern().IsMatch(value) ? value : throw new ArgumentException($"{what} '{value}' is not a JSON Pointer into the facts.", what);

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex KebabPattern();

    [GeneratedRegex("^[a-z]+\\.[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex CheckIdPattern();

    [GeneratedRegex("^[a-z][A-Za-z0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[a-z0-9][A-Za-z0-9]*(?:-[A-Za-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SymbolPattern();

    [GeneratedRegex("^[a-z]+(?:/[a-z0-9]+(?:-[a-z0-9]+)*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex ConditionPattern();

    [GeneratedRegex("^(?:/[A-Za-z0-9]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex FactPointerPattern();
}
