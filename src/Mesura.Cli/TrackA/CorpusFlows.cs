using System.Text;
using Mesura.Core.Privacy;
using Mesura.Core.Raw;
using Mesura.Core.TrackA;
using Mesura.Platform.Windows;

namespace Mesura.Cli.TrackA;

/// <summary>
/// The owner's side of Track A (architecture §4.7): verifying an incoming session, the acceptance gate with its manual
/// review, and the generated index. These read an incoming session outside git and write only into the corpus folder the
/// owner names (the repository's <c>track-a</c>), never into a session they read.
/// </summary>
internal static class CorpusFlows
{
    public static int Verify(TrackACli.VerifyRequest request, AppInfo app, UserProfileRedactor profile)
    {
        Action<string> say = new Operator(profile).Say;
        if (ReadSession(request.Session, say) is not { } session)
        {
            return 1;
        }

        var evaluation = AcceptanceGate.Evaluate(session, Build(app), (_, _) => false);
        Print(evaluation, say);
        WarnUnlisted(evaluation, session, say);
        say(evaluation.Passed
            ? "Verified: every automatic criterion passes. Acceptance also needs the manual review (track-a promote)."
            : "Not verified: see the criteria marked [x].");
        return evaluation.Passed ? 0 : TrackACli.GateRefused;
    }

    public static int Promote(TrackACli.PromoteRequest request, AppInfo app, UserProfileRedactor profile)
    {
        var console = new Operator(profile);
        Action<string> say = console.Say;
        var corpus = Path.GetFullPath(request.Corpus);
        if (!IsCorpus(corpus, say))
        {
            return 2;
        }

        if (ReadSession(request.Session, say) is not { } session)
        {
            return 1;
        }

        var build = Build(app);
        var evaluation = AcceptanceGate.Evaluate(session, build, (cohort, id) => Directory.Exists(Path.Combine(corpus, Corpus.CohortFolder, cohort, id)));
        Print(evaluation, say);
        WarnUnlisted(evaluation, session, say);
        if (!evaluation.Passed || evaluation.Manifest is not { } manifest)
        {
            say("Refused: nothing was accepted.");
            return TrackACli.GateRefused;
        }

        try
        {
            // The manual part: shown in full, decided by a person.
            say("");
            say("Manual review. Nothing below is judged automatically.");
            say("Operator answers, verbatim:");
            foreach (var line in evaluation.AnswersToReview)
            {
                say("  " + line);
            }

            if (evaluation.AnswersToReview.Count == 0)
            {
                say("  (none)");
            }

            var decisions = new Dictionary<string, EvidenceDecision>(StringComparer.Ordinal);
            foreach (var file in manifest.Evidence.Files)
            {
                say("");
                say($"Evidence {file.File} ({file.Tool}, {file.State}, {file.Kind}; text scan on the captured PC: {file.TextScan}).");
                say($"  Open {Path.Combine(Path.GetFullPath(request.Session), "evidence", file.File)} and look for usernames, the computer name, account names and serial numbers.");
                decisions[file.File] = console.Confirm("  Accept it as evidence? (no = reject)") ? EvidenceDecision.Accepted : EvidenceDecision.Rejected;
            }

            var acknowledged = new List<string>();
            foreach (var kind in manifest.Privacy.UncheckedIdentifierKinds)
            {
                if (console.Confirm($"The captured PC's {kind} couldn't be read, so nothing proved it absent. Have you checked the files for it by hand?"))
                {
                    acknowledged.Add(kind);
                }
            }

            var consentSince = evaluation.ConsentOpen
                && evaluation.Observations?.Find(ObservationCatalog.GameNamesConsent)?.State == ObservationState.Unknown
                && console.Confirm("The package holds game names and the capture recorded the PC owner's consent as unknown. Has the owner agreed since?");

            say("");
            say($"Read {PackageFiles.ScrubbedRaw}, {PackageFiles.Facts}, {PackageFiles.Results} and {PackageFiles.Observations} in {Path.Combine(Path.GetFullPath(request.Session), "shareable")}.");
            var reviewed = console.Confirm("Did you find nothing personal in them (names, account details, serial numbers, user-chosen text)?");

            var (record, refusals) = AcceptanceGate.Accept(evaluation, new AcceptanceDecisions(reviewed, acknowledged, consentSince, decisions), build, DateOnly.FromDateTime(DateTime.UtcNow));
            if (record is null)
            {
                foreach (var refusal in refusals)
                {
                    say("  [x] " + refusal);
                }

                say("Refused: nothing was accepted.");
                return TrackACli.GateRefused;
            }

            var accepted = new AcceptedSession(manifest, evaluation.Observations!, record);
            var acceptanceJson = TrackAJson.Serialize(record);
            var readme = Corpus.Readme(accepted);
            var findings = ShareableAudit.AuditJson(Corpus.AcceptanceFile, acceptanceJson).Concat(ShareableAudit.AuditText(Corpus.ReadmeFile, Encoding.UTF8.GetString(readme))).ToList();
            if (findings.Count > 0)
            {
                foreach (var finding in findings)
                {
                    say($"  [x] {finding.File} {finding.Pointer}: {finding.Category}");
                }

                say("Refused: a generated file failed the audit; nothing was accepted.");
                return TrackACli.GateRefused;
            }

            var folder = LocalStore.CreateExportFolder(corpus, Corpus.CohortFolder, manifest.CohortId, manifest.SessionId);
            foreach (var name in Corpus.SessionFiles.Take(5))
            {
                LocalStore.WriteUserExport(Path.Combine(folder, name), session.Shareable[name]);
            }

            LocalStore.WriteUserExport(Path.Combine(folder, Corpus.AcceptanceFile), acceptanceJson);
            LocalStore.WriteUserExport(Path.Combine(folder, Corpus.ReadmeFile), readme);
            say("");
            say($"Accepted: {folder}");
            if (!record.Replay.SameCommit)
            {
                say($"  Accepted with another commit than the producer's; replay: facts {record.Replay.Facts}, results {record.Replay.Results} (recorded).");
            }

            return Index(new TrackACli.IndexRequest(corpus, Write: true), profile);
        }
        catch (OperatorInputEndedException ex)
        {
            say(ex.Message + " Nothing was accepted.");
            return 1;
        }
    }

    public static int Index(TrackACli.IndexRequest request, UserProfileRedactor profile)
    {
        Action<string> say = new Operator(profile).Say;
        var corpus = Path.GetFullPath(request.Corpus);
        if (!IsCorpus(corpus, say))
        {
            return 2;
        }

        List<AcceptedSession> sessions;
        try
        {
            sessions = Load(corpus);
        }
        catch (Exception ex) when (ex is TrackAFormatException or IOException)
        {
            say($"The corpus can't be read: {ex.Message}");
            return 1;
        }

        var index = CohortIndex.Build(sessions);
        if (request.Write)
        {
            var json = TrackAJson.Serialize(index);
            var findings = ShareableAudit.AuditJson(Corpus.IndexFile, json);
            if (findings.Count > 0)
            {
                say($"The generated index failed the audit ({string.Join(", ", findings.Select(finding => finding.Category))}); it wasn't written.");
                return 1;
            }

            LocalStore.WriteUserExport(Path.Combine(corpus, Corpus.IndexFile), json);
            say($"Wrote {Path.Combine(corpus, Corpus.IndexFile)} from {sessions.Count} accepted session(s).");
        }

        using var stdout = Console.OpenStandardOutput();
        stdout.Write(Encoding.UTF8.GetBytes(CoverageReport.Render(index)));
        return 0;
    }

    /// <summary>Every accepted session in the corpus, strictly read.</summary>
    internal static List<AcceptedSession> Load(string corpus)
    {
        var cohort = Path.Combine(corpus, Corpus.CohortFolder);
        var sessions = new List<AcceptedSession>();
        if (!Directory.Exists(cohort))
        {
            return sessions;
        }

        foreach (var machine in Directory.EnumerateDirectories(cohort).Order(StringComparer.Ordinal))
        {
            foreach (var folder in Directory.EnumerateDirectories(machine).Order(StringComparer.Ordinal))
            {
                var files = Directory.EnumerateFiles(folder).ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes, StringComparer.Ordinal);
                sessions.Add(Corpus.Read(Path.GetFileName(machine), Path.GetFileName(folder), files));
            }
        }

        return sessions;
    }

    /// <summary>The corpus is the repository's <c>track-a</c> folder: it exists, it is inside a git repository, and it holds the corpus README.</summary>
    private static bool IsCorpus(string corpus, Action<string> say)
    {
        if (Directory.Exists(corpus) && Program.InsideGitRepository(corpus) && File.Exists(Path.Combine(corpus, Corpus.ReadmeFile)))
        {
            return true;
        }

        say($"{corpus} isn't the Track A corpus: give the repository's track-a folder (it exists, is inside the repository and holds README.md).");
        return false;
    }

    /// <summary>Evidence the operator handed over that the capture didn't record is never reviewed: name it so it gets deleted.</summary>
    private static void WarnUnlisted(GateEvaluation evaluation, IncomingSession session, Action<string> say)
    {
        var listed = evaluation.Manifest?.Evidence.Files.Select(file => file.File).ToHashSet(StringComparer.Ordinal) ?? [];
        var unlisted = session.Evidence.Keys.Where(name => !listed.Contains(name)).Order(StringComparer.Ordinal).ToList();
        if (unlisted.Count > 0)
        {
            say($"  Warning: evidence files the capture didn't record, so nobody reviews them: {string.Join(", ", unlisted)}. Delete them from the incoming folder.");
        }
    }

    private static IncomingSession? ReadSession(string path, Action<string> say)
    {
        var full = Path.GetFullPath(path);
        var shareable = Path.Combine(full, "shareable");
        if (!Directory.Exists(shareable))
        {
            say($"{full} has no shareable folder: give the session folder that holds shareable\\ (and evidence\\).");
            return null;
        }

        var evidence = Path.Combine(full, "evidence");
        return new IncomingSession(
            Directory.EnumerateFiles(shareable).ToDictionary(file => Path.GetFileName(file), File.ReadAllBytes, StringComparer.Ordinal),
            Directory.Exists(evidence)
                ? Directory.EnumerateFiles(evidence).ToDictionary(file => Path.GetFileName(file), File.ReadAllBytes, StringComparer.Ordinal)
                : new Dictionary<string, byte[]>(StringComparer.Ordinal));
    }

    private static PromotingBuild Build(AppInfo app) => new(app.Version, app.Commit.IsKnown ? app.Commit.Value : null);

    private static void Print(GateEvaluation evaluation, Action<string> say)
    {
        say("Acceptance gate, automatic criteria:");
        foreach (var check in evaluation.Checks)
        {
            say($"  {(check.Passed ? "[ok]" : "[x] ")} {check.Criterion}: {check.Detail}");
        }
    }
}
