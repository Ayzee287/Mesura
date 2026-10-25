using System.Text.Json.Serialization;
using Mesura.Core.Facts;
using Mesura.Core.Raw.Serialization;

namespace Mesura.Core.Checks;

/// <summary>
/// A diagnostic check (architecture §6.1): a pure function of the normalised facts. It reads facts only, never the raw
/// snapshot or the platform, and it writes no text. §6.1's <c>Requires</c> (raw sections for a scan plan) is not built:
/// a check names facts, and a later plan can derive its sources from the facts' own provenance.
/// </summary>
public interface ICheck
{
    /// <summary>The check's id, e.g. <c>display.refresh-below-max</c>.</summary>
    string Id { get; }

    /// <summary>One result per subject (per display, or one for the machine), in the facts' order.</summary>
    IReadOnlyList<CheckResult> Evaluate(NormalizedSnapshot facts, FixtureGate gate);
}

/// <summary>The product document's eight checks, by the ids §6.5 gives them, in its order.</summary>
public static class CheckIds
{
    public const string RefreshBelowMax = "display.refresh-below-max";
    public const string OnIntegratedGpu = "display.on-integrated-gpu";
    public const string LinkLimitsRefresh = "display.link-limits-refresh";
    public const string BelowRatedSpeed = "memory.below-rated-speed";
    public const string SingleChannel = "memory.single-channel";
    public const string PcieLinkReduced = "gpu.pcie-link-reduced";
    public const string SustainedThrottling = "cpu.sustained-throttling";
    public const string GameDrives = "storage.game-drives";

    public static IReadOnlyList<string> All { get; } =
        [RefreshBelowMax, OnIntegratedGpu, LinkLimitsRefresh, BelowRatedSpeed, SingleChannel, PcieLinkReduced, SustainedThrottling, GameDrives];
}

/// <summary>Whether a check ran. Coverage is what stops a check that didn't run from reading as a clean result (§5). It holds no counts.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<CoverageStatus>))]
public enum CoverageStatus
{
    Ran,
    NotImplemented,
}

public sealed record CoverageEntry(string Check, CoverageStatus Status);

/// <summary>
/// The results of the implemented checks on one normalised snapshot. <see cref="Evidence.FactPath"/> points into those
/// facts, so the facts and raw schema versions they refer to are recorded, and so is the gate that decided certainty:
/// the same snapshot can read Likely on this build and Confirmed on a later one with more labelled fixtures.
/// </summary>
public sealed class CheckResults
{
    public const int CurrentSchemaVersion = 1;

    public CheckResults(
        int factsSchemaVersion,
        int rawSchemaVersion,
        DateTimeOffset capturedAtUtc,
        IReadOnlyList<GatedCondition> unlockedConditions,
        IReadOnlyList<CoverageEntry> coverage,
        IReadOnlyList<CheckResult> results)
    {
        ArgumentNullException.ThrowIfNull(unlockedConditions);
        ArgumentNullException.ThrowIfNull(coverage);
        ArgumentNullException.ThrowIfNull(results);
        if (results.DistinctBy(result => (result.Check, result.Subject, result.FindingCode)).Count() != results.Count)
        {
            throw new ArgumentException("One result per check, subject and aspect.", nameof(results));
        }

        FactsSchemaVersion = factsSchemaVersion;
        RawSchemaVersion = rawSchemaVersion;
        CapturedAtUtc = capturedAtUtc;
        UnlockedConditions = [.. unlockedConditions];
        Coverage = [.. coverage];
        Results = [.. results];
    }

    public int SchemaVersion => CurrentSchemaVersion;

    public int FactsSchemaVersion { get; }

    public int RawSchemaVersion { get; }

    public DateTimeOffset CapturedAtUtc { get; }

    /// <summary>The fixture gate's unlocked conditions when the checks ran (none today: Decision B).</summary>
    public IReadOnlyList<GatedCondition> UnlockedConditions { get; }

    public IReadOnlyList<CoverageEntry> Coverage { get; }

    public IReadOnlyList<CheckResult> Results { get; }
}

/// <summary>Runs the implemented checks (explicit list, no discovery: §6.2) on one normalised snapshot.</summary>
public static class CheckRunner
{
    /// <summary>Milestone 3A: checks 1 and 2. The others are listed in the coverage as not implemented.</summary>
    public static IReadOnlyList<ICheck> Implemented { get; } = [new RefreshBelowMaxCheck(), new OnIntegratedGpuCheck()];

    /// <summary>
    /// A check that throws is a defect and isn't caught here, as in the normaliser: it surfaces rather than turning into
    /// a Couldn't check. (§10's engine wrapper, which keeps one check's defect from stopping the others, arrives with the
    /// App.)
    /// </summary>
    public static CheckResults Run(NormalizedSnapshot facts, FixtureGate gate)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(gate);

        var results = new List<CheckResult>();
        foreach (var check in Implemented)
        {
            var produced = check.Evaluate(facts, gate);
            if (produced.Count == 0 || produced.Any(result => result.Check != check.Id))
            {
                throw new InvalidOperationException($"Check {check.Id} returned no result, or a result of another check.");
            }

            results.AddRange(produced);
        }

        var coverage = CheckIds.All
            .Select(id => new CoverageEntry(id, Implemented.Any(check => check.Id == id) ? CoverageStatus.Ran : CoverageStatus.NotImplemented))
            .ToList();

        return new CheckResults(facts.SchemaVersion, facts.RawSchemaVersion, facts.CapturedAtUtc, gate.Unlocked, coverage, results);
    }
}
