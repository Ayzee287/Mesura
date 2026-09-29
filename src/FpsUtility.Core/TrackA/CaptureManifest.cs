using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FpsUtility.Core.Raw;
using FpsUtility.Core.Raw.Serialization;

namespace FpsUtility.Core.TrackA;

/// <summary>
/// <c>capture-manifest.json</c> (architecture §4.7): what the capture process did, never what the machine is. It names the
/// session by the operator's cohort id and a time-based session id, never by a hardware identifier, and it holds no
/// value the scan read except the platform's version fields. It is written last, after the rest of the package passed
/// the privacy verification, and it lists every other file with its SHA-256.
/// </summary>
public sealed partial record CaptureManifest
{
    public const int CurrentSchemaVersion = 1;
    public const string FormatName = "fpsutility-track-a-capture";

    [JsonConstructor]
    public CaptureManifest(
        int schemaVersion,
        string format,
        string cohortId,
        string sessionId,
        DateTimeOffset capturedAtUtc,
        ManifestScenario scenario,
        ManifestProducer producer,
        ManifestPlatform platform,
        IReadOnlyList<ManifestSource> sources,
        IReadOnlyList<ManifestStage> stages,
        ManifestChecks checks,
        ManifestDeterminism determinism,
        ManifestRepeatScan repeatScan,
        ManifestPrivacy privacy,
        ManifestPrivateMaterial privateMaterial,
        ManifestAnnotations annotations,
        IReadOnlyList<ManifestFile> files,
        ManifestEvidence evidence,
        ManifestPackage package)
    {
        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion), schemaVersion, $"Only capture-manifest schema version {CurrentSchemaVersion} exists.");
        }

        if (format != FormatName)
        {
            throw new ArgumentException($"The format is '{FormatName}'.", nameof(format));
        }

        if (capturedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The capture time is in UTC.", nameof(capturedAtUtc));
        }

        NoNulls(sources, nameof(sources));
        NoNulls(stages, nameof(stages));
        NoNulls(files, nameof(files));

        if (files.DistinctBy(file => file.Path).Count() != files.Count || files.DistinctBy(file => file.Role).Count() != files.Count)
        {
            throw new ArgumentException("Each file and each role is listed once.", nameof(files));
        }

        if (sources.Select(source => source.Source).Order(StringComparer.Ordinal).SequenceEqual(RawSources.All.Order(StringComparer.Ordinal)) is false)
        {
            throw new ArgumentException("Every raw source is listed once.", nameof(sources));
        }

        SchemaVersion = schemaVersion;
        Format = format;
        CohortId = TrackA.CohortId.Require(cohortId, nameof(cohortId));
        SessionId = TrackA.SessionId.Require(sessionId, nameof(sessionId));
        CapturedAtUtc = capturedAtUtc;
        Scenario = scenario ?? throw new ArgumentNullException(nameof(scenario));
        Producer = producer ?? throw new ArgumentNullException(nameof(producer));
        Platform = platform ?? throw new ArgumentNullException(nameof(platform));
        Sources = [.. sources];
        Stages = [.. stages];
        Checks = checks ?? throw new ArgumentNullException(nameof(checks));
        Determinism = determinism ?? throw new ArgumentNullException(nameof(determinism));
        RepeatScan = repeatScan ?? throw new ArgumentNullException(nameof(repeatScan));
        Privacy = privacy ?? throw new ArgumentNullException(nameof(privacy));
        PrivateMaterial = privateMaterial ?? throw new ArgumentNullException(nameof(privateMaterial));
        Annotations = annotations ?? throw new ArgumentNullException(nameof(annotations));
        Files = [.. files];
        Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        Package = package ?? throw new ArgumentNullException(nameof(package));
    }

    public int SchemaVersion { get; }

    /// <summary>Always <see cref="FormatName"/>: says what the file is to someone who finds it alone.</summary>
    public string Format { get; }

    /// <summary>The operator-assigned pseudonymous PC id (<c>PC-NNN</c>).</summary>
    public string CohortId { get; }

    /// <summary>The session, from the clock only. Two sessions of one cohort id are the same PC.</summary>
    public string SessionId { get; }

    /// <summary>When the scan ran (the raw snapshot's own time).</summary>
    public DateTimeOffset CapturedAtUtc { get; }

    public ManifestScenario Scenario { get; }

    public ManifestProducer Producer { get; }

    public ManifestPlatform Platform { get; }

    /// <summary>Every raw source and whether it could be read: which capabilities this capture actually holds.</summary>
    public IReadOnlyList<ManifestSource> Sources { get; }

    public IReadOnlyList<ManifestStage> Stages { get; }

    public ManifestChecks Checks { get; }

    public ManifestDeterminism Determinism { get; }

    public ManifestRepeatScan RepeatScan { get; }

    public ManifestPrivacy Privacy { get; }

    public ManifestPrivateMaterial PrivateMaterial { get; }

    public ManifestAnnotations Annotations { get; }

    /// <summary>The package's other files (not this manifest), each with its SHA-256.</summary>
    public IReadOnlyList<ManifestFile> Files { get; }

    public ManifestEvidence Evidence { get; }

    public ManifestPackage Package { get; }

    internal static bool IsSha256(string? value) => value is not null && Sha256Pattern().IsMatch(value);

    internal static string Token(string? value, string what) =>
        value is not null && TokenPattern().IsMatch(value) ? value : throw new ArgumentException($"{what} '{value}' is not a token.", what);

    private static void NoNulls<T>(IReadOnlyList<T> list, string what)
    {
        ArgumentNullException.ThrowIfNull(list, what);
        if (list.Any(item => item is null))
        {
            throw new ArgumentException($"{what} has no null item.", what);
        }
    }

    [GeneratedRegex("^[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex("^[a-z0-9][A-Za-z0-9.:-]*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}

/// <summary>Which scenario the operator declared, and what decided its status.</summary>
public sealed record ManifestScenario
{
    /// <param name="id">The scenario id.</param>
    /// <param name="step"><c>idle</c> or <c>load</c> for the PCIe spike; otherwise absent.</param>
    /// <param name="series">For a two-step scenario, which session pair this belongs to.</param>
    /// <param name="operatorDisposition">Operator-declared: carried out, not applicable, or skipped.</param>
    /// <param name="unavailableRequiredSources">Machine-observed: the required sources the scan couldn't read.</param>
    /// <param name="unconfirmedProcedures">Operator-declared: procedure fields not answered "yes".</param>
    /// <param name="missingEvidenceTools">Evidence the step expects and no file supplied.</param>
    /// <param name="status">Must be what <see cref="Scenario.StatusOf"/> gives for the rest, so it can't be claimed.</param>
    [JsonConstructor]
    public ManifestScenario(
        string id,
        ScenarioDisposition operatorDisposition,
        IReadOnlyList<string> unavailableRequiredSources,
        IReadOnlyList<string> unconfirmedProcedures,
        IReadOnlyList<EvidenceTool> missingEvidenceTools,
        ScenarioStatus status,
        string? step = null,
        ManifestSeries? series = null)
    {
        var scenario = ScenarioCatalog.Require(id);
        Step = scenario.Step(step).Name;
        Guard.NoNulls(unavailableRequiredSources, nameof(unavailableRequiredSources));
        Guard.NoNulls(unconfirmedProcedures, nameof(unconfirmedProcedures));
        ArgumentNullException.ThrowIfNull(missingEvidenceTools);

        if (missingEvidenceTools.Any(tool => !scenario.Step(step).Evidence.Any(expected => expected.Tool == tool)))
        {
            throw new ArgumentException("Only evidence the step expects can be missing.", nameof(missingEvidenceTools));
        }

        if ((scenario.Steps.Count > 1) != (series is not null))
        {
            throw new ArgumentException("A two-step scenario's session names its series; a one-step one has none.", nameof(series));
        }

        if (unavailableRequiredSources.Any(source => !scenario.RequiredSources.Contains(source)))
        {
            throw new ArgumentException("Only the scenario's required sources are listed.", nameof(unavailableRequiredSources));
        }

        if (unconfirmedProcedures.Any(field => !ObservationCatalog.Procedures.Any(procedure => procedure.Id == field)))
        {
            throw new ArgumentException("Only procedure fields are listed as unconfirmed.", nameof(unconfirmedProcedures));
        }

        var expected = Scenario.StatusOf(operatorDisposition, unavailableRequiredSources.Count > 0, unconfirmedProcedures.Count == 0, missingEvidenceTools.Count == 0);
        if (status != expected)
        {
            throw new ArgumentException($"The status follows from the rest: {expected}, not {status}.", nameof(status));
        }

        Id = scenario.Id;
        Series = series;
        OperatorDisposition = operatorDisposition;
        UnavailableRequiredSources = [.. unavailableRequiredSources];
        UnconfirmedProcedures = [.. unconfirmedProcedures];
        MissingEvidenceTools = [.. missingEvidenceTools];
        Status = status;
    }

    public string Id { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Step { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ManifestSeries? Series { get; }

    public ScenarioDisposition OperatorDisposition { get; }

    public IReadOnlyList<string> UnavailableRequiredSources { get; }

    public IReadOnlyList<string> UnconfirmedProcedures { get; }

    public IReadOnlyList<EvidenceTool> MissingEvidenceTools { get; }

    public ScenarioStatus Status { get; }
}

/// <summary>The PCIe spike's pair: both sessions carry the idle session's id as the series id.</summary>
/// <param name="Id">The idle session's id.</param>
/// <param name="PairedSession">For the load step, the idle session it follows; absent for the idle step.</param>
/// <param name="SecondsSincePairedCapture">For the load step, the time between the two scans.</param>
/// <param name="SameModelHardware">For the load step, whether both scans list the same graphics and PCI device models (model identity only, never a unit identity).</param>
public sealed record ManifestSeries(
    string Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PairedSession = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? SecondsSincePairedCapture = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? SameModelHardware = null)
{
    private readonly bool _idIsSession = SessionId.IsValid(Id) ? true : throw new ArgumentException($"'{Id}' is not a session id.", nameof(Id));

    private readonly bool _pairedIsSession = PairedSession is null || SessionId.IsValid(PairedSession)
        ? true
        : throw new ArgumentException($"'{PairedSession}' is not a session id.", nameof(PairedSession));
}

/// <summary>The build that produced the package (spec §21): a fixture from one commit stays distinguishable from a later one's.</summary>
public sealed record ManifestProducer
{
    /// <param name="commit">The source commit; absent when the build had uncommitted changes or no stamp.</param>
    /// <param name="commitMissing">Why there is no commit, when there isn't.</param>
    [JsonConstructor]
    public ManifestProducer(string name, string version, ManifestSchemas schemas, string? commit = null, string? commitMissing = null)
    {
        if ((commit is null) == (commitMissing is null))
        {
            throw new ArgumentException("A producer names its commit, or why it has none.", nameof(commit));
        }

        Name = name ?? throw new ArgumentNullException(nameof(name));
        Version = version ?? throw new ArgumentNullException(nameof(version));
        Commit = commit;
        CommitMissing = commitMissing;
        Schemas = schemas ?? throw new ArgumentNullException(nameof(schemas));
    }

    public string Name { get; }

    public string Version { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Commit { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CommitMissing { get; }

    public ManifestSchemas Schemas { get; }
}

public sealed record ManifestSchemas(int Raw, int Facts, int CheckResults, int Observations, int Manifest);

[JsonConverter(typeof(CamelCaseEnumConverter<WindowsFamily>))]
public enum WindowsFamily
{
    /// <summary>Build 19045 (22H2) up to 21999. The .NET 10 runtime isn't Microsoft-supported here; FPS Utility's own testing covers it (D008).</summary>
    Windows10,

    /// <summary>Build 22000 or later.</summary>
    Windows11,
}

/// <summary>
/// The platform, as the product's own startup check read and accepted it (D008, D011, D013). An unsupported Windows,
/// Windows Server or a non-x64 PC never reaches a manifest: the kit refuses before collecting anything.
/// </summary>
public sealed record ManifestPlatform(
    WindowsFamily Family,
    int Build,
    bool Elevated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? UpdateRevision = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Edition = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Version = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SessionType? Session = null)
{
    public static WindowsFamily FamilyOf(int build) => build >= 22000 ? WindowsFamily.Windows11 : WindowsFamily.Windows10;
}

public sealed record ManifestSource(
    string Source,
    SourceStatus Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MissingReason? Reason = null);

[JsonConverter(typeof(CamelCaseEnumConverter<StageStatus>))]
public enum StageStatus
{
    Succeeded,
    Failed,

    /// <summary>Not run: an earlier stage failed, or the scenario doesn't use it.</summary>
    Skipped,
}

/// <param name="Stage">One of <see cref="Stages"/>.</param>
/// <param name="Detail">For a failed stage that ran on scrubbed data: the exception type and message, profile path rewritten. Never a stack trace.</param>
public sealed record ManifestStage(
    string Stage,
    StageStatus Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail = null)
{
    private readonly bool _isStage = Stages.All.Contains(Stage) ? true : throw new ArgumentException($"'{Stage}' is not a capture stage.", nameof(Stage));
}

/// <summary>The capture stages, in the order they run.</summary>
public static class Stages
{
    public const string PlatformCheck = "platformCheck";
    public const string Scan = "scan";
    public const string RepeatScan = "repeatScan";
    public const string Scrub = "scrub";
    public const string Normalize = "normalize";
    public const string Checks = "checks";
    public const string Determinism = "determinism";
    public const string Annotation = "annotation";
    public const string Evidence = "evidence";
    public const string PrivacyVerification = "privacyVerification";

    public static IReadOnlyList<string> All { get; } = [PlatformCheck, Scan, RepeatScan, Scrub, Normalize, Checks, Determinism, Annotation, Evidence, PrivacyVerification];
}

/// <summary>
/// Which checks ran and which didn't, from the check results' own coverage (§4.6). The statement is fixed text, so
/// "checks 1 and 2 ran" can never be read as "the machine passed every check".
/// </summary>
public sealed record ManifestChecks(IReadOnlyList<string> Ran, IReadOnlyList<string> NotImplemented, string Statement)
{
    private readonly bool _lists = Guard.NoNulls(Ran, nameof(Ran)) && Guard.NoNulls(NotImplemented, nameof(NotImplemented));

    public const string FixedStatement =
        "Only the checks listed under 'ran' were evaluated. The others are not implemented in this build: this capture says nothing about them, and no verdict here is a pass of all checks.";

    private readonly bool _isFixed = Statement == FixedStatement ? true : throw new ArgumentException("The statement is the fixed coverage statement.", nameof(Statement));
}

[JsonConverter(typeof(CamelCaseEnumConverter<Comparison>))]
public enum Comparison
{
    Identical,
    Differs,

    /// <summary>Not compared, because an earlier stage failed.</summary>
    NotRun,
}

/// <summary>
/// The determinism the existing contracts promise, checked on this capture (§4.4, §4.5, §4.6): the scrubbed snapshot
/// reads back to the same bytes; normalising it twice, and under other cultures, gives the same facts; the checks give
/// the same results twice. The facts hold the raw snapshot's own capture time, a value of the input, so a pure function
/// of the input still gives identical bytes. The manifest's session time never reaches them.
/// </summary>
public sealed record ManifestDeterminism(
    Comparison ScrubbedRoundTrip,
    Comparison NormalizeRepeated,
    Comparison NormalizeAcrossCultures,
    IReadOnlyList<string> Cultures,
    Comparison ChecksRepeated)
{
    private readonly bool _lists = Guard.NoNulls(Cultures, nameof(Cultures));

    /// <summary>Every comparison ran and gave identical bytes.</summary>
    [JsonIgnore]
    public bool Passed => All.All(comparison => comparison == Comparison.Identical);

    /// <summary>A comparison that ran gave different bytes: a determinism defect. A comparison not run (an earlier stage failed) is not one.</summary>
    [JsonIgnore]
    public bool Failed => All.Any(comparison => comparison == Comparison.Differs);

    private Comparison[] All => [ScrubbedRoundTrip, NormalizeRepeated, NormalizeAcrossCultures, ChecksRepeated];
}

/// <summary>
/// A second scan straight after the first (not for in-game steps, whose load window stays short). Descriptive, never a
/// gate: the vault's Track A procedure asks for two captures and a record of what differed.
/// </summary>
/// <param name="ExpectedDifferences">Differing values that §4.4 documents as run-dependent (capture time, durations, free space), counted.</param>
/// <param name="OtherDifferences">Every other differing value, as JSON Pointers into the scrubbed raw snapshot (at most <see cref="MaxPointers"/>).</param>
/// <param name="OtherDifferenceCount">How many other values differed in all.</param>
/// <param name="Results">Whether the checks gave the same results on both scans (subjects, verdicts, certainty, trace).</param>
public sealed record ManifestRepeatScan(
    bool Performed,
    int ExpectedDifferences,
    IReadOnlyList<string> OtherDifferences,
    int OtherDifferenceCount,
    Comparison Results)
{
    public const int MaxPointers = 100;

    private readonly bool _lists = Guard.NoNulls(OtherDifferences, nameof(OtherDifferences));
}

[JsonConverter(typeof(CamelCaseEnumConverter<PrivacyVerdict>))]
public enum PrivacyVerdict
{
    /// <summary>Every check passed, and every kind of identifier on the PC could be read and compared.</summary>
    Passed,

    /// <summary>Every check passed, but some kinds of identifier couldn't be read on the PC, so nothing proved they are absent; acceptance needs a person to acknowledge each.</summary>
    PassedWithUncheckedKinds,
}

/// <summary>
/// The privacy verification of the whole package, on the capturing PC: the scrub (the existing pseudonymiser), the
/// identifier check against this PC's own identifiers and the opaque ids the scrub replaced (every file, the manifest
/// included), and the identifier-free audit. A package is written only when all of them pass.
/// </summary>
/// <param name="IdentifierKindsChecked">How many kinds of this PC's identifiers were compared.</param>
/// <param name="UncheckedIdentifierKinds">Kinds that couldn't be read on this PC.</param>
/// <param name="OpaqueIdsChecked">How many of the scan's own opaque ids (LUIDs, PnP instance parts, volume GUIDs) were compared.</param>
public sealed record ManifestPrivacy(
    string Scrub,
    int IdsReplaced,
    int IdentifierKindsChecked,
    IReadOnlyList<string> UncheckedIdentifierKinds,
    int OpaqueIdsChecked,
    IReadOnlyList<string> FilesVerified,
    PrivacyVerdict Verdict)
{
    public const string PseudonymizerScrub = "fixturePseudonymizer";

    private readonly bool _lists = Guard.NoNulls(UncheckedIdentifierKinds, nameof(UncheckedIdentifierKinds)) && Guard.NoNulls(FilesVerified, nameof(FilesVerified));

    private readonly bool _verdictFits = (Verdict == PrivacyVerdict.Passed) == (UncheckedIdentifierKinds?.Count == 0)
        ? true
        : throw new ArgumentException("The verdict says whether any identifier kind went unchecked.", nameof(Verdict));
}

/// <summary>Whether this session kept private material outside the package (opt-in; never shared, never committed).</summary>
public sealed record ManifestPrivateMaterial(bool UnscrubbedSnapshotKept);

[JsonConverter(typeof(CamelCaseEnumConverter<AnnotationStatus>))]
public enum AnnotationStatus
{
    /// <summary>Every question the session needed has an answer (a value, unknown or not applicable).</summary>
    Complete,
    Incomplete,
}

public sealed record ManifestAnnotations(AnnotationStatus Status, int Asked, int Known, int Unknown, int NotApplicable, IReadOnlyList<string> Unanswered)
{
    private readonly bool _fits = Guard.NoNulls(Unanswered, nameof(Unanswered)) && (Status == AnnotationStatus.Complete) == (Unanswered.Count == 0)
        ? true
        : throw new ArgumentException("Annotations are complete exactly when nothing is unanswered.", nameof(Status));
}

[JsonConverter(typeof(CamelCaseEnumConverter<FileRole>))]
public enum FileRole
{
    ScrubbedRawSnapshot,
    NormalizedFacts,
    CheckResults,
    OperatorObservations,
}

public sealed record ManifestFile(string Path, FileRole Role, string Sha256, long Bytes)
{
    private readonly bool _pathFits = PackageFiles.NameOf(Role) == Path ? true : throw new ArgumentException($"A {Role} file is named {PackageFiles.NameOf(Role)}.", nameof(Path));

    private readonly bool _isSha256 = CaptureManifest.IsSha256(Sha256) ? true : throw new ArgumentException("A SHA-256 is 64 lower-case hex digits.", nameof(Sha256));
}

[JsonConverter(typeof(CamelCaseEnumConverter<EvidenceReview>))]
public enum EvidenceReview
{
    /// <summary>Not yet reviewed by a person. Evidence is never trusted automatically; acceptance records the decision.</summary>
    Pending,
}

/// <summary>External evidence the operator supplied in the session's <c>evidence</c> folder. Not part of the package, never interpreted.</summary>
/// <param name="Files">The files that follow the naming convention.</param>
/// <param name="Ignored">How many other files were there: not recorded, not even by name.</param>
public sealed record ManifestEvidence(IReadOnlyList<ManifestEvidenceFile> Files, int Ignored)
{
    private readonly bool _lists = Guard.NoNulls(Files, nameof(Files));
}

public sealed record ManifestEvidenceFile(
    string File,
    EvidenceTool Tool,
    EvidenceState State,
    EvidenceKind Kind,
    bool BelongsToStep,
    string Sha256,
    long Bytes,
    EvidenceTextScan TextScan,
    EvidenceReview Review)
{
    private readonly bool _fileFits = EvidenceConvention.Read(File) == new EvidenceName(Tool, State, Kind)
        ? true
        : throw new ArgumentException($"'{File}' isn't named by the evidence convention as a {Tool} {State} {Kind}.", nameof(File));

    private readonly bool _isSha256 = CaptureManifest.IsSha256(Sha256) ? true : throw new ArgumentException("A SHA-256 is 64 lower-case hex digits.", nameof(Sha256));
}

[JsonConverter(typeof(CamelCaseEnumConverter<PackageStatus>))]
public enum PackageStatus
{
    /// <summary>Privacy-verified and complete: it can be offered for acceptance.</summary>
    Ready,

    /// <summary>Privacy-verified, so it may be handed to the owner, but it can't join the corpus (the reasons say why).</summary>
    NotPromotable,
}

[JsonConverter(typeof(CamelCaseEnumConverter<FixtureStatus>))]
public enum FixtureStatus
{
    /// <summary>Every package starts here; only acceptance (<c>acceptance.json</c> in the corpus) makes it an accepted cohort fixture.</summary>
    Incoming,
}

/// <param name="Reasons">Tokens from <see cref="PackageReasons"/>.</param>
public sealed record ManifestPackage(PackageStatus Status, IReadOnlyList<string> Reasons, FixtureStatus Fixture)
{
    private readonly bool _statusFits = Guard.NoNulls(Reasons, nameof(Reasons)) && (Status == PackageStatus.Ready) == (Reasons.Count == 0)
        ? true
        : throw new ArgumentException("A package is ready exactly when nothing stops it.", nameof(Status));
}

/// <summary>Why a package can't join the corpus.</summary>
public static class PackageReasons
{
    public const string BuildNotCommitted = "buildNotCommitted";
    public const string StageFailed = "stageFailed";
    public const string DeterminismFailed = "determinismFailed";
    public const string ConsentNotGiven = "gameNamesConsentNotGiven";
    public const string AnnotationsIncomplete = "annotationsIncomplete";
}

/// <summary>The package's fixed file names.</summary>
public static class PackageFiles
{
    public const string Manifest = "capture-manifest.json";
    public const string ScrubbedRaw = "raw.scrubbed.json";
    public const string Facts = "normalized.json";
    public const string Results = "check-results.json";
    public const string Observations = "operator-observations.json";

    public static string NameOf(FileRole role) => role switch
    {
        FileRole.ScrubbedRawSnapshot => ScrubbedRaw,
        FileRole.NormalizedFacts => Facts,
        FileRole.CheckResults => Results,
        FileRole.OperatorObservations => Observations,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };
}
