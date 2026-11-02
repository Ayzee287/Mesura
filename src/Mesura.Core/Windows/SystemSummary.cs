using Mesura.Core.Nvidia;

namespace Mesura.Core.Windows;

/// <summary>A hardware graphics adapter as Windows reports it: name, PCI vendor and device id, the driver version Windows shows, and whether it is the processor's own graphics.</summary>
public sealed record GpuAdapter(string Name, int VendorId, int DeviceId, string DriverVersion, bool? Integrated);

/// <summary>
/// What Optimize shows about this PC and decides its sections from (redesign 2026-10 §3): the Windows edition and
/// build, whether it is a laptop (a system battery that isn't a UPS) or a Modern Standby PC, its NVIDIA GPUs, and
/// which graphics adapters it has. A value that couldn't be read is <c>null</c> (or empty), never guessed.
/// </summary>
/// <param name="Edition">The edition id, e.g. <c>Professional</c>, or empty.</param>
/// <param name="Version">The feature update, e.g. <c>25H2</c>, or empty.</param>
/// <param name="HasBattery">A system battery (a laptop or tablet); <c>null</c> when unread.</param>
/// <param name="ModernStandby">A Modern Standby (S0) PC; <c>null</c> when unread.</param>
/// <param name="Gpus">Every hardware graphics adapter by name, as its driver reports it.</param>
/// <param name="NvidiaGpus">The NVIDIA ones among them (PCI vendor 0x10DE), by name.</param>
/// <param name="GpusRead">Whether the adapters could be read at all (when not, NVIDIA is unknown, not absent).</param>
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
    IReadOnlyList<NvidiaGpu>? NvidiaAdapters = null,
    IReadOnlyList<GpuAdapter>? Adapters = null)
{
    /// <summary>The first NVIDIA adapter (the one the NVIDIA steps are for), or <c>null</c>.</summary>
    public NvidiaGpu? Nvidia => NvidiaAdapters?.FirstOrDefault();

    /// <summary>Every NVIDIA adapter by name, joined for one line (a PC with two cards shows both; the steps use the first).</summary>
    public string NvidiaNames => string.Join(" + ", (NvidiaAdapters ?? []).Select(a => a.Name).Distinct(StringComparer.Ordinal));

    /// <summary>The AMD adapters (PCI vendor 0x1002): a Radeon card or the graphics inside a Ryzen. Mesura detects and names them; it changes none of their settings.</summary>
    public IReadOnlyList<GpuAdapter> Amd => [.. (Adapters ?? []).Where(a => a.VendorId == AmdVendorId)];

    /// <summary>The PCI vendor id of AMD's graphics division.</summary>
    public const int AmdVendorId = 0x1002;

    /// <summary>The PCI vendor id of NVIDIA.</summary>
    public const int NvidiaVendorId = 0x10DE;

    public bool HasNvidia => NvidiaGpus.Count > 0;

    /// <summary>Windows 11 starts at build 22000.</summary>
    public string WindowsName => Build >= 22000 ? "Windows 11" : "Windows 10";
}
