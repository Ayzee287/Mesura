using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mesura.Core.GpuControl;

/// <summary>A GPU feature Mesura can change through a vendor's own interface (AMD's ADLX, 0.6): each is one switch, read before and after.</summary>
public enum GpuFeature
{
    /// <summary>AMD Anti-Lag, the original driver feature (level 0). Anti-Lag Next and Anti-Lag+ are never written.</summary>
    AntiLag,

    /// <summary>AMD SmartAccess Memory (the driver's switch; the BIOS side is never touched).</summary>
    SmartAccessMemory,

    /// <summary>AMD FreeSync on one display.</summary>
    FreeSync,
}

/// <summary>What the driver says about one switch: supported, on, and (Anti-Lag only) which level is selected.</summary>
public sealed record GpuFeatureState(bool? Supported, bool? Enabled, int? Level = null, string? Note = null);

/// <summary>What a change would do, from fresh readings, before anything is written: the same text the apply step acts on.</summary>
public sealed record GpuChangePreview(
    GpuFeature Feature,
    string Gpu,
    string? Display,
    bool Requested,
    GpuFeatureState Current,
    IReadOnlyDictionary<string, GpuFeatureState> Related,
    string Plan,
    IReadOnlyList<string> SideEffects,
    IReadOnlyList<string> Blockers,
    bool AlreadyInState)
{
    /// <summary>Whether a write is possible and needed.</summary>
    public bool CanApply => Blockers.Count == 0 && !AlreadyInState;
}

/// <summary>How a change ended. <see cref="Applied"/> only when a second, independent reading shows the requested state.</summary>
public enum GpuChangeStatus
{
    /// <summary>Nothing was written (preview only, or refused).</summary>
    NotWritten,

    /// <summary>The record exists and the write was started; the outcome is not known yet (a crash here leaves this).</summary>
    Written,

    /// <summary>Written and read back by a new session: the requested state is in place.</summary>
    Applied,

    /// <summary>The driver answered success but the independent reading does not show the requested state: not reported as applied.</summary>
    NotConfirmed,

    /// <summary>The driver took the change but it needs a restart before it is in effect: not confirmed.</summary>
    RestartNeeded,

    /// <summary>The driver refused or failed.</summary>
    Failed,

    /// <summary>Put back and read back: the state from before the change (including what the driver changed beside it) is in place.</summary>
    Reverted,

    /// <summary>Put back, but a reading shows something differs from before.</summary>
    RevertIncomplete,
}

/// <summary>One change, kept on disk before the write so that it can be undone even if the process dies half way.</summary>
public sealed record GpuChangeRecord(
    string Id,
    DateTimeOffset CreatedUtc,
    string Vendor,
    GpuFeature Feature,
    string GpuName,
    int? GpuUniqueId,
    string? DisplayName,
    int? DisplayIndex,
    bool Requested,
    GpuFeatureState Before,
    IReadOnlyDictionary<string, GpuFeatureState> RelatedBefore,
    GpuChangeStatus Status,
    string? Detail,
    GpuFeatureState? After,
    IReadOnlyDictionary<string, GpuFeatureState>? RelatedAfter)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static GpuChangeRecord? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<GpuChangeRecord>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
