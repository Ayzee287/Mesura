namespace Mesura.Core.Raw;

/// <summary>
/// Puts every list in a snapshot into one fixed order, so the JSON never depends on the order in
/// which a platform or parallel collectors returned items (architecture §4.4, "Deterministic").
/// Model constructors call it, which makes the order a property of the model, not a collector habit.
/// </summary>
internal static class Canonical
{
    /// <summary>
    /// Stable sort by a full key; ties keep their relative order (identical keys only occur for identical items).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The list holds a null element. The model has no null list elements, so a snapshot that holds one is
    /// malformed; <see cref="Serialization.SnapshotJson"/> reports this as a <see cref="Serialization.SnapshotFormatException"/>.
    /// </exception>
    public static IReadOnlyList<T> Sort<T>(IEnumerable<T> items, Comparison<T> comparison)
    {
        var array = NoNulls(items);
        return array.Order(Comparer<T>.Create(comparison)).ToArray();
    }

    public static Reading<IReadOnlyList<T>> Sort<T>(Reading<IReadOnlyList<T>> reading, Comparison<T> comparison)
    {
        ArgumentNullException.ThrowIfNull(reading);
        return reading.IsKnown
            ? Reading.Known(Sort(reading.Value!, comparison), reading.Native)
            : reading;
    }

    /// <summary>Culture-independent string order.</summary>
    public static int Ordinal(string? a, string? b) => string.CompareOrdinal(a, b);

    private static T[] NoNulls<T>(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var array = items.ToArray();
        if (Array.Exists(array, item => item is null))
        {
            throw new ArgumentException($"A list of {typeof(T).Name} has a null element.", nameof(items));
        }

        return array;
    }
}
