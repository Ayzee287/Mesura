using System.Text.Json;
using Mesura.Core.Facts;

namespace Mesura.Core.Checks;

/// <summary>
/// Collects one result's evidence, trace, parameters and gaps in the order a check reasons, then ends in exactly one
/// outcome. <see cref="CheckResult"/>'s constructor enforces the rules; this only makes a check read as its contract.
/// </summary>
internal sealed class ResultBuilder(string check, string findingCode, string subject)
{
    /// <summary>The gate step: no owner-labelled fixture covers the result's condition (Decision B).</summary>
    public const string UnvalidatedCondition = "unvalidated-condition";

    public const string Machine = "machine";

    private readonly List<Evidence> evidence = [];
    private readonly List<TraceStep> trace = [];
    private readonly List<NamedValue> parameters = [];
    private readonly List<MissingInfo> missing = [];

    public bool IsMissingSomething => missing.Count > 0;

    public ResultBuilder Add<T>(EvidenceRole role, string path, Fact<T> fact)
    {
        evidence.Add(Evidence.Of(path, role, fact));
        return this;
    }

    /// <summary>A fact the result needs: recorded with its role, and, when it is missing, as what is missing.</summary>
    /// <returns>Whether the fact is known.</returns>
    public bool Require<T>(EvidenceRole role, string path, Fact<T> fact, string missingCode)
    {
        Add(role, path, fact);
        if (!fact.IsKnown)
        {
            missing.Add(new MissingInfo(missingCode, path));
        }

        return fact.IsKnown;
    }

    public ResultBuilder Step(TraceKind kind, string code, params IReadOnlyList<NamedValue> values)
    {
        trace.Add(new TraceStep(code, kind, values));
        return this;
    }

    /// <summary>A fact whose state lowers certainty, and the downgrade it causes.</summary>
    public ResultBuilder Downgrade<T>(string path, Fact<T> fact, string code, params IReadOnlyList<NamedValue> values) =>
        Add(EvidenceRole.Downgrade, path, fact).Step(TraceKind.Downgrade, code, values);

    /// <summary>A reason the check couldn't decide that isn't one missing fact (no qualifying mode, conflicting values).</summary>
    public ResultBuilder Missing(string code, params IReadOnlyList<string> facts)
    {
        missing.Add(new MissingInfo(code, facts));
        return this;
    }

    public ResultBuilder Param(string name, CheckValue value)
    {
        parameters.Add(new NamedValue(name, value));
        return this;
    }

    public CheckResult NotApplicable() => Build(Verdict.NotApplicable, null, null);

    public CheckResult CannotDetermine() => Build(Verdict.CannotDetermine, null, null);

    /// <summary>
    /// A Problem or an Ok. Any downgrade makes it Likely. Otherwise the gate decides on
    /// <c>{verdict}/{conditions}</c>: Confirmed only when an owner-labelled fixture unlocked that key, else Likely with a
    /// gate step. Nothing here can raise certainty.
    /// </summary>
    public CheckResult Decide(Verdict verdict, string conditions, FixtureGate gate)
    {
        if (trace.Any(step => step.Kind == TraceKind.Downgrade))
        {
            return Build(verdict, Certainty.Likely, null);
        }

        var key = $"{JsonNamingPolicy.CamelCase.ConvertName(verdict.ToString())}/{conditions}";
        if (gate.Unlocks(check, key))
        {
            return Build(verdict, Certainty.Confirmed, key);
        }

        Step(TraceKind.Gate, UnvalidatedCondition);
        return Build(verdict, Certainty.Likely, key);
    }

    private CheckResult Build(Verdict verdict, Certainty? certainty, string? condition) =>
        new(check, findingCode, subject, verdict, certainty, condition, evidence, trace, parameters, missing); // a gap left behind by a decided result throws
}
