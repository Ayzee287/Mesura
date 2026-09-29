using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FpsUtility.Core.Raw.Serialization;

namespace FpsUtility.Core.TrackA;

/// <summary>The program an evidence file comes from, by the kit's naming convention.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<EvidenceTool>))]
public enum EvidenceTool
{
    /// <summary><c>gpu-z-…</c>: TechPowerUp GPU-Z.</summary>
    GpuZ,

    /// <summary><c>hwinfo-…</c>: HWiNFO64.</summary>
    Hwinfo,

    /// <summary><c>devmgr-…</c>: Device Manager, Devices by connection.</summary>
    DeviceManager,

    /// <summary><c>pcie-probe-…</c>: the step-0 research probe (<c>research\pcie-spike\pcie-probe.ps1</c>), outside the kit.</summary>
    PcieProbe,
}

[JsonConverter(typeof(CamelCaseEnumConverter<EvidenceKind>))]
public enum EvidenceKind
{
    Screenshot,

    /// <summary>A text file a tool wrote (the probe's JSON, an export).</summary>
    TextOutput,
}

/// <summary>What the operator says the PC was doing when the file was made: the file name's state part.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<EvidenceState>))]
public enum EvidenceState
{
    Idle,

    /// <summary><c>load</c>, or the research procedure's <c>game</c>.</summary>
    Load,

    /// <summary>The research procedure's idle run after the game (<c>after</c>).</summary>
    After,
}

/// <summary>A result of scanning a text evidence file for the capturing PC's identifiers. Informational: evidence is reviewed by a person.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<EvidenceTextScan>))]
public enum EvidenceTextScan
{
    /// <summary>An image: not scanned (no OCR, by design).</summary>
    NotText,

    NoIdentifierFound,

    IdentifierFound,
}

/// <summary>An evidence file's name read by the convention.</summary>
public sealed record EvidenceName(EvidenceTool Tool, EvidenceState State, EvidenceKind Kind);

/// <summary>
/// The kit's evidence naming convention (architecture §4.7): <c>&lt;tool&gt;-&lt;state&gt;[-suffix…].&lt;ext&gt;</c>, with tool
/// <c>gpu-z</c>, <c>hwinfo</c>, <c>devmgr</c> or <c>pcie-probe</c>, state <c>idle</c>, <c>load</c> (or <c>game</c>) or
/// <c>after</c>, and an image or text extension. A suffix is a number (<c>hwinfo-idle-2.png</c>), the probe's own time
/// stamp, or one of a few fixed words (<see cref="SuffixWords"/>), never free text. A file that doesn't follow it is never
/// recorded, not even by name, because a free file name can carry a person's name.
/// </summary>
public static partial class EvidenceConvention
{
    public static EvidenceName? Read(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        var match = Pattern().Match(fileName);
        if (!match.Success)
        {
            return null;
        }

        var tool = match.Groups["tool"].Value.ToUpperInvariant() switch
        {
            "GPU-Z" => EvidenceTool.GpuZ,
            "HWINFO" => EvidenceTool.Hwinfo,
            "DEVMGR" => EvidenceTool.DeviceManager,
            _ => EvidenceTool.PcieProbe,
        };
        var state = match.Groups["state"].Value.ToUpperInvariant() switch
        {
            "IDLE" => EvidenceState.Idle,
            "AFTER" => EvidenceState.After,
            _ => EvidenceState.Load,
        };
        var kind = match.Groups["ext"].Value.ToUpperInvariant() is "PNG" or "JPG" or "JPEG" or "BMP" ? EvidenceKind.Screenshot : EvidenceKind.TextOutput;

        return new EvidenceName(tool, state, kind);
    }

    /// <summary>Whether a file's state belongs to a scenario step: idle files to the idle step, load and after files to the load step, any to a one-step scenario.</summary>
    public static bool BelongsTo(EvidenceState state, string? step) => step switch
    {
        null => true,
        ScenarioCatalog.IdleStep => state == EvidenceState.Idle,
        ScenarioCatalog.LoadStep => state is EvidenceState.Load or EvidenceState.After,
        _ => false,
    };

    /// <summary>The words a suffix may be, besides a number and the probe's time stamp: parts of the PCIe chain.</summary>
    public static IReadOnlyList<string> SuffixWords { get; } = ["gpu", "bridge", "switch", "upstream", "downstream", "rootport", "port", "chain"];

    [GeneratedRegex(@"^(?<tool>gpu-z|hwinfo|devmgr|pcie-probe)-(?<state>idle|load|game|after)(?:-(?:[0-9]{1,3}|[0-9]{8}T[0-9]{6}Z|gpu|bridge|switch|upstream|downstream|rootport|port|chain))*\.(?<ext>png|jpg|jpeg|bmp|json|txt|csv)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

/// <summary>An evidence file as the kit found it: never interpreted, only named, hashed and, for text, scanned for this PC's identifiers.</summary>
public sealed record EvidenceFile(string Name, byte[] Contents);
