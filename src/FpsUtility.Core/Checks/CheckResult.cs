using System.Text.Json;
using System.Text.Json.Serialization;
using FpsUtility.Core.Facts;
using FpsUtility.Core.Facts.Serialization;
using FpsUtility.Core.Raw.Serialization;

namespace FpsUtility.Core.Checks;

/// <summary>A check's outcome (architecture §4.2, §6.3). Problem and Ok are outcomes; how sure the check is of them is <see cref="Certainty"/>.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<Verdict>))]
public enum Verdict
{
    Problem,
    Ok,

    /// <summary>Known facts establish that the check doesn't apply here (a laptop, no discrete GPU, a remote session).</summary>
    NotApplicable,

    /// <summary>"Couldn't check": what the check needs is missing, ambiguous, conflicting or insufficient. Never a problem.</summary>
    CannotDetermine,
}

/// <summary>How strongly the evidence supports a Problem or an Ok on this PC (§6.3). Not a fact state, not a probability.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<Certainty>))]
public enum Certainty
{
    Confirmed,
    Likely,
}

/// <summary>What a fact is for in one result. A fact has exactly one role per result.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<EvidenceRole>))]
public enum EvidenceRole
{
    /// <summary>A fact the verdict rests on. Known in every Problem and Ok; a missing one makes the result Couldn't check.</summary>
    Premise,

    /// <summary>A fact that decides whether the check applies. Known in every Not applicable.</summary>
    Applicability,

    /// <summary>A fact whose state lowered the certainty of a Problem or Ok. It may be missing: missing data only ever lowers certainty.</summary>
    Downgrade,
}

[JsonConverter(typeof(CamelCaseEnumConverter<TraceKind>))]
public enum TraceKind
{
    /// <summary>Why the check applies, or why it doesn't.</summary>
    Applicability,

    /// <summary>The comparison the verdict comes from, with the values compared.</summary>
    Comparison,

    /// <summary>A condition that lowers certainty to Likely.</summary>
    Downgrade,

    /// <summary>The fixture gate held certainty at Likely: no owner-labelled fixture covers the result's condition (§6.3, Decision B).</summary>
    Gate,
}

/// <summary>
/// One fact a result rests on (architecture §4.6): where it is in the normalised facts, its role, and a copy of its
/// state as the facts hold it: the value, basis and rule of a known fact, or the gap of a missing one, and its raw
/// evidence pointers. The fact's English <c>detail</c> is never copied; <see cref="FactPath"/> leads to it.
/// </summary>
public sealed class Evidence
{
    private Evidence(string factPath, EvidenceRole role, JsonElement? value, Basis? basis, string? rule, FactGap? missing, IReadOnlyList<string> from)
    {
        FactPath = factPath;
        Role = role;
        Value = value;
        Basis = basis;
        Rule = rule;
        Missing = missing;
        From = from;
    }

    /// <summary>A JSON Pointer into the normalised facts' JSON, e.g. <c>/displays/value/0/signalRefresh</c>.</summary>
    public string FactPath { get; }

    public EvidenceRole Role { get; }

    /// <summary>The known fact's value, exactly as the facts JSON writes it.</summary>
    public JsonElement? Value { get; }

    public Basis? Basis { get; }

    public string? Rule { get; }

    public FactGap? Missing { get; }

    /// <summary>The fact's raw evidence: JSON Pointers into the raw snapshot, copied verbatim.</summary>
    public IReadOnlyList<string> From { get; }

    [JsonIgnore]
    public bool IsKnown => Missing is null;

    public static Evidence Of<T>(string factPath, EvidenceRole role, Fact<T> fact)
    {
        Codes.FactPointer(factPath, nameof(factPath));
        ArgumentNullException.ThrowIfNull(fact);
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "Not an evidence role.");
        }

        return fact.IsKnown
            ? new Evidence(factPath, role, FactsJson.ValueElement(fact.Value!), fact.Basis, fact.Rule, null, [.. fact.From])
            : new Evidence(factPath, role, null, null, null, fact.Missing, [.. fact.From]);
    }
}

/// <summary>One machine-readable reasoning step (§6.3): a code and the typed values it used. No prose.</summary>
public sealed class TraceStep
{
    public TraceStep(string code, TraceKind kind, params IReadOnlyList<NamedValue> values)
    {
        Code = Codes.Kebab(code, nameof(code));
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a trace kind.");
        }

        Kind = kind;
        Values = CheckResult.Named(values, nameof(values));
    }

    public string Code { get; }

    public TraceKind Kind { get; }

    public IReadOnlyList<NamedValue> Values { get; }
}

/// <summary>Why a result is Couldn't check: a code and the facts it concerns (§4.2 <c>MissingInfo</c>).</summary>
public sealed class MissingInfo
{
    public MissingInfo(string code, params IReadOnlyList<string> facts)
    {
        Code = Codes.Kebab(code, nameof(code));
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Count == 0)
        {
            throw new ArgumentException("A Couldn't check names the facts it concerns.", nameof(facts));
        }

        Facts = [.. facts.Select(fact => Codes.FactPointer(fact, nameof(facts)))];
    }

    public string Code { get; }

    /// <summary>JSON Pointers into the normalised facts; each is also in the result's evidence, with its state.</summary>
    public IReadOnlyList<string> Facts { get; }
}

/// <summary>
/// A check's finding about one subject (architecture §4.2 as built in Milestone 3, §4.6). The constructor enforces the
/// confidence model's rules (§6.3), so a result that breaks them can't exist:
/// <list type="bullet">
/// <item>a certainty exactly for Problem and Ok;</item>
/// <item>Not applicable only from known applicability facts;</item>
/// <item>Couldn't check exactly when something is named missing, with each named fact in the evidence;</item>
/// <item>a Problem or Ok rests on known premises and a comparison; a missing fact can only lower its certainty;</item>
/// <item>Likely always names its downgrade or the gate; Confirmed carries neither, and names the condition the gate unlocked;</item>
/// <item>each fact has one role; no prose anywhere (every code is a token).</item>
/// </list>
/// There are no actions and no verification plan: neither is the check layer's (Decision A; §4.6).
/// </summary>
public sealed class CheckResult
{
    public CheckResult(
        string check,
        string findingCode,
        string subject,
        Verdict verdict,
        Certainty? certainty,
        string? condition,
        IReadOnlyList<Evidence> evidence,
        IReadOnlyList<TraceStep> trace,
        IReadOnlyList<NamedValue> parameters,
        IReadOnlyList<MissingInfo> missing)
    {
        Check = Codes.CheckId(check, nameof(check));
        FindingCode = Codes.Kebab(findingCode, nameof(findingCode));
        Subject = Codes.Kebab(subject, nameof(subject));
        if (!Enum.IsDefined(verdict) || (certainty is { } given && !Enum.IsDefined(given)))
        {
            throw new ArgumentOutOfRangeException(nameof(verdict), "Not a defined verdict or certainty.");
        }

        Evidence = NoNulls(evidence, nameof(evidence));
        Trace = NoNulls(trace, nameof(trace));
        Params = Named(parameters, nameof(parameters));
        Missing = NoNulls(missing, nameof(missing));
        Verdict = verdict;
        Certainty = certainty;
        Condition = condition;

        Validate();
    }

    /// <summary>The check's id, e.g. <c>display.refresh-below-max</c>.</summary>
    public string Check { get; }

    /// <summary>
    /// The aspect evaluated, the same code whatever the verdict (Decision C): <c>refresh-vs-highest-offered</c> is the
    /// question, and <see cref="Verdict"/> is the answer.
    /// </summary>
    public string FindingCode { get; }

    /// <summary>A snapshot-scoped subject id (<c>display-1</c>) or <c>machine</c>. Never an identity across scans.</summary>
    public string Subject { get; }

    public Verdict Verdict { get; }

    public Certainty? Certainty { get; }

    /// <summary>
    /// For a Problem or Ok with no downgrade: the condition key the fixture gate looked up (<c>problem/external</c>).
    /// Confirmed only when an owner-labelled fixture unlocked it; otherwise Likely with a gate step.
    /// </summary>
    public string? Condition { get; }

    public IReadOnlyList<Evidence> Evidence { get; }

    public IReadOnlyList<TraceStep> Trace { get; }

    /// <summary>Typed values for the future explanation text (§6.4 step 4), each taken from the evidence or a trace step.</summary>
    public IReadOnlyList<NamedValue> Params { get; }

    public IReadOnlyList<MissingInfo> Missing { get; }

    internal static IReadOnlyList<NamedValue> Named(IReadOnlyList<NamedValue> values, string what)
    {
        var copy = NoNulls(values, what);
        if (copy.Select(value => value.Name).Distinct(StringComparer.Ordinal).Count() != copy.Count)
        {
            throw new ArgumentException("Each name appears once.", what);
        }

        return copy;
    }

    private static IReadOnlyList<T> NoNulls<T>(IReadOnlyList<T> items, string what)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(items, what);
        return items.Any(item => item is null) ? throw new ArgumentException("No null entries.", what) : [.. items];
    }

    /// <summary>
    /// A basis Confirmed may rest on (§6.3). Every <see cref="Basis"/> member must be listed: a new one (Inferred,
    /// UserProvided) fails the build here until someone decides whether a Confirmed may rest on it.
    /// </summary>
#pragma warning disable CS8524 // Unnamed values can't occur; leaving out the discard makes a new named member a build error (CS8509).
    private static bool SupportsConfirmed(Basis basis) => basis switch
    {
        Facts.Basis.Authoritative => true,
        Facts.Basis.Declared => true,
    };
#pragma warning restore CS8524

    private void Validate()
    {
        var decided = Verdict is Verdict.Problem or Verdict.Ok;
        if (decided != Certainty.HasValue)
        {
            throw new ArgumentException("A Problem or an Ok has a certainty; Not applicable and Couldn't check have none.");
        }

        if (Evidence.Select(item => item.FactPath).Distinct(StringComparer.Ordinal).Count() != Evidence.Count)
        {
            throw new ArgumentException("A fact has exactly one role in a result.");
        }

        if ((Verdict == Verdict.CannotDetermine) != (Missing.Count > 0))
        {
            throw new ArgumentException("A result is Couldn't check exactly when it names what is missing.");
        }

        var downgradeEvidence = Evidence.Any(item => item.Role == EvidenceRole.Downgrade);
        var downgraded = Trace.Any(step => step.Kind == TraceKind.Downgrade);
        var gated = Trace.Any(step => step.Kind == TraceKind.Gate);
        if (!decided && (downgradeEvidence || downgraded || gated || Condition is not null))
        {
            throw new ArgumentException("Only a Problem or an Ok has a certainty to lower, a gate or a condition.");
        }

        switch (Verdict)
        {
            case Verdict.Problem or Verdict.Ok:
                ValidateDecided(downgradeEvidence, downgraded, gated);
                break;

            case Verdict.NotApplicable:
                if (Evidence.Count == 0 || Evidence.Any(item => item.Role != EvidenceRole.Applicability || !item.IsKnown))
                {
                    throw new ArgumentException("Not applicable rests only on known applicability facts, and on at least one.");
                }

                if (!Trace.Any(step => step.Kind == TraceKind.Applicability) || Trace.Any(step => step.Kind != TraceKind.Applicability))
                {
                    throw new ArgumentException("Not applicable records why, as applicability steps only.");
                }

                break;

            case Verdict.CannotDetermine:
                var cited = Evidence.Select(item => item.FactPath).ToHashSet(StringComparer.Ordinal);
                if (Missing.SelectMany(info => info.Facts).Any(fact => !cited.Contains(fact)))
                {
                    throw new ArgumentException("Every fact a Couldn't check names is in its evidence, with its state.");
                }

                break;
        }
    }

    private void ValidateDecided(bool downgradeEvidence, bool downgraded, bool gated)
    {
        var premises = Evidence.Where(item => item.Role == EvidenceRole.Premise).ToList();
        if (premises.Count == 0)
        {
            throw new ArgumentException("A Problem or an Ok rests on at least one premise fact.");
        }

        if (Evidence.Any(item => item.Role != EvidenceRole.Downgrade && !item.IsKnown))
        {
            throw new ArgumentException("A missing fact can only lower certainty or make the result Couldn't check, never support a Problem or an Ok.");
        }

        if (!Trace.Any(step => step.Kind == TraceKind.Comparison))
        {
            throw new ArgumentException("A Problem or an Ok records the comparison it comes from.");
        }

        if (downgradeEvidence && !downgraded)
        {
            throw new ArgumentException("A downgrade fact names the downgrade it caused.");
        }

        if (gated && downgraded)
        {
            throw new ArgumentException("The gate is consulted only when no downgrade applies.");
        }

        if ((Condition is null) != downgraded)
        {
            throw new ArgumentException("A condition is named exactly when no downgrade applies: it is what the fixture gate decides on.");
        }

        if (Condition is not null)
        {
            Codes.Condition(Condition, JsonNamingPolicy.CamelCase.ConvertName(Verdict.ToString()), nameof(Condition));
        }

        if (Certainty == Checks.Certainty.Likely && !downgraded && !gated)
        {
            throw new ArgumentException("Likely names its downgrade or the gate that held it.");
        }

        if (Certainty == Checks.Certainty.Confirmed)
        {
            if (downgraded || gated || downgradeEvidence)
            {
                throw new ArgumentException("Confirmed carries no downgrade and no unresolved gate.");
            }

            if (!Evidence.All(item => SupportsConfirmed(item.Basis!.Value)))
            {
                throw new ArgumentException("Confirmed rests only on facts whose basis supports it.");
            }
        }
    }
}
