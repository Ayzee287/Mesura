using System.Globalization;
using System.Text;

namespace Mesura.Core.TrackA;

/// <summary>
/// The Track A coverage report (architecture §4.7): a descriptive inventory of the accepted cohort, read from the index.
/// Counts of machines, never a score, and nothing about users: the cohort is a handful of known PCs.
/// </summary>
public static class CoverageReport
{
    public static string Render(CohortIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);

        var text = new StringBuilder();
        void Line(string line = "") => text.Append(line).Append('\n');
        int Count(string dimension, string value) => index.Summary.FirstOrDefault(tally => tally.Dimension == dimension && tally.Value == value)?.Machines ?? 0;
        string Counts(string dimension) => string.Join(" - ", index.Summary.Where(tally => tally.Dimension == dimension).Select(tally => $"{tally.Value} {tally.Machines}"));

        Line("TRACK A COVERAGE (accepted sessions only; an inventory, not a score)");
        Line();
        Line(string.Create(CultureInfo.InvariantCulture, $"Machines: {Count("machines", "enrolled")}{(index.Machines.Count > 0 ? " (" + string.Join(", ", index.Machines.Select(machine => machine.CohortId)) + ")" : "")}"));
        Line($"Discrete GPU: {Counts("discreteGpu")}");
        Line($"Windows: {Counts("windows")}");
        Line($"Form factor, firmware-declared: {Counts("formFactorDeclared")}");
        Line($"Chassis, operator-observed: {Counts("chassisObserved")}");
        Line($"Steam: {Counts("steam")}");
        Line($"Disk media: {Counts("diskMedia")}");
        Line($"Disk bus: {Counts("diskBus")}");
        Line($"Displays: {Counts("display")}");
        Line();
        Line("Scenarios (machines per state):");
        foreach (var scenario in ScenarioCatalog.All)
        {
            Line($"  {scenario.Id,-22} {Counts("scenario:" + scenario.Id)}");
        }

        Line();
        Line("Checks with real-hardware paths (verdict, certainty, condition, codes: machines):");
        var paths = index.Machines
            .SelectMany(machine => machine.CheckPaths.Select(path => (path.Key, machine.CohortId)))
            .GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToList();
        if (paths.Count == 0)
        {
            Line("  none yet");
        }

        foreach (var path in paths)
        {
            Line($"  {path.Key}: {string.Join(", ", path.Select(pair => pair.CohortId).Distinct(StringComparer.Ordinal))}");
        }

        Line("  Checks 3-8 are not implemented, so no machine can cover them yet.");
        Line();
        Line("First cohort targets (one machine may meet several):");
        foreach (var target in CohortIndex.Targets)
        {
            var met = index.Machines.Where(machine => machine.Targets.Single(result => result.Target == target).Met).Select(machine => machine.CohortId).ToList();
            Line($"  {target,-26} {(met.Count == 0 ? "missing" : string.Join(", ", met))}");
        }

        var missing = index.Machines
            .Select(machine => (machine.CohortId, Missing: machine.Scenarios.Where(coverage => coverage.State != CoverageState.Completed).Select(coverage => $"{coverage.Scenario} ({CohortIndex.Name(coverage.State)})").ToList()))
            .Where(pair => pair.Missing.Count > 0)
            .ToList();
        if (missing.Count > 0)
        {
            Line();
            Line("Scenarios not completed, per machine:");
            foreach (var (cohortId, scenarios) in missing)
            {
                Line($"  {cohortId}: {string.Join(", ", scenarios)}");
            }
        }

        var warnings = index.Machines.Where(machine => machine.Warnings.Count > 0).ToList();
        if (warnings.Count > 0)
        {
            Line();
            Line("Warnings:");
            foreach (var machine in warnings)
            {
                Line($"  {machine.CohortId}: {string.Join(", ", machine.Warnings)} (sessions of one cohort id show different machine models: a mistyped id, or changed hardware)");
            }
        }

        return text.ToString();
    }
}
