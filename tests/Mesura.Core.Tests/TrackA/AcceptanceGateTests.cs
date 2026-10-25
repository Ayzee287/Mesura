using System.Text;
using System.Text.Json.Nodes;
using Mesura.Core.TrackA;

namespace Mesura.Core.Tests.TrackA;

/// <summary>The acceptance gate (spec §26): nothing becomes an accepted cohort fixture without every criterion and a person's review.</summary>
public sealed class AcceptanceGateTests
{
    private static readonly PromotingBuild SameBuild = new("0.1.0", TrackAFixture.Commit);
    private static readonly DateOnly Today = new(2026, 9, 29);

    public static AcceptanceDecisions Reviewed(IReadOnlyDictionary<string, EvidenceDecision>? evidence = null, bool consentSince = false, params string[] acknowledged) =>
        new(true, acknowledged, consentSince, evidence ?? new Dictionary<string, EvidenceDecision>());

    public static AcceptanceRecord Accept(CapturedPackage package, IReadOnlyDictionary<string, byte[]>? evidence = null, AcceptanceDecisions? decisions = null)
    {
        var evaluation = AcceptanceGate.Evaluate(TrackAFixture.Incoming(package, evidence), SameBuild, (_, _) => false);
        var (record, refusals) = AcceptanceGate.Accept(evaluation, decisions ?? Reviewed(), SameBuild, Today);
        return record ?? throw new InvalidOperationException(string.Join("; ", refusals));
    }

    [Fact]
    public void A_ready_package_with_a_person_s_review_is_accepted_and_bound_to_its_manifest()
    {
        var package = TrackAFixture.Package(TrackAFixture.Baseline());

        var evaluation = AcceptanceGate.Evaluate(TrackAFixture.Incoming(package), SameBuild, (_, _) => false);
        Assert.True(evaluation.Passed, string.Join("; ", evaluation.Checks.Where(check => !check.Passed).Select(check => check.Criterion + ": " + check.Detail)));

        var record = Accept(package);
        Assert.Equal(CapturePipeline.Sha256(package.Files[PackageFiles.Manifest]), record.ManifestSha256);
        Assert.Equal(new ReplayRecord(true, Comparison.Identical, Comparison.Identical), record.Replay);
        Assert.Equal("2026-09-29", record.AcceptedOn);
        Assert.Equal(ConsentStatus.GivenAtCapture, record.Consent.GameNames);
        Assert.Contains("privacyAudit", record.Criteria);
        Assert.Equal(TrackAJson.Serialize(record), TrackAJson.Serialize(TrackAJson.Deserialize<AcceptanceRecord>(TrackAJson.Serialize(record), AcceptanceRecord.CurrentSchemaVersion)));
    }

    [Fact]
    public void Without_the_manual_review_nothing_is_accepted()
    {
        var evaluation = AcceptanceGate.Evaluate(TrackAFixture.Incoming(TrackAFixture.Package(TrackAFixture.Baseline())), SameBuild, (_, _) => false);

        var (record, refusals) = AcceptanceGate.Accept(evaluation, new AcceptanceDecisions(false, [], false, new Dictionary<string, EvidenceDecision>()), SameBuild, Today);

        Assert.Null(record);
        Assert.Contains(refusals, refusal => refusal.StartsWith("manualReview", StringComparison.Ordinal));
    }

    [Fact]
    public void A_changed_file_is_detected()
    {
        var package = TrackAFixture.Package(TrackAFixture.Baseline());
        var files = new Dictionary<string, byte[]>(package.Files) { [PackageFiles.Facts] = [.. package.Files[PackageFiles.Facts], (byte)' '] };

        Assert.Contains("hashesMatch", Failed(new IncomingSession(files, new Dictionary<string, byte[]>())));
    }

    [Fact]
    public void A_missing_or_unlisted_file_is_detected()
    {
        var package = TrackAFixture.Package(TrackAFixture.Baseline());
        var missing = package.Files.Where(pair => pair.Key != PackageFiles.Results).ToDictionary();
        var extra = new Dictionary<string, byte[]>(package.Files) { ["raw.json"] = [1] };

        Assert.Contains("filesComplete", Failed(new IncomingSession(missing, new Dictionary<string, byte[]>())));
        Assert.Contains("filesComplete", Failed(new IncomingSession(extra, new Dictionary<string, byte[]>())));
    }

    [Fact]
    public void A_package_without_a_manifest_or_with_a_broken_one_is_refused()
    {
        var package = TrackAFixture.Package(TrackAFixture.Baseline());
        var noManifest = package.Files.Where(pair => pair.Key != PackageFiles.Manifest).ToDictionary();
        var broken = new Dictionary<string, byte[]>(package.Files) { [PackageFiles.Manifest] = Encoding.UTF8.GetBytes("{\"schemaVersion\":1}") };

        Assert.Contains("manifestReadable", Failed(new IncomingSession(noManifest, new Dictionary<string, byte[]>())));
        Assert.Contains("manifestReadable", Failed(new IncomingSession(broken, new Dictionary<string, byte[]>())));
    }

    [Fact]
    public void A_build_without_a_commit_can_neither_produce_nor_accept()
    {
        var raw = TrackAFixture.WithApp(TrackAFixture.Raw(), new Raw.AppInfo("Mesura", "0.1.0", Raw.Reading.Missing<string>(Raw.MissingReason.NotPresent, "uncommitted")));
        var uncommitted = TrackAFixture.Package(TrackAFixture.Baseline(TrackAFixture.Prepare(raw)));
        var ready = TrackAFixture.Package(TrackAFixture.Baseline());

        Assert.Contains("producerCommitted", Failed(TrackAFixture.Incoming(uncommitted)));
        Assert.Contains("packageReady", Failed(TrackAFixture.Incoming(uncommitted)));
        Assert.Contains("promotingBuildCommitted", Failed(TrackAFixture.Incoming(ready), new PromotingBuild("0.1.0", null)));
    }

    [Fact]
    public void Replay_must_match_on_the_producing_commit_and_is_recorded_on_another()
    {
        // A tampered fact file whose manifest was rewritten to match: integrity passes, replay doesn't.
        var package = TrackAFixture.Package(TrackAFixture.Baseline());
        var facts = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(package.Files[PackageFiles.Facts]).Replace("\"authoritative\"", "\"declared\"", StringComparison.Ordinal));
        var manifest = JsonNode.Parse(package.Files[PackageFiles.Manifest])!;
        var entry = manifest["files"]!.AsArray().Single(file => file!["path"]!.GetValue<string>() == PackageFiles.Facts)!;
        entry["sha256"] = CapturePipeline.Sha256(facts);
        entry["bytes"] = facts.LongLength;
        var files = new Dictionary<string, byte[]>(package.Files)
        {
            [PackageFiles.Facts] = facts,
            [PackageFiles.Manifest] = TrackAJson.Serialize(TrackAJson.Deserialize<CaptureManifest>(Encoding.UTF8.GetBytes(manifest.ToJsonString()), 1)),
        };
        var incoming = new IncomingSession(files, new Dictionary<string, byte[]>());

        Assert.Contains("replay", Failed(incoming));

        var other = AcceptanceGate.Evaluate(incoming, new PromotingBuild("0.2.0", "fedcba9876543210fedcba9876543210fedcba98"), (_, _) => false);
        Assert.DoesNotContain(other.Checks, check => check.Criterion == "replay" && !check.Passed);
        Assert.Equal(new ReplayRecord(false, Comparison.Differs, Comparison.Identical), other.Replay);
    }

    [Fact]
    public void An_already_accepted_session_is_not_accepted_twice()
    {
        var package = TrackAFixture.Package(TrackAFixture.Baseline());

        Assert.Contains("notAlreadyAccepted", Failed(TrackAFixture.Incoming(package), SameBuild, (cohort, session) => cohort == TrackAFixture.Cohort && session == package.Manifest.SessionId));
    }

    [Fact]
    public void Observations_from_another_session_are_refused()
    {
        var package = TrackAFixture.Package(TrackAFixture.Baseline());
        var elsewhere = TrackAFixture.Package(TrackAFixture.Baseline(session: SessionId.At(TrackAFixture.Started.AddHours(1))));
        var files = new Dictionary<string, byte[]>(package.Files) { [PackageFiles.Observations] = elsewhere.Files[PackageFiles.Observations] };

        var failed = Failed(new IncomingSession(files, new Dictionary<string, byte[]>()));

        Assert.Contains("hashesMatch", failed);
        Assert.Contains("observationsReadable", failed);
    }

    [Fact]
    public void A_not_ready_package_is_refused_with_its_reasons()
    {
        var no = TrackAFixture.Package(TrackAFixture.Baseline(answers: [TrackAFixture.Consent("no"), TrackAFixture.Answer(ObservationCatalog.PhysicalChassisType, "desktop")]));

        var evaluation = AcceptanceGate.Evaluate(TrackAFixture.Incoming(no), SameBuild, (_, _) => false);

        var check = evaluation.Checks.Single(item => item.Criterion == "packageReady");
        Assert.False(check.Passed);
        Assert.Contains(PackageReasons.ConsentNotGiven, check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Consent_recorded_as_unknown_can_be_given_at_acceptance_and_is_recorded_so()
    {
        var unknown = TrackAFixture.Package(TrackAFixture.Baseline(answers: [TrackAFixture.Consent(""), TrackAFixture.Answer(ObservationCatalog.PhysicalChassisType, "desktop")]));
        var evaluation = AcceptanceGate.Evaluate(TrackAFixture.Incoming(unknown), SameBuild, (_, _) => false);

        Assert.True(evaluation.ConsentOpen);
        var (refused, refusals) = AcceptanceGate.Accept(evaluation, Reviewed(), SameBuild, Today);
        Assert.Null(refused);
        Assert.Contains(refusals, refusal => refusal.StartsWith("consent", StringComparison.Ordinal));

        Assert.Equal(ConsentStatus.GivenAtAcceptance, Accept(unknown, decisions: Reviewed(consentSince: true)).Consent.GameNames);
    }

    [Fact]
    public void Evidence_must_be_present_unchanged_and_decided_by_a_person()
    {
        var screenshot = new EvidenceFile("gpu-z-idle.png", [1, 2, 3]);
        var package = TrackAFixture.Package(TrackAFixture.Baseline(evidence: [screenshot]));
        var present = new Dictionary<string, byte[]> { [screenshot.Name] = screenshot.Contents };

        Assert.Contains("evidencePresent", Failed(TrackAFixture.Incoming(package)));
        Assert.Contains("evidencePresent", Failed(TrackAFixture.Incoming(package, new Dictionary<string, byte[]> { [screenshot.Name] = [9] })));

        var evaluation = AcceptanceGate.Evaluate(TrackAFixture.Incoming(package, present), SameBuild, (_, _) => false);
        Assert.Contains(AcceptanceGate.Accept(evaluation, Reviewed(), SameBuild, Today).Refusals, refusal => refusal.StartsWith("evidenceReview", StringComparison.Ordinal));

        var record = Accept(package, present, Reviewed(new Dictionary<string, EvidenceDecision> { [screenshot.Name] = EvidenceDecision.Rejected }));
        Assert.Equal(EvidenceDecision.Rejected, Assert.Single(record.Evidence).Decision);
    }

    [Fact]
    public void Identifier_kinds_that_went_unchecked_must_each_be_acknowledged()
    {
        var package = TrackAFixture.Package(TrackAFixture.Baseline(unread: ["network MAC address"]));
        var evaluation = AcceptanceGate.Evaluate(TrackAFixture.Incoming(package), SameBuild, (_, _) => false);

        Assert.True(evaluation.Passed);
        Assert.Contains(AcceptanceGate.Accept(evaluation, Reviewed(), SameBuild, Today).Refusals, refusal => refusal.StartsWith("uncheckedKinds", StringComparison.Ordinal));
        Assert.Equal(["network MAC address"], Accept(package, decisions: Reviewed(null, false, "network MAC address")).Review.UncheckedIdentifierKindsAcknowledged);
    }

    [Fact]
    public void A_scenario_record_that_disagrees_with_the_files_is_refused()
    {
        // A manifest claiming a procedure was confirmed although the answers don't say so.
        var package = TrackAFixture.Package(TrackAFixture.Capture(ScenarioCatalog.GameLoad, null, answers:
        [
            TrackAFixture.Consent(),
            TrackAFixture.Answer(ObservationCatalog.GameName, "Test Game"),
            TrackAFixture.Answer(ObservationCatalog.GameDisplayMode, "windowed"),
            TrackAFixture.Answer(ObservationCatalog.StableGameplayReached, "yes"),
            TrackAFixture.Answer(ObservationCatalog.GameStayedInForeground, "no"),
        ]));
        var manifest = JsonNode.Parse(package.Files[PackageFiles.Manifest])!;
        manifest["scenario"]!["unconfirmedProcedures"] = new JsonArray();
        manifest["scenario"]!["status"] = "completed";
        var files = new Dictionary<string, byte[]>(package.Files) { [PackageFiles.Manifest] = Encoding.UTF8.GetBytes(manifest.ToJsonString()) };

        Assert.Contains("scenarioConsistent", Failed(new IncomingSession(files, new Dictionary<string, byte[]>())));
    }

    [Fact]
    public void A_manifest_that_hides_missing_evidence_is_refused()
    {
        var package = TrackAFixture.Package(TrackAFixture.Capture(ScenarioCatalog.PcieIdleLoad, ScenarioCatalog.IdleStep,
            answers: TrackAFixture.Answers(ScenarioCatalog.PcieIdleLoad, ScenarioCatalog.IdleStep, (ObservationCatalog.IdleProcedureFollowed, "yes"))));
        Assert.Equal(ScenarioStatus.NeedsManualEvidence, package.Manifest.Scenario.Status);

        var manifest = JsonNode.Parse(package.Files[PackageFiles.Manifest])!;
        manifest["scenario"]!["missingEvidenceTools"] = new JsonArray();
        manifest["scenario"]!["status"] = "completed";
        var files = new Dictionary<string, byte[]>(package.Files) { [PackageFiles.Manifest] = Encoding.UTF8.GetBytes(manifest.ToJsonString()) };

        Assert.Contains("scenarioConsistent", Failed(new IncomingSession(files, new Dictionary<string, byte[]>())));
    }

    [Fact]
    public void An_evidence_entry_whose_name_says_otherwise_is_refused()
    {
        var package = TrackAFixture.Package(TrackAFixture.Baseline(evidence: [new EvidenceFile("gpu-z-idle.png", [1])]));
        var manifest = JsonNode.Parse(package.Files[PackageFiles.Manifest])!;
        manifest["evidence"]!["files"]![0]!["tool"] = "hwinfo";

        Assert.Throws<TrackAFormatException>(() => TrackAJson.Deserialize<CaptureManifest>(Encoding.UTF8.GetBytes(manifest.ToJsonString()), 1));
    }

    [Fact]
    public void A_package_missing_its_facts_results_or_scan_is_refused_whatever_the_manifest_lists()
    {
        var package = TrackAFixture.Package(TrackAFixture.Baseline());
        var other = new PromotingBuild("0.2.0", "fedcba9876543210fedcba9876543210fedcba98");

        IncomingSession Without(params FileRole[] roles)
        {
            var manifest = JsonNode.Parse(package.Files[PackageFiles.Manifest])!;
            var list = manifest["files"]!.AsArray();
            foreach (var entry in list.Where(entry => roles.Select(role => CohortIndex.Name(role)).Contains(entry!["role"]!.GetValue<string>())).ToList())
            {
                list.Remove(entry);
            }

            var files = package.Files.Where(pair => !roles.Select(PackageFiles.NameOf).Contains(pair.Key)).ToDictionary();
            files[PackageFiles.Manifest] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
            return new IncomingSession(files, new Dictionary<string, byte[]>());
        }

        var noOutputs = Failed(Without(FileRole.NormalizedFacts, FileRole.CheckResults), other);
        Assert.Contains("packageComplete", noOutputs);
        Assert.Contains("replay", noOutputs);
        Assert.Contains("rawReadable", Failed(Without(FileRole.ScrubbedRawSnapshot)));
    }

    private static List<string> Failed(IncomingSession session, PromotingBuild? build = null, Func<string, string, bool>? accepted = null) =>
        [.. AcceptanceGate.Evaluate(session, build ?? SameBuild, accepted ?? ((_, _) => false)).Checks.Where(check => !check.Passed).Select(check => check.Criterion)];
}
