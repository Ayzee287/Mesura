using System.Text.RegularExpressions;

namespace Mesura.Core.Apps;

/// <summary>
/// One Microsoft Visual C++ Redistributable Mesura can install: Microsoft's own winget package for one year and one
/// architecture. Each becomes an <see cref="AppPackage"/> (not listed on Apps) so the winget installer runs it, with a
/// detector of its own, because winget's list misses runtimes installed by other installers.
/// </summary>
/// <param name="Supported">Whether Microsoft still supports it (only the 2015–2026 v14 runtime is).</param>
/// <param name="Default">Whether a setup includes it unless the user unticks it.</param>
public sealed record VcRuntime(string Id, string WingetId, string Year, string Arch, bool Supported, bool Default)
{
    /// <summary>The installer package: Microsoft's winget package, detected by <see cref="Id"/>'s own detector.</summary>
    public AppPackage Package { get; } = new(Id, WingetId, $"Visual C++ {Year} ({Arch})", "Runtime", AppSource.Winget,
        Listed: false, Detector: Id);
}

/// <summary>A runtime found on this PC: which one, and its version as installed.</summary>
public sealed record InstalledRuntime(string Id, string Version);

/// <summary>
/// The Visual C++ runtimes a setup offers (D031) and how they are recognised. Source: Microsoft's packages in winget
/// (publisher Microsoft Corporation, downloads from Microsoft's servers, each checked against the manifest's SHA-256),
/// never an all-in-one repack, which removes existing runtimes to install modified ones. 2015–2026 is Microsoft's one
/// supported runtime; 2008 to 2013 are unsupported but kept for older games; 2005 is offered unticked, since its winget
/// packages pick a localised installer by the user's language list.
/// <para>
/// Recognition: the v14 runtime by Microsoft's documented key
/// (<c>HKLM\SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x64|x86</c>, <c>Installed</c> = 1), the older
/// years by their uninstall entries' names ("Microsoft Visual C++ 2012 x64 Minimum Runtime - 11.0.61030",
/// "Microsoft Visual C++ 2005 Redistributable (x64)"; 2005's x86 entry names no architecture). Read on PC-000,
/// 2026-10-23. Any installed build counts as installed, including a build newer than Microsoft's public one.
/// </para>
/// </summary>
public static partial class VcRuntimes
{
    public static IReadOnlyList<VcRuntime> All { get; } =
    [
        new("vcredist-2015plus-x64", "Microsoft.VCRedist.2015+.x64", "2015–2026", "x64", true, true),
        new("vcredist-2015plus-x86", "Microsoft.VCRedist.2015+.x86", "2015–2026", "x86", true, true),
        new("vcredist-2013-x64", "Microsoft.VCRedist.2013.x64", "2013", "x64", false, true),
        new("vcredist-2013-x86", "Microsoft.VCRedist.2013.x86", "2013", "x86", false, true),
        new("vcredist-2012-x64", "Microsoft.VCRedist.2012.x64", "2012", "x64", false, true),
        new("vcredist-2012-x86", "Microsoft.VCRedist.2012.x86", "2012", "x86", false, true),
        new("vcredist-2010-x64", "Microsoft.VCRedist.2010.x64", "2010", "x64", false, true),
        new("vcredist-2010-x86", "Microsoft.VCRedist.2010.x86", "2010", "x86", false, true),
        new("vcredist-2008-x64", "Microsoft.VCRedist.2008.x64", "2008", "x64", false, true),
        new("vcredist-2008-x86", "Microsoft.VCRedist.2008.x86", "2008", "x86", false, true),
        new("vcredist-2005-x64", "Microsoft.VCRedist.2005.x64", "2005", "x64", false, false),
        new("vcredist-2005-x86", "Microsoft.VCRedist.2005.x86", "2005", "x86", false, false),
    ];

    public static VcRuntime? ById(string id) => All.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The runtime an uninstall entry's display name belongs to (2005 to 2013), or <c>null</c>.</summary>
    public static VcRuntime? FromDisplayName(string? displayName)
    {
        var match = OlderRuntime().Match(displayName ?? "");
        if (!match.Success || match.Groups["rest"].Value.Contains("Debug", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var year = match.Groups["year"].Value;
        var rest = match.Groups["rest"].Value;
        var arch = rest.Contains("x64", StringComparison.OrdinalIgnoreCase) ? "x64"
            : rest.Contains("x86", StringComparison.OrdinalIgnoreCase) || year == "2005" ? "x86"
            : null;
        return arch is null ? null : All.FirstOrDefault(r => r.Year == year && r.Arch == arch);
    }

    /// <summary>The v14 runtime for an architecture (<c>x64</c>, <c>x86</c>).</summary>
    public static VcRuntime? V14(string arch) => All.FirstOrDefault(r => r.Supported && r.Arch.Equals(arch, StringComparison.OrdinalIgnoreCase));

    /// <summary>A v14 version from its registry values (<c>Major</c>, <c>Minor</c>, <c>Bld</c>), e.g. <c>14.51.36247</c>.</summary>
    public static string V14Version(int major, int minor, int build) => $"{major}.{minor}.{build}";

    [GeneratedRegex(@"^Microsoft Visual C\+\+ (?<year>2005|2008|2010|2012|2013)\b(?<rest>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex OlderRuntime();
}
