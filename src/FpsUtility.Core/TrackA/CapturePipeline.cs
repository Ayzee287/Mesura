using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FpsUtility.Core.Checks;
using FpsUtility.Core.Checks.Serialization;
using FpsUtility.Core.Facts;
using FpsUtility.Core.Facts.Serialization;
using FpsUtility.Core.Normalize;
using FpsUtility.Core.Privacy;
using FpsUtility.Core.Raw;
using FpsUtility.Core.Raw.Serialization;

namespace FpsUtility.Core.TrackA;

/// <summary>
/// The first half of a capture (architecture §4.7): the scan, scrubbed by the existing pseudonymiser, normalised by the
/// existing normaliser, checked by the existing checks, with the determinism the contracts promise verified on it.
/// Nothing here is written anywhere; the operator's answers come next.
/// </summary>
public sealed class CapturePreparation
{
    internal CapturePreparation(
        RawSnapshot raw,
        RawSnapshot scrubbed,
        byte[] scrubbedJson,
        int idsReplaced,
        NormalizedSnapshot? facts,
        byte[]? factsJson,
        CheckResults? results,
        byte[]? resultsJson,
        IReadOnlyList<ManifestStage> stages,
        ManifestDeterminism determinism,
        ManifestRepeatScan repeatScan,
        IReadOnlyList<LocalIdentifier> opaqueIdentifiers)
    {
        Raw = raw;
        Scrubbed = scrubbed;
        ScrubbedJson = scrubbedJson;
        IdsReplaced = idsReplaced;
        Facts = facts;
        FactsJson = factsJson;
        Results = results;
        ResultsJson = resultsJson;
        Stages = stages;
        Determinism = determinism;
        RepeatScan = repeatScan;
        OpaqueIdentifiers = opaqueIdentifiers;
    }

    /// <summary>The scan as collected. It never reaches the package; the kit writes it only if the operator asks to keep it.</summary>
    public RawSnapshot Raw { get; }

    public RawSnapshot Scrubbed { get; }

    public byte[] ScrubbedJson { get; }

    public int IdsReplaced { get; }

    /// <summary><c>null</c> when normalising failed (a defect, recorded as a failed stage).</summary>
    public NormalizedSnapshot? Facts { get; }

    public byte[]? FactsJson { get; }

    public CheckResults? Results { get; }

    public byte[]? ResultsJson { get; }

    public IReadOnlyList<ManifestStage> Stages { get; }

    public ManifestDeterminism Determinism { get; }

    public ManifestRepeatScan RepeatScan { get; }

    /// <summary>The scan's own opaque ids (from both scans), for the identifier check. In memory only.</summary>
    public IReadOnlyList<LocalIdentifier> OpaqueIdentifiers { get; }

    /// <summary>Whether the scrubbed snapshot holds Steam app manifests, and so game names.</summary>
    public bool HoldsSteamGames => Scrubbed.Sources.Steam.Data?.Libraries.Value?.Any(library => library.Manifests.Value?.Count > 0) == true;
}

/// <summary>Everything the second half of a capture needs.</summary>
/// <param name="Identifiers">This PC's own identifiers (<c>LocalIdentifiers.Read</c>). In memory only.</param>
/// <param name="UncheckedIdentifierKinds">The identifier kinds that couldn't be read on this PC.</param>
/// <param name="Evidence">Every file found in the session's evidence folder; the convention decides which are recorded.</param>
public sealed record CaptureRequest(
    string CohortId,
    string SessionId,
    string Scenario,
    string? Step,
    ManifestSeries? Series,
    ScenarioDisposition Disposition,
    CapturePreparation Preparation,
    OperatorObservations Observations,
    IReadOnlyList<EvidenceFile> Evidence,
    IReadOnlyList<LocalIdentifier> Identifiers,
    IReadOnlyList<string> UncheckedIdentifierKinds,
    bool UnscrubbedSnapshotKept);

/// <summary>What a capture produced: a verified package, or a report of why there is none.</summary>
public abstract record CaptureOutcome;

/// <param name="Files">The package's files by name, the manifest included. Write the manifest last.</param>
public sealed record CapturedPackage(CaptureManifest Manifest, IReadOnlyDictionary<string, byte[]> Files) : CaptureOutcome;

/// <param name="Json">The report as JSON (file, category, pointer, remediation; never a value).</param>
public sealed record PrivacyFailure(PrivacyFailureReport Report, byte[] Json) : CaptureOutcome;

/// <summary>
/// The Track A capture pipeline (architecture §4.7). It orchestrates what exists: <see cref="FixturePseudonymizer"/>,
/// <see cref="Normalizer"/>, <see cref="CheckRunner"/> with the product's <see cref="FixtureGate.Validated"/>,
/// <see cref="IdentifierLeakCheck"/> and <see cref="ShareableAudit"/>. It adds no scanner, scrubber or normaliser. It is
/// pure: the caller collects, reads identifiers, asks the operator and writes files; the clock only reaches it through
/// the session id it is given.
/// </summary>
public static partial class CapturePipeline
{
    /// <summary>Cultures the facts must not depend on (a dotless i, a decimal comma, another script).</summary>
    public static IReadOnlyList<string> DeterminismCultures { get; } = ["en-US", "tr-TR", "de-DE", "ar-SA"];

    /// <summary>
    /// Scrubs, normalises and checks a scan, and verifies determinism. A failure of the normaliser or the checks is
    /// recorded as a failed stage (type and message only; they run on scrubbed data), so the scrubbed snapshot can still
    /// be handed over to reproduce it. A failure of the scrub itself propagates: without it nothing is shareable.
    /// </summary>
    /// <param name="repeat">A second scan straight after the first, or <c>null</c> when none was taken.</param>
    /// <param name="redact">Rewrites the profile path in a failure message.</param>
    public static CapturePreparation Prepare(RawSnapshot raw, RawSnapshot? repeat, Func<string, string> redact) =>
        Prepare(raw, repeat, redact, Normalizer.Normalize, facts => CheckRunner.Run(facts, FixtureGate.Validated));

    internal static CapturePreparation Prepare(
        RawSnapshot raw,
        RawSnapshot? repeat,
        Func<string, string> redact,
        Func<RawSnapshot, NormalizedSnapshot> normalize,
        Func<NormalizedSnapshot, CheckResults> check)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(redact);

        var stages = new List<ManifestStage>
        {
            new(Stages.PlatformCheck, StageStatus.Succeeded, null),
            new(Stages.Scan, StageStatus.Succeeded, null),
            new(Stages.RepeatScan, repeat is null ? StageStatus.Skipped : StageStatus.Succeeded, null),
        };

        var (scrubbed, idsReplaced) = FixturePseudonymizer.Pseudonymize(raw);
        var scrubbedJson = SnapshotJson.Serialize(scrubbed);
        stages.Add(new(Stages.Scrub, StageStatus.Succeeded, null));

        var opaque = FixturePseudonymizer.OpaqueIdentifiers(raw).ToList();

        NormalizedSnapshot? facts = null;
        byte[]? factsJson = null;
        CheckResults? results = null;
        byte[]? resultsJson = null;

        if (Attempt(Stages.Normalize, () => normalize(scrubbed), stages, redact) is { } normalized)
        {
            facts = normalized;
            factsJson = FactsJson.Serialize(normalized);

            if (Attempt(Stages.Checks, () => check(normalized), stages, redact) is { } checkedResults)
            {
                results = checkedResults;
                resultsJson = CheckResultsJson.Serialize(checkedResults);
            }
        }
        else
        {
            stages.Add(new(Stages.Checks, StageStatus.Skipped, null));
        }

        var determinism = Determinism(scrubbedJson, factsJson, resultsJson, normalize, check);
        stages.Add(new(Stages.Determinism, determinism.Failed ? StageStatus.Failed : determinism.Passed ? StageStatus.Succeeded : StageStatus.Skipped, null));

        var repeatScan = new ManifestRepeatScan(false, 0, [], 0, Comparison.NotRun);
        if (repeat is not null)
        {
            opaque.AddRange(FixturePseudonymizer.OpaqueIdentifiers(repeat));
            repeatScan = Repeat(scrubbedJson, results, repeat, normalize, check);
        }

        return new CapturePreparation(
            raw, scrubbed, scrubbedJson, idsReplaced, facts, factsJson, results, resultsJson, stages, determinism, repeatScan,
            [.. opaque.DistinctBy(identifier => (identifier.Category, identifier.Value))]);
    }

    /// <summary>
    /// Checks one operator answer before it is accepted, so a hit re-asks the question instead of failing the package: the
    /// answer's text against this PC's identifiers and the scan's opaque ids, and against the personal patterns.
    /// </summary>
    /// <returns>The categories found (never the values). Empty means the answer can be kept.</returns>
    public static IReadOnlyList<string> Screen(Observation observation, IEnumerable<LocalIdentifier> identifiers)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(identifiers);

        var text = observation.Value switch
        {
            ObservedText value => value.Text,
            ObservedChoices value => string.Join(' ', value.Items),
            _ => null,
        };

        if (text is null)
        {
            return [];
        }

        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStringValue(text);
        }

        var json = buffer.WrittenSpan.ToArray();
        return
        [
            .. IdentifierLeakCheck.FindLeakLocations(json, identifiers).Select(location => location.Category)
                .Concat(ShareableAudit.AuditJson(observation.Field, json).Select(finding => finding.Category))
                .Distinct(StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Builds and verifies the package. Every file, the manifest included, is checked against this PC's identifiers and
    /// the scan's opaque ids, and audited; the package exists only when nothing is found. Otherwise the outcome is a
    /// failure report that names each file, category and JSON Pointer, never a value.
    /// </summary>
    public static CaptureOutcome Package(CaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var prepared = request.Preparation;
        var scenario = ScenarioCatalog.Require(request.Scenario);
        var step = scenario.Step(request.Step);
        var observations = request.Observations;
        if (observations.CohortId != request.CohortId || observations.SessionId != request.SessionId || observations.Scenario != scenario.Id || observations.Step != step.Name)
        {
            throw new ArgumentException("The observations belong to another session or scenario.", nameof(request));
        }

        var identifiers = request.Identifiers.Concat(prepared.OpaqueIdentifiers).ToList();
        var stages = prepared.Stages.ToList();

        // Evidence: only files that follow the naming convention are recorded, and never interpreted.
        var evidence = new List<ManifestEvidenceFile>();
        var ignored = 0;
        foreach (var file in request.Evidence.OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (EvidenceConvention.Read(file.Name) is not { } name)
            {
                ignored++;
                continue;
            }

            evidence.Add(new ManifestEvidenceFile(
                file.Name, name.Tool, name.State, name.Kind, EvidenceConvention.BelongsTo(name.State, step.Name),
                Sha256(file.Contents), file.Contents.LongLength, TextScan(name.Kind, file.Contents, identifiers), EvidenceReview.Pending));
        }

        stages.Add(new(Stages.Annotation, StageStatus.Succeeded, null));
        stages.Add(new(Stages.Evidence, evidence.Count > 0 || step.Evidence.Count > 0 ? StageStatus.Succeeded : StageStatus.Skipped, null));

        // The scenario's status: the operator's declaration beside what the machine and the evidence show.
        var performed = request.Disposition == ScenarioDisposition.Performed;
        var unavailable = scenario.RequiredSources.Where(source => SourceStatusOf(prepared.Scrubbed, source).Status != SourceStatus.Ok).ToList();
        var unconfirmed = performed
            ? step.Fields.Where(field => ObservationCatalog.Procedures.Contains(field) && observations.Find(field)?.Text != "yes").Select(field => field.Id).ToList()
            : [];
        var missingEvidence = performed
            ? step.Evidence.Where(expected => !evidence.Any(file => file.Tool == expected.Tool && file.BelongsToStep)).Select(expected => expected.Tool).ToList()
            : [];
        var status = Scenario.StatusOf(request.Disposition, unavailable.Count > 0, unconfirmed.Count == 0, missingEvidence.Count == 0);

        // The annotation record.
        var gameNamesPresent = prepared.HoldsSteamGames || observations.Find(ObservationCatalog.GameName) is { State: ObservationState.Known };
        var unanswered = observations.Unanswered(request.Disposition, gameNamesPresent);
        var asked = (performed ? step.Fields.Count(field => !field.Optional) : 0) + (gameNamesPresent ? 1 : 0);
        var annotations = new ManifestAnnotations(
            unanswered.Count == 0 ? AnnotationStatus.Complete : AnnotationStatus.Incomplete,
            asked,
            observations.Observations.Count(observation => observation.State == ObservationState.Known && !IsNote(observation)),
            observations.Observations.Count(observation => observation.State == ObservationState.Unknown),
            observations.Observations.Count(observation => observation.State == ObservationState.NotApplicable),
            unanswered);

        // The content files.
        var files = new List<(FileRole Role, byte[] Bytes)> { (FileRole.ScrubbedRawSnapshot, prepared.ScrubbedJson) };
        if (prepared.FactsJson is { } factsJson)
        {
            files.Add((FileRole.NormalizedFacts, factsJson));
        }

        if (prepared.ResultsJson is { } resultsJson)
        {
            files.Add((FileRole.CheckResults, resultsJson));
        }

        files.Add((FileRole.OperatorObservations, TrackAJson.Serialize(observations)));

        var findings = new List<PrivacyFinding>();
        foreach (var (role, bytes) in files)
        {
            findings.AddRange(Verify(PackageFiles.NameOf(role), bytes, identifiers, role == FileRole.ScrubbedRawSnapshot ? prepared.Scrubbed : null));
        }

        var consent = observations.Find(ObservationCatalog.GameNamesConsent)?.Text;
        var reasons = new List<string>();
        var app = prepared.Scrubbed.App;
        if (!app.Commit.IsKnown)
        {
            reasons.Add(PackageReasons.BuildNotCommitted);
        }

        if (stages.Any(stage => stage.Status == StageStatus.Failed && stage.Stage != Stages.Determinism))
        {
            reasons.Add(PackageReasons.StageFailed);
        }

        if (prepared.Determinism.Failed)
        {
            reasons.Add(PackageReasons.DeterminismFailed);
        }

        if (gameNamesPresent && consent == "no")
        {
            reasons.Add(PackageReasons.ConsentNotGiven);
        }

        if (unanswered.Count > 0)
        {
            reasons.Add(PackageReasons.AnnotationsIncomplete);
        }

        stages.Add(new(Stages.PrivacyVerification, findings.Count == 0 ? StageStatus.Succeeded : StageStatus.Failed, null));

        var os = prepared.Scrubbed.Os;
        var manifest = new CaptureManifest(
            CaptureManifest.CurrentSchemaVersion,
            CaptureManifest.FormatName,
            request.CohortId,
            request.SessionId,
            prepared.Scrubbed.CapturedAtUtc,
            new ManifestScenario(scenario.Id, request.Disposition, unavailable, unconfirmed, missingEvidence, status, step: step.Name, series: request.Series),
            new ManifestProducer(
                app.Name,
                app.Version,
                new ManifestSchemas(RawSnapshot.CurrentSchemaVersion, NormalizedSnapshot.CurrentSchemaVersion, CheckResults.CurrentSchemaVersion, OperatorObservations.CurrentSchemaVersion, CaptureManifest.CurrentSchemaVersion),
                commit: app.Commit.IsKnown ? app.Commit.Value : null,
                commitMissing: app.Commit.IsKnown ? null : app.Commit.Detail),
            new ManifestPlatform(
                ManifestPlatform.FamilyOf(os.Build),
                os.Build,
                prepared.Scrubbed.Elevated,
                UpdateRevision: os.UpdateRevision.IsKnown ? os.UpdateRevision.Value : null,
                Edition: os.Edition.IsKnown ? os.Edition.Value : null,
                Version: os.Version.IsKnown ? os.Version.Value : null,
                Session: os.Session.IsKnown ? os.Session.Value : null),
            [.. RawSources.All.Select(source => SourceStatusOf(prepared.Scrubbed, source))],
            stages,
            new ManifestChecks(
                prepared.Results?.Coverage.Where(entry => entry.Status == CoverageStatus.Ran).Select(entry => entry.Check).ToList() ?? [], // none when the stage failed
                [.. CheckIds.All.Where(id => !CheckRunner.Implemented.Any(check => check.Id == id))],
                ManifestChecks.FixedStatement),
            prepared.Determinism,
            prepared.RepeatScan,
            new ManifestPrivacy(
                ManifestPrivacy.PseudonymizerScrub,
                prepared.IdsReplaced,
                request.Identifiers.Where(IdentifierLeakCheck.IsCheckable).Select(identifier => identifier.Category).Distinct(StringComparer.Ordinal).Count(),
                [.. request.UncheckedIdentifierKinds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                prepared.OpaqueIdentifiers.Count(IdentifierLeakCheck.IsCheckable),
                [.. files.Select(file => PackageFiles.NameOf(file.Role)), PackageFiles.Manifest],
                request.UncheckedIdentifierKinds.Count == 0 ? PrivacyVerdict.Passed : PrivacyVerdict.PassedWithUncheckedKinds),
            new ManifestPrivateMaterial(request.UnscrubbedSnapshotKept),
            annotations,
            [.. files.Select(file => new ManifestFile(PackageFiles.NameOf(file.Role), file.Role, Sha256(file.Bytes), file.Bytes.LongLength))],
            new ManifestEvidence(evidence, ignored),
            new ManifestPackage(reasons.Count == 0 ? PackageStatus.Ready : PackageStatus.NotPromotable, reasons, FixtureStatus.Incoming));

        var manifestJson = TrackAJson.Serialize(manifest);
        findings.AddRange(Verify(PackageFiles.Manifest, manifestJson, identifiers, null));

        if (findings.Count > 0)
        {
            var report = new PrivacyFailureReport(PrivacyFailureReport.CurrentSchemaVersion, request.CohortId, request.SessionId, findings, PrivacyFailureReport.FixedStatement);
            return new PrivacyFailure(report, TrackAJson.Serialize(report));
        }

        var output = files.ToDictionary(file => PackageFiles.NameOf(file.Role), file => file.Bytes, StringComparer.Ordinal);
        output[PackageFiles.Manifest] = manifestJson;
        return new CapturedPackage(manifest, output);
    }

    /// <summary>Lower-case hex SHA-256.</summary>
    public static string Sha256(byte[] contents) => Convert.ToHexStringLower(SHA256.HashData(contents));

    /// <summary>A package file's privacy verification: this PC's identifiers, then the identifier-free audit.</summary>
    internal static IReadOnlyList<PrivacyFinding> Verify(string file, byte[] json, IReadOnlyList<LocalIdentifier> identifiers, RawSnapshot? scrubbed)
    {
        var findings = IdentifierLeakCheck.FindLeakLocations(json, identifiers)
            .Select(location => new PrivacyFinding(file, "this PC's " + location.Category, location.Pointer, RemediationFor(file, location.Pointer)))
            .ToList();
        findings.AddRange(scrubbed is null ? ShareableAudit.AuditJson(file, json) : ShareableAudit.AuditScrubbedSnapshot(file, json, scrubbed));
        return findings;
    }

    private static string RemediationFor(string file, string pointer) => (file, pointer) switch
    {
        (PackageFiles.Observations, _) => "An answer holds this PC's identifier. Capture again and leave the identifier out of the answer.",
        (PackageFiles.Manifest, _) when pointer.StartsWith("/evidence/", StringComparison.Ordinal) =>
            "An evidence file's name holds this PC's identifier. Rename the file (the convention's names only) and capture again.",
        _ => "A file the kit made from the scan holds this PC's identifier: the scrub misses it. Don't share anything from this session; it is a privacy defect to fix in the product first.",
    };

    private static bool IsNote(Observation observation) => observation.Field == ObservationCatalog.OperatorNote.Id;

    private static ManifestSource SourceStatusOf(RawSnapshot snapshot, string source)
    {
        var sources = snapshot.Sources;
        (SourceStatus Status, MissingReason? Reason) entry = source switch
        {
            RawSources.Display => (sources.Display.Status, sources.Display.Reason),
            RawSources.Edid => (sources.Edid.Status, sources.Edid.Reason),
            RawSources.Adapters => (sources.Adapters.Status, sources.Adapters.Reason),
            RawSources.Smbios => (sources.Smbios.Status, sources.Smbios.Reason),
            RawSources.Pcie => (sources.Pcie.Status, sources.Pcie.Reason),
            RawSources.Storage => (sources.Storage.Status, sources.Storage.Reason),
            RawSources.Steam => (sources.Steam.Status, sources.Steam.Reason),
            RawSources.Power => (sources.Power.Status, sources.Power.Reason),
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
        };

        return new ManifestSource(source, entry.Status, entry.Reason);
    }

    private static T? Attempt<T>(string stage, Func<T> run, List<ManifestStage> stages, Func<string, string> redact)
        where T : class
    {
        try
        {
            var result = run();
            stages.Add(new(stage, StageStatus.Succeeded, null));
            return result;
        }
#pragma warning disable CA1031 // A defect in the normaliser or a check is recorded, so the scrubbed snapshot can reproduce it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            stages.Add(new(stage, StageStatus.Failed, redact($"{ex.GetType().Name}: {ex.Message}")));
            return null;
        }
    }

    private static ManifestDeterminism Determinism(
        byte[] scrubbedJson,
        byte[]? factsJson,
        byte[]? resultsJson,
        Func<RawSnapshot, NormalizedSnapshot> normalize,
        Func<NormalizedSnapshot, CheckResults> check)
    {
        var reread = SnapshotJson.Deserialize(scrubbedJson);
        var roundTrip = SnapshotJson.Serialize(reread).AsSpan().SequenceEqual(scrubbedJson) ? Comparison.Identical : Comparison.Differs;

        if (factsJson is null)
        {
            return new ManifestDeterminism(roundTrip, Comparison.NotRun, Comparison.NotRun, DeterminismCultures, Comparison.NotRun);
        }

        try
        {
            var again = normalize(reread);
            var repeated = FactsJson.Serialize(again).AsSpan().SequenceEqual(factsJson) ? Comparison.Identical : Comparison.Differs;

            var acrossCultures = DeterminismCultures.All(culture => InCulture(culture, () => FactsJson.Serialize(normalize(reread))).AsSpan().SequenceEqual(factsJson))
                ? Comparison.Identical
                : Comparison.Differs;

            var checksRepeated = resultsJson is null
                ? Comparison.NotRun
                : CheckResultsJson.Serialize(check(again)).AsSpan().SequenceEqual(resultsJson) ? Comparison.Identical : Comparison.Differs;

            return new ManifestDeterminism(roundTrip, repeated, acrossCultures, DeterminismCultures, checksRepeated);
        }
#pragma warning disable CA1031 // A normaliser that succeeds once and throws on the same input is itself a determinism failure.
        catch (Exception)
#pragma warning restore CA1031
        {
            return new ManifestDeterminism(roundTrip, Comparison.Differs, Comparison.Differs, DeterminismCultures, Comparison.Differs);
        }
    }

    private static byte[] InCulture(string culture, Func<byte[]> run)
    {
        var (current, currentUi) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            return run();
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (current, currentUi);
        }
    }

    private static ManifestRepeatScan Repeat(
        byte[] scrubbedJson,
        CheckResults? results,
        RawSnapshot repeat,
        Func<RawSnapshot, NormalizedSnapshot> normalize,
        Func<NormalizedSnapshot, CheckResults> check)
    {
        var repeatScrubbed = FixturePseudonymizer.Pseudonymize(repeat).Snapshot;
        var pointers = JsonDiff.Pointers(JsonNode.Parse(scrubbedJson), JsonNode.Parse(SnapshotJson.Serialize(repeatScrubbed)));
        var expected = pointers.Count(pointer => RunDependent().IsMatch(pointer));
        var other = pointers.Where(pointer => !RunDependent().IsMatch(pointer)).ToList();

        var comparison = Comparison.NotRun;
        if (results is not null)
        {
            try
            {
                var first = JsonNode.Parse(CheckResultsJson.Serialize(results))!["results"];
                var second = JsonNode.Parse(CheckResultsJson.Serialize(check(normalize(repeatScrubbed))))!["results"];
                comparison = JsonNode.DeepEquals(first, second) ? Comparison.Identical : Comparison.Differs;
            }
#pragma warning disable CA1031 // The repeat is descriptive: a failure on it is recorded as a difference, never as a stage.
            catch (Exception)
#pragma warning restore CA1031
            {
                comparison = Comparison.Differs;
            }
        }

        return new ManifestRepeatScan(true, expected, [.. other.Take(ManifestRepeatScan.MaxPointers)], other.Count, comparison);
    }

    private static EvidenceTextScan TextScan(EvidenceKind kind, byte[] contents, IReadOnlyList<LocalIdentifier> identifiers)
    {
        if (kind != EvidenceKind.TextOutput)
        {
            return EvidenceTextScan.NotText;
        }

        var text = DecodeText(contents);
        IReadOnlyList<LeakLocation> found;
        try
        {
            found = IdentifierLeakCheck.FindLeakLocations(Encoding.UTF8.GetBytes(text), identifiers);
        }
        catch (JsonException)
        {
            found = IdentifierLeakCheck.FindLeakLocationsInText(text, identifiers);
        }

        return found.Count == 0 ? EvidenceTextScan.NoIdentifierFound : EvidenceTextScan.IdentifierFound;
    }

    /// <summary>UTF-8 or UTF-16, by the byte-order mark (Windows PowerShell 5.1 writes UTF-16 LE by default).</summary>
    private static string DecodeText(byte[] contents) => contents switch
    {
        [0xFF, 0xFE, ..] => Encoding.Unicode.GetString(contents, 2, contents.Length - 2),
        [0xFE, 0xFF, ..] => Encoding.BigEndianUnicode.GetString(contents, 2, contents.Length - 2),
        [0xEF, 0xBB, 0xBF, ..] => Encoding.UTF8.GetString(contents, 3, contents.Length - 3),
        _ => Encoding.UTF8.GetString(contents),
    };

    /// <summary>Values §4.4 documents as run-dependent between two scans of an unchanged PC.</summary>
    [GeneratedRegex(@"^(?:/capturedAtUtc|/sources/[a-z]+/durationMs|/sources/storage/data/volumes/[0-9]+/freeBytes(?:/value)?)$", RegexOptions.CultureInvariant)]
    private static partial Regex RunDependent();
}

/// <summary>
/// Why a capture has no shareable package (<c>privacy-failure.json</c>, written beside the session's folders, never
/// inside <c>shareable</c>): each finding names the file, the category and the JSON Pointer, never the value.
/// </summary>
public sealed record PrivacyFailureReport(int SchemaVersion, string CohortId, string SessionId, IReadOnlyList<PrivacyFinding> Findings, string Statement)
{
    public const int CurrentSchemaVersion = 1;

    public const string FixedStatement =
        "The privacy verification found data that must not leave this PC, so no shareable package was written. Nothing was sent anywhere.";
}
