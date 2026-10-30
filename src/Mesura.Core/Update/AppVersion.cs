using System.Globalization;

namespace Mesura.Core.Update;

/// <summary>
/// A version as releases name it: <c>major.minor.patch</c> with an optional pre-release tail (<c>0.3.0-rc2</c>), ordered as
/// SemVer orders them: a pre-release is older than its release, and pre-release tails compare part by part (numbers as numbers).
/// A build suffix after <c>+</c> is ignored.
/// </summary>
public sealed class AppVersion : IComparable<AppVersion>, IEquatable<AppVersion>
{
    private AppVersion(int major, int minor, int patch, string[] pre)
    {
        (Major, Minor, Patch, Pre) = (major, minor, patch, pre);
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    public string[] Pre { get; }

    public bool IsPrerelease => Pre.Length > 0;

    public static AppVersion? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var plus = text.IndexOf('+', StringComparison.Ordinal);
        var core = (plus < 0 ? text : text[..plus]).Trim().TrimStart('v', 'V');
        var dash = core.IndexOf('-', StringComparison.Ordinal);
        var numbers = (dash < 0 ? core : core[..dash]).Split('.');
        var pre = dash < 0 ? [] : core[(dash + 1)..].Split('.');
        if (numbers.Length is < 2 or > 4 || pre.Any(p => p.Length == 0))
        {
            return null;
        }

        var parsed = new int[3];
        for (var i = 0; i < Math.Min(numbers.Length, 3); i++)
        {
            if (!int.TryParse(numbers[i], NumberStyles.None, CultureInfo.InvariantCulture, out parsed[i]))
            {
                return null;
            }
        }

        return new AppVersion(parsed[0], parsed[1], parsed[2], pre);
    }

    public int CompareTo(AppVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var order = Major.CompareTo(other.Major);
        order = order != 0 ? order : Minor.CompareTo(other.Minor);
        order = order != 0 ? order : Patch.CompareTo(other.Patch);
        if (order != 0)
        {
            return order;
        }

        if (!IsPrerelease || !other.IsPrerelease)
        {
            return IsPrerelease == other.IsPrerelease ? 0 : IsPrerelease ? -1 : 1;
        }

        for (var i = 0; i < Math.Min(Pre.Length, other.Pre.Length); i++)
        {
            var (a, b) = (Pre[i], other.Pre[i]);
            var (aNumber, bNumber) = (int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var an), int.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var bn));
            var part = aNumber && bNumber ? an.CompareTo(bn) : aNumber ? -1 : bNumber ? 1 : Natural(a, b);
            if (part != 0)
            {
                return part;
            }
        }

        return Pre.Length.CompareTo(other.Pre.Length);
    }

    /// <summary>Releases here are named "rc2", "rc10": the letters compare as text, then the trailing number as a number (SemVer alone would put rc10 before rc2).</summary>
    private static int Natural(string a, string b)
    {
        static (string Text, int Number) Split(string s)
        {
            var end = s.Length;
            while (end > 0 && char.IsAsciiDigit(s[end - 1]))
            {
                end--;
            }

            return (s[..end], end < s.Length && int.TryParse(s[end..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1);
        }

        var (at, an) = Split(a);
        var (bt, bn) = Split(b);
        var order = string.CompareOrdinal(at, bt);
        return order != 0 ? order : an.CompareTo(bn);
    }

    public bool Equals(AppVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => Equals(obj as AppVersion);

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, string.Join('.', Pre));

    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (IsPrerelease ? "-" + string.Join('.', Pre) : "");

    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;

    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;

    public static bool operator >=(AppVersion a, AppVersion b) => a.CompareTo(b) >= 0;

    public static bool operator <=(AppVersion a, AppVersion b) => a.CompareTo(b) <= 0;
}
