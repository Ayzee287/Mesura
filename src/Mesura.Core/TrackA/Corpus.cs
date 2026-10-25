using System.Text;

namespace Mesura.Core.TrackA;

/// <summary>
/// The accepted Track A corpus, as committed in the repository (architecture §4.7):
/// <c>track-a/cohort/PC-NNN/session-…/</c> holds exactly the capture's package files, <c>acceptance.json</c> and
/// <c>README.md</c>; <c>track-a/cohort-index.json</c> is generated from them. Reading is strict: an unexpected file, a
/// changed file or a record that doesn't bind to its manifest is refused.
/// </summary>
public static class Corpus
{
    public const string CohortFolder = "cohort";
    public const string IndexFile = "cohort-index.json";
    public const string AcceptanceFile = "acceptance.json";
    public const string ReadmeFile = "README.md";

    /// <summary>The files an accepted session holds, in the order they are written (the manifest last among the capture's).</summary>
    public static IReadOnlyList<string> SessionFiles { get; } =
    [
        PackageFiles.ScrubbedRaw, PackageFiles.Facts, PackageFiles.Results, PackageFiles.Observations, PackageFiles.Manifest, AcceptanceFile, ReadmeFile,
    ];

    /// <summary>Reads one accepted session from its files.</summary>
    /// <exception cref="TrackAFormatException">The session isn't a well-formed accepted session.</exception>
    public static AcceptedSession Read(string cohortId, string sessionId, IReadOnlyDictionary<string, byte[]> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var where = $"{CohortFolder}/{cohortId}/{sessionId}";
        if (!CohortId.IsValid(cohortId) || !SessionId.IsValid(sessionId))
        {
            throw new TrackAFormatException($"{where}: not a cohort id and session id.");
        }

        var unexpected = files.Keys.Where(name => !SessionFiles.Contains(name, StringComparer.Ordinal)).Order(StringComparer.Ordinal).ToList();
        var missing = SessionFiles.Where(name => !files.ContainsKey(name)).ToList();
        if (unexpected.Count > 0 || missing.Count > 0)
        {
            throw new TrackAFormatException($"{where}: missing {string.Join(", ", missing)}; unexpected {unexpected.Count} file(s).");
        }

        var manifest = TrackAJson.Deserialize<CaptureManifest>(files[PackageFiles.Manifest], CaptureManifest.CurrentSchemaVersion);
        var observations = TrackAJson.Deserialize<OperatorObservations>(files[PackageFiles.Observations], OperatorObservations.CurrentSchemaVersion);
        var acceptance = TrackAJson.Deserialize<AcceptanceRecord>(files[AcceptanceFile], AcceptanceRecord.CurrentSchemaVersion);

        if (manifest.CohortId != cohortId || manifest.SessionId != sessionId || observations.CohortId != cohortId || observations.SessionId != sessionId
            || acceptance.CohortId != cohortId || acceptance.SessionId != sessionId)
        {
            throw new TrackAFormatException($"{where}: a file names another cohort id or session.");
        }

        if (acceptance.ManifestSha256 != CapturePipeline.Sha256(files[PackageFiles.Manifest]))
        {
            throw new TrackAFormatException($"{where}: the manifest changed after acceptance.");
        }

        var changed = manifest.Files.Where(file => CapturePipeline.Sha256(files[file.Path]) != file.Sha256).Select(file => file.Path).ToList();
        if (changed.Count > 0 || manifest.Files.Count != 4)
        {
            throw new TrackAFormatException($"{where}: changed since capture: {string.Join(", ", changed)}.");
        }

        return new AcceptedSession(manifest, observations, acceptance);
    }

    /// <summary>A session's generated README, as bytes (UTF-8, <c>\n</c>).</summary>
    public static byte[] Readme(AcceptedSession session) => Encoding.UTF8.GetBytes(SessionReadme.Render(session));
}
