using System.Globalization;
using System.Text;
using Mesura.Core.Raw;

namespace Mesura.Core.Support;

/// <summary>Why the platform check refused to run.</summary>
public enum PlatformRefusal
{
    NotWindows,

    /// <summary>Build below 19045 (Windows 10 22H2), including Windows 10 LTSC 2021 (19044).</summary>
    BuildBelowFloor,

    /// <summary>The OS isn't x64; in practice Windows 11 on Arm running this x64 app under emulation.</summary>
    NotX64,

    ArchitectureUnknown,

    /// <summary>Windows Server, whatever its build (D013).</summary>
    Server,

    ProductTypeUnknown,
}

/// <param name="Refusals">Every reason the platform is refused; empty when it is supported.</param>
/// <param name="Message">The user-facing refusal message (D011); <c>null</c> when supported.</param>
public sealed record PlatformDecision(IReadOnlyList<PlatformRefusal> Refusals, string? Message)
{
    public bool IsSupported => Refusals.Count == 0;
}

/// <summary>
/// The startup platform check (D008, D011, D013; architecture §17.1): a pure decision over the OS
/// facts, made before any collection. A fact that can't be read is a refusal, because the app never
/// continues silently on a platform it couldn't confirm.
/// </summary>
public static class PlatformSupport
{
    /// <summary>Windows 10 version 22H2. The floor is this build number, never a version name (D008).</summary>
    public const int MinimumBuild = 19045;

    public const string Requirement =
        "Mesura requires Windows 10 version 22H2 (build 19045) or later, or Windows 11, on a 64-bit x64 PC.";

    public static PlatformDecision Evaluate(OsInfo os)
    {
        ArgumentNullException.ThrowIfNull(os);

        var refusals = new List<PlatformRefusal>();

        if (os.Platform != OsPlatform.Windows)
        {
            refusals.Add(PlatformRefusal.NotWindows);
        }

        if (os.Build < MinimumBuild)
        {
            refusals.Add(PlatformRefusal.BuildBelowFloor);
        }

        if (!os.Architecture.IsKnown)
        {
            refusals.Add(PlatformRefusal.ArchitectureUnknown);
        }
        else if (os.Architecture.Value != MachineArchitecture.X64)
        {
            refusals.Add(PlatformRefusal.NotX64);
        }

        if (!os.ProductType.IsKnown)
        {
            refusals.Add(PlatformRefusal.ProductTypeUnknown);
        }
        else if (os.ProductType.Value == OsProductType.Server)
        {
            refusals.Add(PlatformRefusal.Server);
        }

        return refusals.Count == 0
            ? new PlatformDecision([], null)
            : new PlatformDecision(refusals, BuildMessage(os, refusals));
    }

    private static string BuildMessage(OsInfo os, IReadOnlyList<PlatformRefusal> refusals)
    {
        var message = new StringBuilder();
        message.AppendLine("This version of Windows isn't supported, so Mesura won't run on it.");
        message.AppendLine(CultureInfo.InvariantCulture, $"Detected: {Describe(os)}.");

        foreach (var refusal in refusals)
        {
            message.AppendLine(Explain(refusal, os));
        }

        message.Append(Requirement);
        return message.ToString();
    }

    /// <summary>Names what was detected without inferring a product name from it.</summary>
    private static string Describe(OsInfo os)
    {
        var build = os.UpdateRevision.IsKnown
            ? string.Create(CultureInfo.InvariantCulture, $"{os.Build}.{os.UpdateRevision.Value}")
            : os.Build.ToString(CultureInfo.InvariantCulture);

        return string.Join(", ",
            $"platform {os.Platform}",
            $"edition {Show(os.Edition)}",
            $"version {Show(os.Version)}",
            $"build {build}",
            $"architecture {Show(os.Architecture)}",
            $"product type {Show(os.ProductType)}");
    }

    private static string Explain(PlatformRefusal refusal, OsInfo os) => refusal switch
    {
        PlatformRefusal.NotWindows => "It isn't Windows.",
        PlatformRefusal.BuildBelowFloor => string.Create(CultureInfo.InvariantCulture, $"Build {os.Build} is older than build {MinimumBuild} (Windows 10 version 22H2)."),
        PlatformRefusal.NotX64 => $"The PC isn't x64 (detected {Show(os.Architecture)}). A 64-bit Arm PC isn't x64.",
        PlatformRefusal.ArchitectureUnknown => $"The processor architecture couldn't be confirmed: {os.Architecture.Detail}",
        PlatformRefusal.Server => "Windows Server isn't supported.",
        PlatformRefusal.ProductTypeUnknown => $"Whether this is a client or server edition couldn't be confirmed: {os.ProductType.Detail}",
        _ => throw new ArgumentOutOfRangeException(nameof(refusal), refusal, null),
    };

    private static string Show<T>(Reading<T> reading) =>
        reading.IsKnown
            ? reading.Value switch
            {
                MachineArchitecture architecture => architecture switch
                {
                    MachineArchitecture.X64 => "x64",
                    MachineArchitecture.Arm64 => "Arm64",
                    MachineArchitecture.X86 => "x86",
                    _ => reading.Native is { } code ? string.Create(CultureInfo.InvariantCulture, $"other (machine code 0x{code:X4})") : "other",
                },
                OsProductType type => type == OsProductType.Server ? "server" : "workstation",
                var value => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "unknown",
            }
            : "unknown";
}
