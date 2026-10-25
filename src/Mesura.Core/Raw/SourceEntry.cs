using System.Text.Json.Serialization;
using Mesura.Core.Raw.Serialization;

namespace Mesura.Core.Raw;

[JsonConverter(typeof(CamelCaseEnumConverter<SourceStatus>))]
public enum SourceStatus
{
    Ok,
    Unavailable,
}

/// <summary>
/// One raw source in a snapshot (architecture §4.4): either <c>ok</c> with its <see cref="Data"/>,
/// or <c>unavailable</c> with a <see cref="Reason"/> and <see cref="Detail"/>. A failed source never
/// removes or blocks the others (§10).
/// </summary>
public sealed record SourceEntry<T>
    where T : class
{
    [JsonConstructor]
    public SourceEntry(SourceStatus status, string source, long durationMs, MissingReason? reason = null, string? detail = null, T? data = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentOutOfRangeException.ThrowIfNegative(durationMs);

        switch (status)
        {
            case SourceStatus.Ok when data is null || reason is not null || detail is not null:
                throw new ArgumentException("An ok source has data and no reason or detail.");
            case SourceStatus.Unavailable when data is not null || reason is null || string.IsNullOrWhiteSpace(detail):
                throw new ArgumentException("An unavailable source has a reason and a detail, and no data.");
            case SourceStatus.Ok or SourceStatus.Unavailable:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown source status.");
        }

        Status = status;
        Reason = reason;
        Detail = detail;
        Source = source;
        DurationMs = durationMs;
        Data = data;
    }

    public SourceStatus Status { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MissingReason? Reason { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; }

    /// <summary>The platform API(s) read, e.g. <c>win32.QueryDisplayConfig</c>. Native values are read against it.</summary>
    public string Source { get; }

    /// <summary>How long collection took. Run-dependent, so it may differ between scans (§4.4).</summary>
    public long DurationMs { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public T? Data { get; }
}

public static class SourceEntry
{
    public static SourceEntry<T> Ok<T>(string source, long durationMs, T data)
        where T : class => new(SourceStatus.Ok, source, durationMs, data: data);

    public static SourceEntry<T> Unavailable<T>(string source, long durationMs, MissingReason reason, string detail)
        where T : class => new(SourceStatus.Unavailable, source, durationMs, reason, detail);
}
