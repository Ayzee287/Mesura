using System.Globalization;
using System.Text.RegularExpressions;

namespace Mesura.Core.Firmware;

/// <summary>
/// What speed a memory kit is sold at, read from its part number: only for the makers whose numbering is public and regular, and
/// only when the number says it outright. Windows cannot read the XMP or EXPO rating itself (it is not in SMBIOS, D004), so this is the
/// one honest source there is, and a number that doesn't match a pattern gives no answer at all, never a guess.
/// </summary>
public static partial class MemoryRatings
{
    /// <summary>The kit's rated speed in MT/s from its part number, or <c>null</c> when the part number isn't one of the patterns below.</summary>
    public static int? RatedMts(string? partNumber)
    {
        var part = partNumber?.Trim();
        if (string.IsNullOrEmpty(part))
        {
            return null;
        }

        // G.Skill: F5-6000J3038F16GX2-FX5 (DDR5-6000 CL30), F4-3600C16D-32GVK (DDR4-3600 CL16).
        if (GSkill().Match(part) is { Success: true } g)
        {
            return Plausible(int.Parse(g.Groups[2].Value, CultureInfo.InvariantCulture), g.Groups[1].Value == "5");
        }

        // Kingston FURY: KF560C36BBEK2-32 (DDR5-6000 CL36), KF432C16BB1K2/32 (DDR4-3200 CL16); the digits are the speed in hundreds, a few DDR4 grades excepted.
        if (Kingston().Match(part) is { Success: true } k)
        {
            var digits = int.Parse(k.Groups[2].Value, CultureInfo.InvariantCulture);
            var ddr5 = k.Groups[1].Value == "5";
            var mts = ddr5 ? digits * 100 : digits switch { 26 => 2666, 29 => 2933, 21 => 2133, _ => digits * 100 };
            return Plausible(mts, ddr5);
        }

        // Corsair: CMK32GX5M2B6000C30 (DDR5-6000 CL30), CMK16GX4M2B3200C16 (DDR4-3200 CL16): the speed sits before the C and the latency.
        if (Corsair().Match(part) is { Success: true } c)
        {
            return Plausible(int.Parse(c.Groups[2].Value, CultureInfo.InvariantCulture), c.Groups[1].Value == "5");
        }

        // Crucial Pro and Ballistix: CP2K16G60C36U5B (DDR5-6000 CL36); the basic CT line is JEDEC memory and has no profile, so it is not rated above JEDEC.
        if (CrucialPro().Match(part) is { Success: true } p)
        {
            return Plausible(int.Parse(p.Groups[1].Value, CultureInfo.InvariantCulture) * 100, part.EndsWith("5", StringComparison.Ordinal) || part.Contains("U5", StringComparison.Ordinal));
        }

        return null;
    }

    /// <summary>A speed that could be a real DDR4 or DDR5 grade (a regular expression can match an unrelated number).</summary>
    private static int? Plausible(int mts, bool ddr5) => ddr5 ? (mts is >= 4800 and <= 9000 ? mts : null) : (mts is >= 2133 and <= 5333 ? mts : null);

    /// <summary>The top of what JEDEC defines for the memory type: a module running above it is running an XMP or EXPO profile (or an overclock); at or below it, base speed.</summary>
    public static int? JedecTop(string? type) => type?.ToUpperInvariant() switch
    {
        "DDR3" => 1866,
        "DDR4" => 3200,
        "DDR5" => 5600,
        _ => null,
    };

    [GeneratedRegex(@"^F([45])-(\d{4})[A-Z]\d{2,4}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GSkill();

    [GeneratedRegex(@"^KF([45])(\d{2})C\d{2}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Kingston();

    [GeneratedRegex(@"^CM[A-Z0-9]*?[GX]([45])M\d[A-Z](\d{4})C\d{2}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Corsair();

    [GeneratedRegex(@"^CP\d?K?\d{1,3}G(\d{2})C\d{2}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CrucialPro();
}
