using System.Text.RegularExpressions;

namespace Mesura.Core.Nvidia;

/// <summary>
/// The official NVIDIA installer as an automatable route (0.5). NVIDIA's GeForce package is a self-extracting archive of one folder per component plus
/// <c>setup.exe</c> and <c>setup.cfg</c>. Read from the 621.01 package (2026-11-01, static: nothing was run): every component is a package named in its
/// <c>.nvi</c> file (<c>Display.Driver</c>, <c>HDAudio.Driver</c>, <c>Display.PhysX</c>, <c>ShadowPlay</c>, ...); <c>setup.cfg</c> declares the options
/// <c>-clean</c>, <c>-noreboot</c> (<c>-n</c>), <c>-passive</c>, <c>-noeula</c>, <c>-nofinish</c>, <c>-nosplash</c>, <c>-enableTelemetry</c>, <c>-prestage</c>, <c>-validate</c>;
/// <c>setup.exe</c> itself has <c>-s</c> (silent), <c>-deselect</c>, <c>-uninstall</c> and the messages "Command line should specify either packages to install or to
/// deselect" and "User-selected package not found". NVIDIA documents <c>setup.exe -s -n Display.Driver</c> (packages as plain arguments) for its data-centre package;
/// the same installer technology is used here, so the packages are passed the same way. That a GeForce package honours it was NOT run (no NVIDIA GPU was available);
/// the result is always read back (the driver version, the packages found installed) and never assumed from the exit code.
/// </summary>
public static partial class OfficialInstall
{
    /// <summary>NVCleanstall's component ids (<see cref="NvidiaComponents"/>) and the official packages that make them up, by the package names in the 621.01 package.</summary>
    public static IReadOnlyDictionary<string, string[]> Packages { get; } = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        [NvidiaComponents.DisplayDriver] = ["Display.Driver"],
        ["hd-audio"] = ["HDAudio.Driver"],
        ["legacy-control-panel"] = ["Display.ControlPanel"], // not in the 621.01 package (the control panel is the NVIDIA App's now): reported as absent, never invented
        ["vcredist-2017"] = ["MSVCRuntime2019", "MSVCRuntime2017"],
        ["physx"] = ["Display.PhysX"],
        ["usb-c"] = ["USBC"],
        ["frameview"] = ["FrameViewSdk"],
        ["quadro-view"] = ["Display.NView"], // not in the 621.01 package either
        ["virtual-audio"] = ["VirtualAudio.Driver"],
        ["telemetry"] = ["NvTelemetry"],
        ["platform-controllers"] = ["NVPCF"],
        ["dlisr"] = ["NvDLISR"],
        ["nvidia-app"] = ["Display.NvApp", "Display.NvApp.NvCPL"],
        ["nv-container"] = ["NvContainer", "NvContainer.LocalSystem", "NvContainer.Session", "NvContainer.User", "NvContainer.AIUser", "NvPlugin.Watchdog"],
        ["shadowplay"] = ["ShadowPlay"],
        ["nv-backend"] = ["Display.NvApp.NvBackend"],
        ["app-messagebus"] = ["Display.NvApp.MessageBus"],
    };

    /// <summary>The package name an <c>.nvi</c> file declares (<c>&lt;nvi name="..."&gt;</c>), or <c>null</c> for one that is a template (<c>${{PackageId}}</c>) or not an .nvi.</summary>
    public static string? PackageName(string nviText)
    {
        var match = NviName().Match(nviText ?? "");
        return match.Success && !match.Groups[1].Value.StartsWith("${{", StringComparison.Ordinal) ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// What an installation of <paramref name="config"/> would do with a package that offers <paramref name="available"/> packages: the packages passed to the installer,
    /// the components whose packages are not in this driver (shown, never passed), the packages in the package that are left out, and the command line.
    /// </summary>
    public static OfficialInstallPlan Plan(NvidiaDriverConfig config, IReadOnlyCollection<string> available, string logFile)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(available);
        var normal = NvidiaDriverConfig.Normalize(config);
        var have = new HashSet<string>(available, StringComparer.OrdinalIgnoreCase);
        var install = new List<string>();
        var absent = new List<string>();
        foreach (var id in normal.Components)
        {
            if (!Packages.TryGetValue(id, out var names))
            {
                absent.Add(id);
                continue;
            }

            var found = names.Where(have.Contains).ToList();
            if (found.Count == 0)
            {
                absent.Add(id);
            }

            install.AddRange(found);
        }

        var packages = install.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var left = have.Where(p => !packages.Contains(p, StringComparer.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var arguments = new List<string> { "-s", "-noreboot", "-noeula", "-nofinish", "-nosplash" };
        if (normal.CleanInstall)
        {
            arguments.Add("-clean");
        }

        if (!string.IsNullOrWhiteSpace(logFile))
        {
            arguments.Add($"-log:{logFile}");
        }

        arguments.AddRange(packages);
        return new OfficialInstallPlan(packages, absent, left, arguments, have.Contains("Display.Driver"));
    }

    /// <summary>How the installer's exit code is read: 0 installed, 1 installed and Windows needs a restart (NVIDIA's data-centre guide); anything else is a failure.</summary>
    public static OfficialInstallOutcome Outcome(int exitCode) => exitCode switch
    {
        0 => OfficialInstallOutcome.Installed,
        1 => OfficialInstallOutcome.InstalledRestartNeeded,
        _ => OfficialInstallOutcome.Failed,
    };

    [GeneratedRegex(@"<nvi\s[^>]*?\bname\s*=\s*""([^""]*)""", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex NviName();
}

/// <summary>What <see cref="OfficialInstall.Plan"/> decided.</summary>
/// <param name="Install">The package names passed to the installer.</param>
/// <param name="ComponentsNotInThisPackage">Selected components with no package in this driver.</param>
/// <param name="LeftOut">Packages in the driver package that are not installed.</param>
/// <param name="Arguments">The installer's command line.</param>
/// <param name="HasDisplayDriver">Whether the package contains the display driver at all (without it nothing is run).</param>
public sealed record OfficialInstallPlan(
    IReadOnlyList<string> Install,
    IReadOnlyList<string> ComponentsNotInThisPackage,
    IReadOnlyList<string> LeftOut,
    IReadOnlyList<string> Arguments,
    bool HasDisplayDriver);

public enum OfficialInstallOutcome
{
    Installed,
    InstalledRestartNeeded,
    Failed,
}
