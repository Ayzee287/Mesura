namespace Mesura.Core.Windows;

/// <summary>
/// A GPU driver switch an operation sets (0.6): which maker, which feature, and whether it is to be on. Only what the product has proven on a real card exists:
/// <c>amd</c> + <c>antilag</c>, the original Anti-Lag (level 0). The values never say how to write: the setup component's AMD writer does (record first, a separate
/// reading, rollback), and the operation only names the switch.
/// </summary>
public sealed record GpuSetting(string Vendor, string Feature, bool Enabled)
{
    public const string Amd = "amd";

    public const string AntiLag = "antilag";

    /// <summary>The pairs the catalog may name.</summary>
    public static bool IsAllowed(string vendor, string feature) =>
        vendor.Equals(Amd, StringComparison.OrdinalIgnoreCase) && feature.Equals(AntiLag, StringComparison.OrdinalIgnoreCase);

    /// <summary>The words used for this switch in messages.</summary>
    public string Label => Feature.Equals(AntiLag, StringComparison.OrdinalIgnoreCase) ? "Anti-Lag" : Feature;

    public string Target => $"{Vendor} {Feature}";
}

/// <summary>Whether a GPU driver could be asked about a switch.</summary>
public enum GpuAvailability
{
    /// <summary>The driver answered.</summary>
    Available,

    /// <summary>No adapter of that maker is installed.</summary>
    NoAdapter,

    /// <summary>An adapter of that maker is installed but its driver offers no way to read the switch (no ADLX).</summary>
    NoDriver,

    /// <summary>The driver is there but did not answer.</summary>
    Unreadable,
}

/// <summary>
/// What a driver says about one switch on one GPU: whether it is offered, whether it is on and, for Anti-Lag, which level is selected (0 is the original; 1 is
/// Anti-Lag Next, which is never changed). Every value the driver did not give is <c>null</c>, never a default.
/// </summary>
/// <param name="GpuIndex">The GPU's index in the driver's list, passed back to the writer.</param>
public sealed record GpuFeatureReading(GpuAvailability Availability, string? GpuName, int GpuIndex, bool? Supported, bool? Enabled, int? Level, string? Note)
{
    public static GpuFeatureReading None(GpuAvailability availability, string? note = null) => new(availability, null, 0, null, null, null, note);
}

/// <summary>What the GPU writer reports for one write: the id of the record it kept (what an undo needs) and its own words.</summary>
public sealed record GpuWriteResult(string ChangeId, string Detail);
