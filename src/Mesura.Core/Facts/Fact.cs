using System.Text.Json.Serialization;
using Mesura.Core.Raw.Serialization;

namespace Mesura.Core.Facts;

/// <summary>
/// Where a known fact's value comes from (architecture §4.2). A check's confidence rules read it (§6.3): a
/// verdict can be Confirmed only when every fact it rests on is Authoritative or Declared.
/// </summary>
/// <remarks>
/// §4.2 also names <c>Inferred</c> (a decode table or a name pattern) and <c>UserProvided</c>. Nothing produces
/// them yet; they arrive with their first producer (the RAM part-number decode and user entry, step 3, D004).
/// </remarks>
[JsonConverter(typeof(CamelCaseEnumConverter<Basis>))]
public enum Basis
{
    /// <summary>The operating system reports it about its own state or its own device stack (display configuration, adapters, PCI properties, storage, power, OS version).</summary>
    Authoritative,

    /// <summary>
    /// Firmware, a device, or the software that owns the data declares it about itself in a structure the product
    /// reads (SMBIOS, EDID, Steam's own files). True as a declaration; the declarer can be wrong or a placeholder.
    /// </summary>
    Declared,
}

/// <summary>
/// Why a fact is missing. The first eight mirror the raw snapshot's reasons (§4.2), so a gap in the evidence
/// keeps its cause. The last three are the normaliser's own, for relationships it could not establish.
/// None of them means "absent from the machine": a fact that the machine has no such thing is a known fact
/// (an empty list, <c>false</c>, <see cref="SteamRegistration.NotRegistered"/>).
/// </summary>
[JsonConverter(typeof(CamelCaseEnumConverter<FactGap>))]
public enum FactGap
{
    /// <summary>The source answered and has no such value (no monitor name, no link property, no module in the slot).</summary>
    NotPresent,

    /// <summary>The platform, its version or the driver doesn't offer this value.</summary>
    NotSupported,

    /// <summary>The value needs rights the collecting process didn't have.</summary>
    AccessDenied,

    /// <summary>The call or the whole source failed.</summary>
    SourceFailed,

    /// <summary>The source didn't answer in time.</summary>
    TimedOut,

    /// <summary>A value was reported, and it is outside what the platform or the specification defines (a 0 Hz refresh, a width of 0 lanes).</summary>
    Implausible,

    /// <summary>A value or structure couldn't be decoded.</summary>
    ParseFailed,

    /// <summary>A value was reported and deliberately not kept, because it is private (§13).</summary>
    Withheld,

    /// <summary>A relationship: nothing in the snapshot matches (an adapter with no PCI device of its identity).</summary>
    Unmatched,

    /// <summary>A relationship: more than one candidate matches (two identical GPUs), so none is claimed.</summary>
    Ambiguous,

    /// <summary>A relationship: two independent pieces of evidence disagree (a disk's number and its location).</summary>
    Conflicting,
}

/// <summary>
/// A normalised fact (architecture §4.2): either <b>known</b>, with its value, its <see cref="Basis"/> and, when it
/// was derived rather than observed, the <see cref="Rule"/> that derived it; or <b>missing</b>, with a
/// <see cref="FactGap"/> and a detail. Either way <see cref="From"/> says which raw evidence it rests on, as JSON
/// Pointers (RFC 6901) into the raw snapshot the facts were normalised from.
/// </summary>
/// <remarks>
/// A fact never says Confirmed, Likely, Couldn't check or Not applicable: those are a check's verdicts (§6.3),
/// made from facts under the check's own rules.
/// </remarks>
public sealed record Fact<T>
{
    internal Fact(T? value, Basis? basis, string? rule, FactGap? missing, string? detail, IReadOnlyList<string> from)
    {
        ArgumentNullException.ThrowIfNull(from);
        if (from.Count == 0 || from.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("A fact names the raw evidence it rests on.", nameof(from));
        }

        if (missing is null)
        {
            if (value is null || basis is null)
            {
                throw new ArgumentException("A known fact has a value and a basis.");
            }

            if (detail is not null || (rule is not null && string.IsNullOrWhiteSpace(rule)))
            {
                throw new ArgumentException("A known fact has no detail, and a derived one names its rule.");
            }
        }
        else if (!EqualityComparer<T>.Default.Equals(value, default) || basis is not null || rule is not null || string.IsNullOrWhiteSpace(detail))
        {
            throw new ArgumentException("A missing fact has no value, basis or rule, and says why in its detail.");
        }

        Value = value;
        Basis = basis;
        Rule = rule;
        Missing = missing;
        Detail = detail;
        From = from;
    }

    public T? Value { get; }

    public Basis? Basis { get; }

    /// <summary>The derivation rule (<see cref="Rules"/>) for a derived fact; <c>null</c> for a direct observation.</summary>
    public string? Rule { get; }

    public FactGap? Missing { get; }

    public string? Detail { get; }

    /// <summary>JSON Pointers into the raw snapshot: the evidence a known fact rests on, or where a missing one is missing from.</summary>
    public IReadOnlyList<string> From { get; }

    public bool IsKnown => Missing is null;

    public bool IsDerived => Rule is not null;
}

/// <summary>Factory methods for <see cref="Fact{T}"/>.</summary>
public static class Fact
{
    /// <summary>A value the source reported, possibly re-expressed losslessly (a type, a unit), with no derivation.</summary>
    public static Fact<T> Observed<T>(T value, Basis basis, string from) =>
        new(value, basis, null, null, null, [from]);

    /// <summary>A value computed from evidence by a named, deterministic rule (a join, a structure parse).</summary>
    public static Fact<T> Derived<T>(T value, Basis basis, string rule, params IEnumerable<string> from) =>
        new(value, basis, rule, null, null, [.. from]);

    public static Fact<T> Missing<T>(FactGap gap, string detail, params IEnumerable<string> from) =>
        new(default, null, null, gap, detail, [.. from]);
}
