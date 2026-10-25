using System.Globalization;
using System.Text;

namespace Mesura.Core.TrackA;

/// <summary>
/// The <c>README.md</c> of an accepted session in the corpus: a person's view of the JSON beside it, generated at
/// acceptance from those files and fixed text only. The JSON files are the record; this page adds nothing to them.
/// </summary>
public static class SessionReadme
{
    public static string Render(AcceptedSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var (manifest, observations, acceptance) = (session.Manifest, session.Observations, session.Acceptance);
        var text = new StringBuilder();
        void Line(string line = "") => text.Append(line).Append('\n');
        static string Code(string value) => "`" + value + "`";

        Line($"# {manifest.CohortId} - {manifest.SessionId}");
        Line();
        Line("An accepted Track A cohort fixture: engineering validation of a known PC, enrolled by hand and scrubbed. Not telemetry.");
        Line("Generated at acceptance; the JSON files are the record. See `track-a/README.md` for the format.");
        Line();
        Line("| | |");
        Line("|---|---|");
        Line($"| Scenario | {Code(manifest.Scenario.Id)}{(manifest.Scenario.Step is { } step ? " - step " + Code(step) : "")}{(manifest.Scenario.Series is { } series ? " - series " + Code(series.Id) : "")} |");
        Line($"| Status | {Code(CohortIndex.Name(manifest.Scenario.Status))} (operator declared {Code(CohortIndex.Name(manifest.Scenario.OperatorDisposition))}) |");
        Line($"| Captured | {manifest.CapturedAtUtc.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture)} |");
        Line($"| Produced by | {manifest.Producer.Name} {manifest.Producer.Version}, commit {Code(manifest.Producer.Commit ?? "none")} |");
        Line($"| Accepted | {acceptance.AcceptedOn}, with commit {Code(acceptance.AcceptedWith.Commit ?? "none")} |");
        Line($"| Windows | {CohortIndex.Name(manifest.Platform.Family)}, build {manifest.Platform.Build}{(manifest.Platform.UpdateRevision is { } ubr ? "." + ubr.ToString(CultureInfo.InvariantCulture) : "")}{(manifest.Platform.Elevated ? ", elevated" : ", standard user")} |");
        Line($"| Sources unavailable | {Join(manifest.Sources.Where(source => source.Status != Raw.SourceStatus.Ok).Select(source => $"{source.Source} ({(source.Reason is { } reason ? CohortIndex.Name(reason) : "?")})"))} |");
        Line($"| Checks run | {Join(manifest.Checks.Ran.Select(Code))}; not implemented: {manifest.Checks.NotImplemented.Count} |");
        Line($"| Replay at acceptance | facts {CohortIndex.Name(acceptance.Replay.Facts)}, results {CohortIndex.Name(acceptance.Replay.Results)} ({(acceptance.Replay.SameCommit ? "same commit" : "another commit")}) |");
        Line();
        Line("## Check paths (this capture)");
        Line();
        foreach (var path in acceptance.CheckPaths)
        {
            Line($"- {Code(path.Key)}");
        }

        Line();
        Line("## Operator observations (declared, never machine facts)");
        Line();
        if (observations.Observations.Count == 0)
        {
            Line("None.");
        }

        foreach (var observation in observations.Observations)
        {
            var value = observation.Value switch
            {
                ObservedText item => item.Text,
                ObservedChoices item => string.Join(", ", item.Items),
                ObservedNumber item => item.Number.ToString(CultureInfo.InvariantCulture),
                _ => "(" + CohortIndex.Name(observation.State) + ")",
            };
            Line($"- {Code(observation.Field)} ({CohortIndex.Name(observation.Role)}{(observation.Tool is { } tool ? ", " + tool : "")}): {value}");
        }

        Line();
        Line("## Privacy");
        Line();
        Line($"- Scrubbed by the product's pseudonymiser ({manifest.Privacy.IdsReplaced} opaque ids replaced); checked on the captured PC against {manifest.Privacy.IdentifierKindsChecked} kinds of its own identifiers and {manifest.Privacy.OpaqueIdsChecked} of the scan's own opaque ids; verdict {Code(CohortIndex.Name(manifest.Privacy.Verdict))}.");
        if (manifest.Privacy.UncheckedIdentifierKinds.Count > 0)
        {
            Line($"- Not checkable on that PC, acknowledged at acceptance: {Join(manifest.Privacy.UncheckedIdentifierKinds)}.");
        }

        Line($"- The identifier-free audit passed on the captured PC and again at acceptance. A person read the {acceptance.Review.AnswersShown} answer(s) and the files and confirmed nothing personal is in them.");
        Line($"- Game names: {CohortIndex.Name(acceptance.Consent.GameNames)}.");
        Line($"- External evidence: {(acceptance.Evidence.Count == 0 ? "none" : Join(acceptance.Evidence.Select(item => $"{Code(item.File)} {CohortIndex.Name(item.Decision)}")))}. Evidence files never enter git; the corpus keeps their names, hashes and decisions.");

        return text.ToString();
    }

    private static string Join(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count == 0 ? "none" : string.Join(", ", list);
    }
}
