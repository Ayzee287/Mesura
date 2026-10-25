using Mesura.Core.Nvidia;

namespace Mesura.Core.Windows;

/// <summary>
/// What Optimize shows about this PC and decides its sections from (redesign 2026-10 §3): the Windows edition and
/// build, whether it is a laptop (a system battery that isn't a UPS) or a Modern Standby PC, its NVIDIA GPUs, and
/// whether it is a Ghost Spectre image. A value that couldn't be read is <c>null</c> (or empty), never guessed.
/// </summary>
/// <param name="Edition">The edition id, e.g. <c>Professional</c>, or empty.</param>
/// <param name="Version">The feature update, e.g. <c>25H2</c>, or empty.</param>
/// <param name="HasBattery">A system battery (a laptop or tablet); <c>null</c> when unread.</param>
/// <param name="ModernStandby">A Modern Standby (S0) PC; <c>null</c> when unread.</param>
/// <param name="Gpus">Every hardware graphics adapter by name, as its driver reports it.</param>
/// <param name="NvidiaGpus">The NVIDIA ones among them (PCI vendor 0x10DE), by name.</param>
/// <param name="GpusRead">Whether the adapters could be read at all (when not, NVIDIA is unknown, not absent).</param>
/// <param name="GhostSpectre">Whether the Ghost Spectre toolbox folder this image installs is present.</param>
/// <param name="NvidiaAdapters">The NVIDIA adapters with their PCI device id and driver version (D031); empty when none or unread.</param>
public sealed record SystemSummary(
    string Edition,
    string Version,
    int Build,
    bool? HasBattery,
    bool? ModernStandby,
    IReadOnlyList<string> Gpus,
    IReadOnlyList<string> NvidiaGpus,
    bool GpusRead,
    bool GhostSpectre,
    IReadOnlyList<NvidiaGpu>? NvidiaAdapters = null)
{
    /// <summary>The first NVIDIA adapter (the one the NVIDIA steps are for), or <c>null</c>.</summary>
    public NvidiaGpu? Nvidia => NvidiaAdapters?.FirstOrDefault();

    /// <summary>The PCI vendor id of NVIDIA.</summary>
    public const int NvidiaVendorId = 0x10DE;

    public bool HasNvidia => NvidiaGpus.Count > 0;

    /// <summary>Windows 11 starts at build 22000.</summary>
    public string WindowsName => Build >= 22000 ? "Windows 11" : "Windows 10";
}
