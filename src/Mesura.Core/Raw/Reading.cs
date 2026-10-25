namespace Mesura.Core.Raw;

/// <summary>
/// One value as the platform reported it, or why it could not be read. Exactly one state holds:
/// <list type="bullet">
/// <item><b>known</b>: <see cref="Value"/> is set and <see cref="Missing"/> is null;</item>
/// <item><b>missing</b>: <see cref="Missing"/> and <see cref="Detail"/> are set and <see cref="Value"/> is default.</item>
/// </list>
/// <see cref="Native"/> is the platform's own code when the value is a lossless re-encoding of it
/// (architecture §4.4 rule 1). It is kept in both states, so an unrecognised code is never lost.
/// JSON (<c>Serialization.ReadingJsonConverter</c>): <c>{"value": …, "native": …}</c> or
/// <c>{"native": …, "missing": "…", "detail": "…"}</c>.
/// </summary>
public sealed record Reading<T>
{
    public Reading(T? value = default, long? native = null, MissingReason? missing = null, string? detail = null)
    {
        if (missing is null)
        {
            if (value is null)
            {
                throw new ArgumentException("A known reading needs a value.", nameof(value));
            }

            if (detail is not null)
            {
                throw new ArgumentException("A known reading has no detail.", nameof(detail));
            }
        }
        else
        {
            if (!EqualityComparer<T>.Default.Equals(value, default))
            {
                throw new ArgumentException("A missing reading has no value.", nameof(value));
            }

            if (string.IsNullOrWhiteSpace(detail))
            {
                throw new ArgumentException("A missing reading says why in its detail.", nameof(detail));
            }
        }

        Value = value;
        Native = native;
        Missing = missing;
        Detail = detail;
    }

    public T? Value { get; }

    public long? Native { get; }

    public MissingReason? Missing { get; }

    public string? Detail { get; }

    public bool IsKnown => Missing is null;
}

/// <summary>Factory methods for <see cref="Reading{T}"/>.</summary>
public static class Reading
{
    public static Reading<T> Known<T>(T value, long? native = null) => new(value, native, null, null);

    public static Reading<T> Missing<T>(MissingReason reason, string detail, long? native = null) =>
        new(default, native, reason, detail);
}
