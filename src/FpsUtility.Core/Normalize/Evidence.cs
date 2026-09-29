using FpsUtility.Core.Facts;
using FpsUtility.Core.Raw;

namespace FpsUtility.Core.Normalize;

/// <summary>A raw source that was <c>unavailable</c>: every fact that needs it is missing for the source's reason.</summary>
internal readonly record struct UnavailableSource(string Section, FactGap Gap, string Detail)
{
    public string Pointer => $"/sources/{Section}";

    public Fact<T> Fact<T>() => Facts.Fact.Missing<T>(Gap, $"The {Section} source is unavailable: {Detail}", Pointer);
}

/// <summary>How raw evidence becomes facts: the mappings every normaliser shares, so each one reads the same way.</summary>
internal static class Evidence
{
    public static FactGap GapOf(MissingReason reason) => reason switch
    {
        MissingReason.NotPresent => FactGap.NotPresent,
        MissingReason.NotSupported => FactGap.NotSupported,
        MissingReason.AccessDenied => FactGap.AccessDenied,
        MissingReason.SourceFailed => FactGap.SourceFailed,
        MissingReason.TimedOut => FactGap.TimedOut,
        MissingReason.Implausible => FactGap.Implausible,
        MissingReason.ParseFailed => FactGap.ParseFailed,
        MissingReason.Withheld => FactGap.Withheld,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "A raw reason with no fact counterpart."),
    };

    /// <summary>The source's data, or why there is none.</summary>
    public static bool TryData<TData>(SourceEntry<TData> entry, string section, out TData data, out UnavailableSource unavailable)
        where TData : class
    {
        if (entry.Data is { } present)
        {
            data = present;
            unavailable = default;
            return true;
        }

        data = null!;
        unavailable = new UnavailableSource(section, GapOf(entry.Reason!.Value), entry.Detail!);
        return false;
    }

    /// <summary>A raw reading as a direct observation, or a missing fact with the reading's own reason and detail.</summary>
    public static Fact<T> Observed<T>(Reading<T> reading, Basis basis, string pointer) =>
        reading.IsKnown
            ? Fact.Observed(reading.Value!, basis, pointer)
            : Fact.Missing<T>(GapOf(reading.Missing!.Value), reading.Detail!, pointer);

    /// <summary>A known fact whose value falls outside what the platform defines becomes <see cref="FactGap.Implausible"/>, keeping its evidence.</summary>
    public static Fact<T> Plausible<T>(Fact<T> fact, Func<T, bool> isPlausible, Func<T, string> why) =>
        fact.IsKnown && !isPlausible(fact.Value!)
            ? Fact.Missing<T>(FactGap.Implausible, why(fact.Value!), fact.From)
            : fact;

    /// <summary>The same fact with its value re-expressed losslessly (a record, a type); a missing fact stays missing for the same reason.</summary>
    public static Fact<TOut> Map<TIn, TOut>(Fact<TIn> fact, Func<TIn, TOut> map) =>
        fact.IsKnown
            ? new Fact<TOut>(map(fact.Value!), fact.Basis, fact.Rule, null, null, fact.From)
            : Fact.Missing<TOut>(fact.Missing!.Value, fact.Detail!, fact.From);

    /// <summary>A missing fact with the same reason, detail and evidence as another, for a value that depends on it.</summary>
    public static Fact<TOut> MissingLike<TIn, TOut>(Fact<TIn> cause) =>
        Fact.Missing<TOut>(cause.Missing!.Value, cause.Detail!, cause.From);
}
