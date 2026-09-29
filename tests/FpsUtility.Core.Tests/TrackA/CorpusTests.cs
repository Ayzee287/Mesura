using System.Text;
using FpsUtility.Core.Checks;
using FpsUtility.Core.Checks.Serialization;
using FpsUtility.Core.Facts.Serialization;
using FpsUtility.Core.Normalize;
using FpsUtility.Core.Privacy;
using FpsUtility.Core.Raw;
using FpsUtility.Core.Raw.Serialization;
using FpsUtility.Core.TrackA;

namespace FpsUtility.Core.Tests.TrackA;

/// <summary>
/// The committed Track A corpus (<c>track-a/</c>, architecture §4.7). Every accepted session must still read strictly,
/// match its hashes, bind to its acceptance record, pass the privacy audit and keep LF line ends; its README and the
/// cohort index must be exactly what the code generates from it. Stored facts and results are the producing commit's
/// record and are not compared with this build (future replay work owns that); the scrubbed snapshot must still read
/// and normalise deterministically while its raw schema is current.
/// </summary>
public sealed class CorpusTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "TrackA", "Corpus");
    private static readonly string Cohort = Path.Combine(Root, Corpus.CohortFolder);

    public static TheoryData<string, string> Sessions()
    {
        var data = new TheoryData<string, string>();
        foreach (var (cohortId, sessionId) in SessionFolders())
        {
            data.Add(cohortId, sessionId);
        }

        return data;
    }

    private static IEnumerable<(string CohortId, string SessionId)> SessionFolders()
    {
        if (!Directory.Exists(Cohort))
        {
            yield break;
        }

        foreach (var machine in Directory.EnumerateDirectories(Cohort).Order(StringComparer.Ordinal))
        {
            foreach (var session in Directory.EnumerateDirectories(machine).Order(StringComparer.Ordinal))
            {
                yield return (Path.GetFileName(machine), Path.GetFileName(session));
            }
        }
    }

    [Fact]
    public void The_corpus_folder_holds_its_readme_the_cohort_and_the_index_only()
    {
        Assert.True(File.Exists(Path.Combine(Root, "README.md")), "track-a/README.md is part of the corpus");
        Assert.All(Directory.EnumerateFiles(Root), file => Assert.Contains(Path.GetFileName(file), new[] { "README.md", Corpus.IndexFile }));
        Assert.All(Directory.EnumerateDirectories(Root), folder => Assert.Equal(Corpus.CohortFolder, Path.GetFileName(folder)));
        if (Directory.Exists(Cohort))
        {
            Assert.Empty(Directory.EnumerateFiles(Cohort));
            Assert.All(Directory.EnumerateDirectories(Cohort), machine =>
            {
                Assert.True(CohortId.IsValid(Path.GetFileName(machine)), machine);
                Assert.Empty(Directory.EnumerateFiles(machine));
                Assert.All(Directory.EnumerateDirectories(machine), session => Assert.True(SessionId.IsValid(Path.GetFileName(session)), session));
            });
        }
    }

    [Fact]
    public void The_committed_index_is_exactly_what_the_accepted_sessions_generate()
    {
        var sessions = SessionFolders().Select(folder => Read(folder.CohortId, folder.SessionId).Session).ToList();

        var generated = TrackAJson.Serialize(CohortIndex.Build(sessions));

        Assert.Equal(Encoding.UTF8.GetString(generated), File.ReadAllText(Path.Combine(Root, Corpus.IndexFile)));
        Assert.Empty(ShareableAudit.AuditJson(Corpus.IndexFile, generated));
    }

    [Theory(SkipTestWithoutData = true)] // the corpus may hold no session yet
    [MemberData(nameof(Sessions))]
    public void A_session_reads_strictly_and_its_files_are_unchanged(string cohortId, string sessionId)
    {
        var (session, files) = Read(cohortId, sessionId);

        Assert.Equal(PackageStatus.Ready, session.Manifest.Package.Status);
        Assert.NotNull(session.Manifest.Producer.Commit);
        Assert.NotNull(session.Acceptance.AcceptedWith.Commit);
        Assert.True(session.Acceptance.Review.Confirmed);
        Assert.All(files, pair => Assert.DoesNotContain((byte)'\r', pair.Value)); // hashed byte for byte: LF only
    }

    [Theory(SkipTestWithoutData = true)]
    [MemberData(nameof(Sessions))]
    public void A_session_s_readme_is_the_generated_one(string cohortId, string sessionId)
    {
        var (session, files) = Read(cohortId, sessionId);

        Assert.Equal(SessionReadme.Render(session), Encoding.UTF8.GetString(files[Corpus.ReadmeFile]));
    }

    [Theory(SkipTestWithoutData = true)]
    [MemberData(nameof(Sessions))]
    public void Every_file_of_a_session_passes_the_privacy_audit(string cohortId, string sessionId)
    {
        var (session, files) = Read(cohortId, sessionId);

        foreach (var (name, bytes) in files.Where(pair => pair.Key != Corpus.ReadmeFile && pair.Key != PackageFiles.ScrubbedRaw))
        {
            Assert.Empty(ShareableAudit.AuditJson(name, bytes));
        }

        Assert.Empty(ShareableAudit.AuditText(Corpus.ReadmeFile, Encoding.UTF8.GetString(files[Corpus.ReadmeFile])));

        if (session.Manifest.Producer.Schemas.Raw == RawSnapshot.CurrentSchemaVersion)
        {
            var raw = SnapshotJson.Deserialize(files[PackageFiles.ScrubbedRaw]);
            Assert.Empty(ShareableAudit.AuditScrubbedSnapshot(PackageFiles.ScrubbedRaw, files[PackageFiles.ScrubbedRaw], raw));
            Assert.True(raw.App.Commit.IsKnown);
        }
        else
        {
            Assert.Empty(ShareableAudit.AuditJson(PackageFiles.ScrubbedRaw, files[PackageFiles.ScrubbedRaw])); // superseded: patterns only
        }
    }

    [Theory(SkipTestWithoutData = true)]
    [MemberData(nameof(Sessions))]
    public void A_current_schema_session_still_replays_deterministically(string cohortId, string sessionId)
    {
        var (session, files) = Read(cohortId, sessionId);
        if (session.Manifest.Producer.Schemas.Raw != RawSnapshot.CurrentSchemaVersion)
        {
            return; // superseded, like §4.4's schema-1 captures: listed, not re-read
        }

        var raw = SnapshotJson.Deserialize(files[PackageFiles.ScrubbedRaw]);
        Assert.Equal(files[PackageFiles.ScrubbedRaw], SnapshotJson.Serialize(raw));

        var once = FactsJson.Serialize(Normalizer.Normalize(raw));
        var again = FactsJson.Serialize(Normalizer.Normalize(SnapshotJson.Deserialize(files[PackageFiles.ScrubbedRaw])));
        Assert.Equal(once, again);
        Assert.Equal(
            CheckResultsJson.Serialize(CheckRunner.Run(Normalizer.Normalize(raw), FixtureGate.Validated)),
            CheckResultsJson.Serialize(CheckRunner.Run(Normalizer.Normalize(raw), FixtureGate.Validated)));
    }

    private static (AcceptedSession Session, IReadOnlyDictionary<string, byte[]> Files) Read(string cohortId, string sessionId)
    {
        var folder = Path.Combine(Cohort, cohortId, sessionId);
        var files = Directory.EnumerateFiles(folder).ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes, StringComparer.Ordinal);
        return (Corpus.Read(cohortId, sessionId, files), files);
    }
}
