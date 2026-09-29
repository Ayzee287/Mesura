using FpsUtility.Core.Facts;
using FpsUtility.Core.Raw;

namespace FpsUtility.Core.Normalize;

/// <summary>
/// Turns a raw snapshot into normalised facts (architecture §5 "Normalise"): raw observation → fact, with provenance.
/// It is a pure function: no clock, no randomness, no platform call, no I/O. The same raw snapshot always gives the
/// same facts, in the same order, with the same ids.
/// </summary>
/// <remarks>
/// Each domain has its own normaliser, and each states its rules. A source that was <c>unavailable</c> makes only
/// its own facts missing. Data that breaks a rule of the specification becomes a missing fact
/// (<see cref="FactGap.Implausible"/>, <see cref="FactGap.ParseFailed"/>). A defect in this code is not caught here:
/// it surfaces as an exception, so it is found rather than hidden.
/// </remarks>
public static class Normalizer
{
    public static NormalizedSnapshot Normalize(RawSnapshot raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var sources = raw.Sources;
        var (machine, processors, memory) = SmbiosNormalizer.Normalize(sources.Smbios);
        var (disks, volumes) = StorageNormalizer.Normalize(sources.Storage);

        return new NormalizedSnapshot(
            SchemaVersion: NormalizedSnapshot.CurrentSchemaVersion,
            RawSchemaVersion: raw.SchemaVersion,
            CapturedAtUtc: raw.CapturedAtUtc,
            Os: SystemNormalizer.Os(raw.Os),
            Power: SystemNormalizer.Power(sources.Power),
            Machine: machine,
            Processors: processors,
            MemoryDevices: memory,
            Displays: DisplayNormalizer.Displays(sources),
            Gpus: GpuNormalizer.Gpus(sources.Adapters, sources.Pcie),
            PciDevices: GpuNormalizer.PciDevices(sources.Pcie),
            Disks: disks,
            Volumes: volumes,
            Steam: SteamNormalizer.Normalize(sources.Steam));
    }
}
