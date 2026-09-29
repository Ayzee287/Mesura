using FpsUtility.Core.Privacy;
using FpsUtility.Core.Raw;
using FpsUtility.Core.Tests.Normalize;
using FpsUtility.Core.TrackA;

namespace FpsUtility.Core.Tests.TrackA;

/// <summary>
/// Track A captures of the synthetic snapshot (made-up data, no real machine), with a valid scrubbed EDID so the
/// structural audit can walk it. The "PC's own identifiers" here are invented values planted where a test needs them.
/// </summary>
internal static class TrackAFixture
{
    public const string Cohort = "PC-042";
    public const string Commit = "0123456789abcdef0123456789abcdef01234567";

    public static readonly DateTimeOffset Started = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    public static readonly string Session = SessionId.At(Started);

    /// <summary>A serial number this "PC" has; tests plant it to prove the check finds it.</summary>
    public static readonly LocalIdentifier Serial = new("system serial number", "SN7F3K2Q9X");

    /// <summary>The synthetic snapshot with a real, scrubbed EDID base block.</summary>
    public static RawSnapshot Raw(bool reversed = false, Func<SnapshotSources, SnapshotSources>? change = null)
    {
        var raw = SnapshotFixture.Create(reversed);
        var edid = raw.Sources.Edid.Data!;
        var scrubbed = EdidScrubber.Scrub(RawBuilder.EdidBaseBlock());
        var monitors = edid.Monitors.Select(monitor => monitor.Edid.IsKnown
            ? new MonitorEdid(monitor.MonitorDevicePath, Reading.Known(new EdidPayload(scrubbed.Bytes, scrubbed.ChecksumValidBeforeScrub, 0, Reading.Known(false))))
            : monitor);
        var sources = raw.Sources with { Edid = SourceEntry.Ok("test.edid", 3, new EdidData([.. monitors])) };
        return new RawSnapshot(raw.SchemaVersion, raw.CapturedAtUtc, raw.App, raw.Os, raw.Elevated, change is null ? sources : change(sources));
    }

    public static RawSnapshot WithApp(RawSnapshot raw, AppInfo app) => new(raw.SchemaVersion, raw.CapturedAtUtc, app, raw.Os, raw.Elevated, raw.Sources);

    public static CapturePreparation Prepare(RawSnapshot? raw = null, RawSnapshot? repeat = null) =>
        CapturePipeline.Prepare(raw ?? Raw(), repeat, text => text);

    public static Observation Answer(ObservationField field, string answer) =>
        field.Read(answer).Observation ?? throw new InvalidOperationException($"'{answer}' is not an answer to {field.Id}.");

    /// <summary>Every question of a step answered: the given answers, "unknown" for the rest, and the consent.</summary>
    public static Observation[] Answers(string scenario, string? step, params (ObservationField Field, string Answer)[] given) =>
    [
        Consent(),
        .. ScenarioCatalog.Require(scenario).Step(step).Fields.Select(field =>
            Answer(field, given.FirstOrDefault(pair => pair.Field == field).Answer ?? "")),
    ];

    /// <summary>The synthetic snapshot holds Steam app manifests, so a ready package needs the consent.</summary>
    public static Observation Consent(string answer = "yes") => Answer(ObservationCatalog.GameNamesConsent, answer);

    public static OperatorObservations Observations(string scenario, string? step, string session, params Observation[] answers) =>
        new(OperatorObservations.CurrentSchemaVersion, Cohort, session, scenario, answers, step);

    /// <summary>A baseline capture, answered, with no evidence.</summary>
    public static CaptureOutcome Baseline(
        CapturePreparation? prepared = null,
        string? session = null,
        IReadOnlyList<LocalIdentifier>? identifiers = null,
        IReadOnlyList<string>? unread = null,
        Observation[]? answers = null,
        IReadOnlyList<EvidenceFile>? evidence = null,
        ScenarioDisposition disposition = ScenarioDisposition.Performed) =>
        Capture(ScenarioCatalog.Baseline, null, prepared, session, identifiers, unread,
            answers ?? [Consent(), Answer(ObservationCatalog.PhysicalChassisType, "desktop")], evidence, disposition);

    public static CaptureOutcome Capture(
        string scenario,
        string? step,
        CapturePreparation? prepared = null,
        string? session = null,
        IReadOnlyList<LocalIdentifier>? identifiers = null,
        IReadOnlyList<string>? unread = null,
        Observation[]? answers = null,
        IReadOnlyList<EvidenceFile>? evidence = null,
        ScenarioDisposition disposition = ScenarioDisposition.Performed,
        ManifestSeries? series = null)
    {
        session ??= Session;
        if (step is not null && series is null)
        {
            series = new ManifestSeries(session);
        }

        return CapturePipeline.Package(new CaptureRequest(
            Cohort, session, scenario, step, series, disposition, prepared ?? Prepare(),
            Observations(scenario, step, session, answers ?? [Consent()]),
            evidence ?? [], identifiers ?? [Serial], unread ?? [], UnscrubbedSnapshotKept: false));
    }

    public static CapturedPackage Package(CaptureOutcome outcome) =>
        outcome as CapturedPackage ?? throw new InvalidOperationException(
            "Expected a package, got a privacy failure: " + string.Join("; ", ((PrivacyFailure)outcome).Report.Findings.Select(finding => $"{finding.File} {finding.Pointer} {finding.Category}")));

    public static IncomingSession Incoming(CapturedPackage package, IReadOnlyDictionary<string, byte[]>? evidence = null) =>
        new(package.Files, evidence ?? new Dictionary<string, byte[]>());

    /// <summary>A snapshot text value planted in the Steam library file, where no scrub looks for serials.</summary>
    public static RawSnapshot WithSteamText(string text)
    {
        var raw = Raw();
        var steam = raw.Sources.Steam.Data!;
        var planted = new SteamData(steam.InstallPath, Reading.Known(new SteamFile(steam.LibraryFolders.Value!.Path, text)), steam.Libraries);
        return new RawSnapshot(raw.SchemaVersion, raw.CapturedAtUtc, raw.App, raw.Os, raw.Elevated, raw.Sources with { Steam = SourceEntry.Ok("test.steam", 5, planted) });
    }
}
