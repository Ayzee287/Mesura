using System.Globalization;
using System.Text.Json.Serialization;
using Mesura.Core.Checks;
using Mesura.Core.Checks.Serialization;
using Mesura.Core.Facts.Serialization;
using Mesura.Core.Normalize;
using Mesura.Core.Privacy;
using Mesura.Core.Raw;
using Mesura.Core.Raw.Serialization;

namespace Mesura.Core.TrackA;

/// <summary>An incoming session as the owner received it: the files of its <c>shareable</c> folder and of its <c>evidence</c> folder.</summary>
public sealed record IncomingSession(IReadOnlyDictionary<string, byte[]> Shareable, IReadOnlyDictionary<string, byte[]> Evidence);

/// <summary>One criterion of the acceptance gate and how the session fared.</summary>
public sealed record GateCheck(string Criterion, bool Passed, string Detail);

/// <summary>The build that is doing the accepting.</summary>
/// <param name="Commit"><c>null</c> when the build has no single commit: such a build can't accept anything.</param>
public sealed record PromotingBuild(string Version, string? Commit);

/// <summary>What only a person can decide at acceptance (architecture §4.7): nothing here is inferred.</summary>
/// <param name="ReviewConfirmed">The person read every operator answer and evidence name the kit showed, and the package files, and found nothing personal.</param>
/// <param name="UncheckedKindsAcknowledged">The identifier kinds the capture couldn't check, acknowledged one by one.</param>
/// <param name="ConsentGivenSince">When the capture recorded the game-names consent as unknown: the PC owner has agreed since.</param>
/// <param name="EvidenceDecisions">Each evidence file the manifest lists: accepted or rejected after being looked at.</param>
public sealed record AcceptanceDecisions(
    bool ReviewConfirmed,
    IReadOnlyList<string> UncheckedKindsAcknowledged,
    bool ConsentGivenSince,
    IReadOnlyDictionary<string, EvidenceDecision> EvidenceDecisions);

[JsonConverter(typeof(CamelCaseEnumConverter<EvidenceDecision>))]
public enum EvidenceDecision
{
    Accepted,
    Rejected,
}

/// <summary>The result of the automatic part of the gate, and what a person must still look at.</summary>
public sealed class GateEvaluation
{
    internal GateEvaluation(
        IReadOnlyList<GateCheck> checks,
        CaptureManifest? manifest,
        OperatorObservations? observations,
        RawSnapshot? scrubbed,
        byte[]? manifestJson,
        ReplayRecord? replay,
        bool gameNamesPresent)
    {
        Checks = checks;
        Manifest = manifest;
        Observations = observations;
        Scrubbed = scrubbed;
        ManifestJson = manifestJson;
        Replay = replay;
        GameNamesPresent = gameNamesPresent;
    }

    public IReadOnlyList<GateCheck> Checks { get; }

    public bool Passed => Checks.All(check => check.Passed);

    public CaptureManifest? Manifest { get; }

    public OperatorObservations? Observations { get; }

    public RawSnapshot? Scrubbed { get; }

    internal byte[]? ManifestJson { get; }

    public ReplayRecord? Replay { get; }

    public bool GameNamesPresent { get; }

    /// <summary>Every string an operator typed, shown verbatim to the person reviewing the package.</summary>
    public IReadOnlyList<string> AnswersToReview =>
        [.. Observations?.Observations.Select(observation => observation.Value switch
        {
            ObservedText text => $"{observation.Field}: {text.Text}",
            ObservedChoices choices => $"{observation.Field}: {string.Join(", ", choices.Items)}",
            ObservedNumber number => string.Create(CultureInfo.InvariantCulture, $"{observation.Field}: {number.Number}"),
            _ => $"{observation.Field}: ({observation.State})",
        }) ?? []];

    /// <summary>Whether the capture recorded consent as unknown, so the person may record it now.</summary>
    public bool ConsentOpen => GameNamesPresent && Observations?.Find(ObservationCatalog.GameNamesConsent)?.Text != "yes";
}

/// <summary>
/// How the promoting build's reading of the scrubbed snapshot compares with the outputs stored at capture. Equal bytes
/// are required when both builds are the same commit; across commits a difference is recorded, not refused, because the
/// stored outputs are the historical record of the producing build (spec §21).
/// </summary>
public sealed record ReplayRecord(bool SameCommit, Comparison Facts, Comparison Results);

/// <summary>
/// <c>acceptance.json</c> (architecture §4.7): the record that made an incoming session an accepted cohort fixture. It
/// binds itself to the capture by the manifest's SHA-256 and never changes a captured file.
/// </summary>
public sealed record AcceptanceRecord(
    int SchemaVersion,
    string CohortId,
    string SessionId,
    string AcceptedOn,
    string ManifestSha256,
    PromotingBuild AcceptedWith,
    ReplayRecord Replay,
    IReadOnlyList<string> Criteria,
    ManualReview Review,
    ConsentRecord Consent,
    IReadOnlyList<EvidenceDecisionRecord> Evidence,
    SessionTraits Traits,
    IReadOnlyList<CheckPath> CheckPaths)
{
    public const int CurrentSchemaVersion = 1;

    private readonly bool _idsFit = TrackA.CohortId.IsValid(CohortId) && TrackA.SessionId.IsValid(SessionId)
        ? true
        : throw new ArgumentException("An acceptance record names a cohort id and a session id.", nameof(CohortId));

    private readonly bool _isSha256 = CaptureManifest.IsSha256(ManifestSha256) ? true : throw new ArgumentException("A SHA-256 is 64 lower-case hex digits.", nameof(ManifestSha256));

    private readonly bool _lists = Guard.NoNulls(Criteria, nameof(Criteria)) && Guard.NoNulls(Evidence, nameof(Evidence)) && Guard.NoNulls(CheckPaths, nameof(CheckPaths))
        && AcceptedWith is not null && Review is not null && Consent is not null && Traits is not null && Replay is not null;
}

/// <param name="AnswersShown">How many operator answers were shown verbatim.</param>
/// <param name="EvidenceNamesShown">How many evidence file names were shown.</param>
public sealed record ManualReview(bool Confirmed, int AnswersShown, int EvidenceNamesShown, IReadOnlyList<string> UncheckedIdentifierKindsAcknowledged)
{
    private readonly bool _lists = Guard.NoNulls(UncheckedIdentifierKindsAcknowledged, nameof(UncheckedIdentifierKindsAcknowledged));
}

[JsonConverter(typeof(CamelCaseEnumConverter<ConsentStatus>))]
public enum ConsentStatus
{
    /// <summary>The package holds no game names.</summary>
    NotNeeded,

    GivenAtCapture,

    /// <summary>The capture recorded "unknown"; the owner confirmed the PC owner's agreement at acceptance.</summary>
    GivenAtAcceptance,
}

public sealed record ConsentRecord(ConsentStatus GameNames);

public sealed record EvidenceDecisionRecord(string File, string Sha256, EvidenceDecision Decision);

/// <summary>
/// The acceptance gate (architecture §4.7, spec §26): an incoming session becomes an accepted cohort fixture only when
/// every automatic criterion passes and a person has made the manual decisions. It reads files, never the capture PC: the
/// identifier check against that PC's own identifiers happened there and is recorded in the manifest.
/// </summary>
public static class AcceptanceGate
{
    public static GateEvaluation Evaluate(IncomingSession session, PromotingBuild build, Func<string, string, bool> alreadyAccepted)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(alreadyAccepted);

        var checks = new List<GateCheck>();
        void Check(string criterion, bool passed, string detail) => checks.Add(new GateCheck(criterion, passed, detail));

        Check("promotingBuildCommitted", build.Commit is not null, build.Commit is null
            ? "This build has no single commit (uncommitted changes). Accept with a build of a commit."
            : $"accepting with {build.Commit}");

        if (!session.Shareable.TryGetValue(PackageFiles.Manifest, out var manifestJson))
        {
            Check("manifestReadable", false, $"No {PackageFiles.Manifest}: the capture didn't finish, or this isn't a shareable folder.");
            return new GateEvaluation(checks, null, null, null, null, null, false);
        }

        CaptureManifest manifest;
        try
        {
            manifest = TrackAJson.Deserialize<CaptureManifest>(manifestJson, CaptureManifest.CurrentSchemaVersion);
            Check("manifestReadable", true, "strict read, schema " + CaptureManifest.CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture));
        }
        catch (TrackAFormatException ex)
        {
            Check("manifestReadable", false, ex.Message);
            return new GateEvaluation(checks, null, null, null, null, null, false);
        }

        Check("notAlreadyAccepted", !alreadyAccepted(manifest.CohortId, manifest.SessionId), $"{manifest.CohortId}/{manifest.SessionId}");

        // Integrity: exactly the listed files, each with its hash.
        var listed = manifest.Files.Select(file => file.Path).Append(PackageFiles.Manifest).ToHashSet(StringComparer.Ordinal);
        var extra = session.Shareable.Keys.Where(name => !listed.Contains(name)).Order(StringComparer.Ordinal).ToList();
        var missing = manifest.Files.Where(file => !session.Shareable.ContainsKey(file.Path)).Select(file => file.Path).ToList();
        Check("filesComplete", extra.Count == 0 && missing.Count == 0,
            missing.Count + extra.Count == 0 ? "exactly the listed files" : $"missing: {Join(missing)}; not listed: {extra.Count} file(s)");

        var roles = manifest.Files.Select(file => file.Role).Order().ToList();
        Check("packageComplete", roles.SequenceEqual(Enum.GetValues<FileRole>()),
            roles.SequenceEqual(Enum.GetValues<FileRole>()) ? "the scrubbed scan, facts, results and observations" : "an accepted session holds all four package files");

        var changed = manifest.Files
            .Where(file => session.Shareable.TryGetValue(file.Path, out var bytes) && (bytes.LongLength != file.Bytes || CapturePipeline.Sha256(bytes) != file.Sha256))
            .Select(file => file.Path)
            .ToList();
        Check("hashesMatch", changed.Count == 0, changed.Count == 0 ? "SHA-256 of every file matches the manifest" : $"changed since capture: {Join(changed)}");

        Check("producerCommitted", manifest.Producer.Commit is not null, manifest.Producer.Commit ?? manifest.Producer.CommitMissing!);
        Check("packageReady", manifest.Package.Status == PackageStatus.Ready,
            manifest.Package.Status == PackageStatus.Ready ? "ready" : $"not promotable: {Join(manifest.Package.Reasons)}");
        Check("privacyVerifiedAtCapture", manifest.Stages.Any(stage => stage.Stage == Stages.PrivacyVerification && stage.Status == StageStatus.Succeeded),
            manifest.Privacy.Verdict == PrivacyVerdict.Passed ? "passed" : $"passed; unchecked kinds to acknowledge: {Join(manifest.Privacy.UncheckedIdentifierKinds)}");
        Check("determinism", manifest.Determinism.Passed, manifest.Determinism.Passed ? "identical"
            : manifest.Determinism.Failed ? "a determinism comparison differed at capture" : "not every determinism comparison ran at capture");

        // The documents themselves.
        OperatorObservations? observations = null;
        if (session.Shareable.TryGetValue(PackageFiles.Observations, out var observationsJson))
        {
            try
            {
                observations = TrackAJson.Deserialize<OperatorObservations>(observationsJson, OperatorObservations.CurrentSchemaVersion);
                var consistent = observations.CohortId == manifest.CohortId && observations.SessionId == manifest.SessionId
                    && observations.Scenario == manifest.Scenario.Id && observations.Step == manifest.Scenario.Step;
                Check("observationsReadable", consistent, consistent ? "strict read; same cohort, session and scenario" : "the observations name another cohort, session or scenario");
            }
            catch (TrackAFormatException ex)
            {
                Check("observationsReadable", false, ex.Message);
            }
        }
        else
        {
            Check("observationsReadable", false, $"no {PackageFiles.Observations}");
        }

        RawSnapshot? scrubbed = null;
        if (manifest.Producer.Schemas.Raw != RawSnapshot.CurrentSchemaVersion)
        {
            Check("rawReadable", false, $"raw schema {manifest.Producer.Schemas.Raw}; this build reads {RawSnapshot.CurrentSchemaVersion}: the capture is superseded");
        }
        else if (!session.Shareable.TryGetValue(PackageFiles.ScrubbedRaw, out var rawJson))
        {
            Check("rawReadable", false, $"no {PackageFiles.ScrubbedRaw}");
        }
        else
        {
            try
            {
                scrubbed = SnapshotJson.Deserialize(rawJson);
                Check("rawReadable", true, "strict read, raw schema " + RawSnapshot.CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture));
            }
            catch (SnapshotFormatException ex)
            {
                Check("rawReadable", false, ex.Message);
            }
        }

        // The identifier-free privacy audit, again, on every file.
        var findings = new List<PrivacyFinding>();
        foreach (var (name, bytes) in session.Shareable.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            try
            {
                findings.AddRange(name == PackageFiles.ScrubbedRaw && scrubbed is not null
                    ? ShareableAudit.AuditScrubbedSnapshot(name, bytes, scrubbed)
                    : ShareableAudit.AuditJson(name, bytes));
            }
            catch (System.Text.Json.JsonException)
            {
                findings.Add(new PrivacyFinding(name, "not JSON", "", "Every package file is JSON; this one isn't."));
            }
        }

        Check("privacyAudit", findings.Count == 0, findings.Count == 0
            ? "no finding in any file"
            : string.Join("; ", findings.Select(finding => $"{finding.File} {finding.Pointer}: {finding.Category}")));

        // Replay with this build.
        ReplayRecord? replay = null;
        var gameNamesPresent = false;
        if (scrubbed is not null)
        {
            gameNamesPresent = scrubbed.Sources.Steam.Data?.Libraries.Value?.Any(library => library.Manifests.Value?.Count > 0) == true
                || observations?.Find(ObservationCatalog.GameName) is { State: ObservationState.Known };

            var facts = Normalizer.Normalize(scrubbed);
            var sameCommit = build.Commit is not null && build.Commit == manifest.Producer.Commit;
            replay = new ReplayRecord(
                sameCommit,
                Compare(FactsJson.Serialize(facts), session.Shareable.GetValueOrDefault(PackageFiles.Facts)),
                Compare(CheckResultsJson.Serialize(CheckRunner.Run(facts, FixtureGate.Validated)), session.Shareable.GetValueOrDefault(PackageFiles.Results)));

            var reproduced = replay.Facts == Comparison.Identical && replay.Results == Comparison.Identical;
            var compared = replay.Facts != Comparison.NotRun && replay.Results != Comparison.NotRun;
            Check("replay", compared && (reproduced || !sameCommit), !compared ? "a stored output to compare with is missing" : (sameCommit, reproduced) switch
            {
                (true, true) => "this build (the producing commit) reproduces the facts and results byte for byte",
                (true, false) => "this build is the producing commit and does NOT reproduce the stored outputs: a determinism defect",
                (false, true) => $"{(build.Commit is null ? "a build without a commit" : "another commit (" + build.Commit + ")")}; it reproduces the stored outputs",
                _ => $"{(build.Commit is null ? "a build without a commit" : "another commit (" + build.Commit + ")")}; its outputs differ from the producer's, recorded, not refused",
            });

            // The scenario record must agree with what the files show.
            if (observations is not null)
            {
                var scenario = ScenarioCatalog.Require(manifest.Scenario.Id);
                var step = scenario.Step(manifest.Scenario.Step);
                var performed = manifest.Scenario.OperatorDisposition == ScenarioDisposition.Performed;
                var unavailable = scenario.RequiredSources.Where(source => !SourceOk(scrubbed, source)).ToList();
                var unconfirmed = performed
                    ? step.Fields.Where(field => ObservationCatalog.Procedures.Contains(field) && observations.Find(field)?.Text != "yes").Select(field => field.Id).ToList()
                    : [];
                var missingEvidence = performed
                    ? step.Evidence.Where(expected => !manifest.Evidence.Files.Any(file => file.Tool == expected.Tool && EvidenceConvention.BelongsTo(file.State, step.Name))).Select(expected => expected.Tool).ToList()
                    : [];
                Check("scenarioConsistent",
                    unavailable.SequenceEqual(manifest.Scenario.UnavailableRequiredSources, StringComparer.Ordinal)
                        && unconfirmed.SequenceEqual(manifest.Scenario.UnconfirmedProcedures, StringComparer.Ordinal)
                        && missingEvidence.SequenceEqual(manifest.Scenario.MissingEvidenceTools)
                        && manifest.Evidence.Files.All(file => file.BelongsToStep == EvidenceConvention.BelongsTo(file.State, step.Name)),
                    $"{manifest.Scenario.Id}{(manifest.Scenario.Step is { } name ? "/" + name : "")}: {manifest.Scenario.Status}");

                var unanswered = observations.Unanswered(manifest.Scenario.OperatorDisposition, gameNamesPresent);
                Check("annotationsComplete", unanswered.Count == 0, unanswered.Count == 0 ? "every question answered (a value, unknown or n/a)" : $"unanswered: {Join(unanswered)}");
            }
        }

        // Evidence the manifest lists must be here, unchanged, to be reviewed.
        var evidenceProblems = manifest.Evidence.Files
            .Where(file => !session.Evidence.TryGetValue(file.File, out var bytes) || CapturePipeline.Sha256(bytes) != file.Sha256)
            .Select(file => file.File)
            .ToList();
        Check("evidencePresent", evidenceProblems.Count == 0, evidenceProblems.Count == 0
            ? $"{manifest.Evidence.Files.Count} listed evidence file(s) present and unchanged"
            : $"missing or changed: {Join(evidenceProblems)} (copy the session's evidence folder unchanged)");

        return new GateEvaluation(checks, manifest, observations, scrubbed, manifestJson, replay, gameNamesPresent);
    }

    /// <summary>
    /// Completes acceptance with a person's decisions. Returns the reasons it can't, or the acceptance record. The traits
    /// and check paths are this build's reading of the scrubbed snapshot.
    /// </summary>
    public static (AcceptanceRecord? Record, IReadOnlyList<string> Refusals) Accept(GateEvaluation evaluation, AcceptanceDecisions decisions, PromotingBuild build, DateOnly acceptedOn)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(build);

        var refusals = evaluation.Checks.Where(check => !check.Passed).Select(check => $"{check.Criterion}: {check.Detail}").ToList();
        if (evaluation.Manifest is not { } manifest || evaluation.Scrubbed is not { } scrubbed || evaluation.Replay is not { } replay || evaluation.ManifestJson is null)
        {
            refusals.Add("the package couldn't be read");
            return (null, refusals);
        }

        if (!decisions.ReviewConfirmed)
        {
            refusals.Add("manualReview: a person must read the answers, the evidence names and the files, and confirm nothing personal is in them");
        }

        var unacknowledged = manifest.Privacy.UncheckedIdentifierKinds.Except(decisions.UncheckedKindsAcknowledged, StringComparer.Ordinal).ToList();
        if (unacknowledged.Count > 0)
        {
            refusals.Add($"uncheckedKinds: not acknowledged: {Join(unacknowledged)}");
        }

        var consent = !evaluation.GameNamesPresent
            ? ConsentStatus.NotNeeded
            : evaluation.Observations?.Find(ObservationCatalog.GameNamesConsent)?.Text == "yes"
                ? ConsentStatus.GivenAtCapture
                : evaluation.Observations?.Find(ObservationCatalog.GameNamesConsent)?.State == ObservationState.Unknown && decisions.ConsentGivenSince
                    ? ConsentStatus.GivenAtAcceptance
                    : (ConsentStatus?)null;
        if (consent is null)
        {
            refusals.Add("consent: the package holds game names and the PC owner's agreement isn't recorded");
        }

        var undecided = manifest.Evidence.Files.Where(file => !decisions.EvidenceDecisions.ContainsKey(file.File)).Select(file => file.File).ToList();
        if (undecided.Count > 0)
        {
            refusals.Add($"evidenceReview: not decided: {Join(undecided)}");
        }

        if (refusals.Count > 0 || build.Commit is null)
        {
            return (null, refusals);
        }

        var facts = Normalizer.Normalize(scrubbed);
        var record = new AcceptanceRecord(
            AcceptanceRecord.CurrentSchemaVersion,
            manifest.CohortId,
            manifest.SessionId,
            acceptedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            CapturePipeline.Sha256(evaluation.ManifestJson),
            build,
            replay,
            [.. evaluation.Checks.Select(check => check.Criterion)],
            new ManualReview(true, evaluation.AnswersToReview.Count, manifest.Evidence.Files.Count, [.. manifest.Privacy.UncheckedIdentifierKinds]),
            new ConsentRecord(consent!.Value),
            [.. manifest.Evidence.Files.Select(file => new EvidenceDecisionRecord(file.File, file.Sha256, decisions.EvidenceDecisions[file.File]))],
            SessionTraits.From(facts),
            CheckPath.Of(CheckRunner.Run(facts, FixtureGate.Validated)));

        return (record, []);
    }

    private static bool SourceOk(RawSnapshot snapshot, string source)
    {
        var sources = snapshot.Sources;
        return source switch
        {
            RawSources.Display => sources.Display.Status == SourceStatus.Ok,
            RawSources.Edid => sources.Edid.Status == SourceStatus.Ok,
            RawSources.Adapters => sources.Adapters.Status == SourceStatus.Ok,
            RawSources.Smbios => sources.Smbios.Status == SourceStatus.Ok,
            RawSources.Pcie => sources.Pcie.Status == SourceStatus.Ok,
            RawSources.Storage => sources.Storage.Status == SourceStatus.Ok,
            RawSources.Steam => sources.Steam.Status == SourceStatus.Ok,
            RawSources.Power => sources.Power.Status == SourceStatus.Ok,
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
        };
    }

    private static Comparison Compare(byte[] produced, byte[]? stored) =>
        stored is null ? Comparison.NotRun : produced.AsSpan().SequenceEqual(stored) ? Comparison.Identical : Comparison.Differs;

    private static string Join(IEnumerable<string> items) => string.Join(", ", items);
}
