using System.Text;
using Mesura.Core.Checks;
using Mesura.Core.Checks.Serialization;
using Mesura.Core.Facts.Serialization;
using Mesura.Core.Normalize;
using Mesura.Core.Privacy;
using Mesura.Core.Raw;
using Mesura.Core.Raw.Serialization;
using Mesura.Core.Tests.Normalize;
using Mesura.Core.TrackA;

namespace Mesura.Core.Tests.TrackA;

/// <summary>The capture pipeline on synthetic data: the package, its privacy verification, determinism and partial captures.</summary>
public sealed class CapturePipelineTests
{
    [Fact]
    public void A_baseline_capture_is_a_ready_package_of_five_files_with_the_manifest_hashing_the_others()
    {
        var package = TrackAFixture.Package(TrackAFixture.Baseline());
        var manifest = package.Manifest;

        Assert.Equal([PackageFiles.Manifest, PackageFiles.Results, PackageFiles.Facts, PackageFiles.Observations, PackageFiles.ScrubbedRaw], package.Files.Keys.Order(StringComparer.Ordinal));
        Assert.All(manifest.Files, file =>
        {
            Assert.Equal(CapturePipeline.Sha256(package.Files[file.Path]), file.Sha256);
            Assert.Equal(package.Files[file.Path].LongLength, file.Bytes);
        });
        Assert.Equal(PackageStatus.Ready, manifest.Package.Status);
        Assert.Equal(FixtureStatus.Incoming, manifest.Package.Fixture);
        Assert.Equal(ScenarioStatus.Completed, manifest.Scenario.Status);
        Assert.Equal(PrivacyVerdict.Passed, manifest.Privacy.Verdict);
        Assert.Equal(TrackAFixture.Commit, manifest.Producer.Commit);
        Assert.Equal(Stages.All, manifest.Stages.Select(stage => stage.Stage));
        Assert.True(manifest.Determinism.Passed);
        Assert.Equal(AnnotationStatus.Complete, manifest.Annotations.Status);
    }

    [Fact]
    public void The_package_holds_the_existing_pipeline_s_outputs_unchanged()
    {
        var raw = TrackAFixture.Raw();
        var package = TrackAFixture.Package(TrackAFixture.Baseline(TrackAFixture.Prepare(raw)));
        var scrubbed = FixturePseudonymizer.Pseudonymize(raw).Snapshot;
        var facts = Normalizer.Normalize(scrubbed);

        Assert.Equal(SnapshotJson.Serialize(scrubbed), package.Files[PackageFiles.ScrubbedRaw]);
        Assert.Equal(FactsJson.Serialize(facts), package.Files[PackageFiles.Facts]);
        Assert.Equal(CheckResultsJson.Serialize(CheckRunner.Run(facts, FixtureGate.Validated)), package.Files[PackageFiles.Results]);
    }

    [Fact]
    public void Only_implemented_checks_are_listed_as_run_and_the_statement_says_so()
    {
        var checks = TrackAFixture.Package(TrackAFixture.Baseline()).Manifest.Checks;

        Assert.Equal([CheckIds.RefreshBelowMax, CheckIds.OnIntegratedGpu], checks.Ran);
        Assert.Equal(6, checks.NotImplemented.Count);
        Assert.Contains(CheckIds.PcieLinkReduced, checks.NotImplemented);
        Assert.Contains(CheckIds.GameDrives, checks.NotImplemented);
        Assert.Contains("no verdict here is a pass of all checks", checks.Statement, StringComparison.Ordinal);
    }

    [Fact]
    public void Sensitive_raw_values_never_reach_any_shareable_file()
    {
        var raw = TrackAFixture.Raw();
        var package = TrackAFixture.Package(TrackAFixture.Baseline(TrackAFixture.Prepare(raw)));
        var all = string.Join('\n', package.Files.Values.Select(Encoding.UTF8.GetString));

        Assert.DoesNotContain("00000000000A000", all, StringComparison.Ordinal); // adapter LUIDs
        Assert.DoesNotContain("4\\u0026test\\u00260\\u00260008", all, StringComparison.Ordinal); // PnP instance part
        Assert.DoesNotContain("00000000-0000-0000-0000-00000000000", all, StringComparison.Ordinal); // volume GUIDs
        Assert.DoesNotContain("1\\u0026test\\u00260\\u0026UID1", all, StringComparison.Ordinal); // monitor path instance
    }

    [Fact]
    public void An_identifier_planted_in_the_scan_fails_the_package_and_the_report_names_file_category_and_pointer_only()
    {
        var planted = TrackAFixture.WithSteamText("\"libraryfolders\"\n{\n\t\"note\"\t\t\"" + TrackAFixture.Serial.Value + "\"\n}\n");

        var failure = Assert.IsType<PrivacyFailure>(TrackAFixture.Baseline(TrackAFixture.Prepare(planted)));

        var findings = failure.Report.Findings.Where(finding => finding.Category == "this PC's system serial number").ToList();
        Assert.Contains(findings, finding => finding.File == PackageFiles.ScrubbedRaw && finding.Pointer == "/sources/steam/data/libraryFolders/value/text");
        Assert.DoesNotContain(TrackAFixture.Serial.Value, Encoding.UTF8.GetString(failure.Json), StringComparison.Ordinal);
        Assert.All(failure.Report.Findings, finding => Assert.False(string.IsNullOrWhiteSpace(finding.Remediation)));
    }

    [Fact]
    public void The_verification_covers_the_observations_the_manifest_and_evidence_names_too()
    {
        // An answer carrying the serial (the kit screens answers first; this is the package-level net).
        var note = new Observation(ObservationCatalog.OperatorNote.Id, ObservationRole.ContextualNote, ObservationState.Known, null, new ObservedText("serial " + TrackAFixture.Serial.Value));
        var viaNote = Assert.IsType<PrivacyFailure>(TrackAFixture.Baseline(answers: [TrackAFixture.Consent(), note]));
        Assert.Contains(viaNote.Report.Findings, finding => finding.File == PackageFiles.Observations && finding.Pointer == "/observations/1/value");

        // A failed stage's detail is recorded in the manifest, so the manifest is checked as well.
        var prepared = CapturePipeline.Prepare(TrackAFixture.Raw(), null, text => text,
            _ => throw new InvalidOperationException("device " + TrackAFixture.Serial.Value), facts => CheckRunner.Run(facts, FixtureGate.Validated));
        var viaDetail = Assert.IsType<PrivacyFailure>(TrackAFixture.Baseline(prepared));
        Assert.Contains(viaDetail.Report.Findings, finding => finding.File == PackageFiles.Manifest && finding.Pointer == "/stages/4/detail");

        // An evidence name can't carry free text at all: a name with one is not evidence, and it isn't recorded.
        var named = TrackAFixture.Package(TrackAFixture.Baseline(evidence: [new EvidenceFile($"hwinfo-idle-{TrackAFixture.Serial.Value}.png", [1, 2, 3])]));
        Assert.Empty(named.Manifest.Evidence.Files);
        Assert.Equal(1, named.Manifest.Evidence.Ignored);
    }

    [Fact]
    public void The_scan_s_own_opaque_ids_are_checked_wherever_they_turn_up()
    {
        var note = new Observation(ObservationCatalog.OperatorNote.Id, ObservationRole.ContextualNote, ObservationState.Known, null,
            new ObservedText(@"device path PCI\VEN_10DE&DEV_2484\4&test&0&0008"));

        var failure = Assert.IsType<PrivacyFailure>(TrackAFixture.Baseline(answers: [TrackAFixture.Consent(), note], identifiers: []));

        Assert.Contains(failure.Report.Findings, finding => finding.Category == "this PC's PnP instance id");
    }

    [Fact]
    public void Answers_are_screened_one_by_one_so_a_hit_can_be_asked_again()
    {
        var identifiers = new[] { TrackAFixture.Serial };

        Assert.Empty(CapturePipeline.Screen(TrackAFixture.Answer(ObservationCatalog.GameName, "Cyberpunk 2077"), identifiers));
        Assert.Contains("system serial number", CapturePipeline.Screen(TrackAFixture.Answer(ObservationCatalog.OperatorNote, "sn " + TrackAFixture.Serial.Value), identifiers));
        Assert.Contains(ShareableAudit.EmailAddress, CapturePipeline.Screen(TrackAFixture.Answer(ObservationCatalog.OperatorNote, "ask someone@example.org"), identifiers));
        Assert.Contains(ShareableAudit.UserFolder, CapturePipeline.Screen(TrackAFixture.Answer(ObservationCatalog.OperatorNote, @"C:\Users\Dave\Desktop"), identifiers));
    }

    [Fact]
    public void Evidence_that_doesn_t_follow_the_convention_is_counted_never_named()
    {
        var package = TrackAFixture.Package(TrackAFixture.Baseline(evidence:
        [
            new EvidenceFile("Screenshot of Daves PC.png", [1]),
            new EvidenceFile("gpu-z-idle.png", [2]),
        ]));

        Assert.Equal(1, package.Manifest.Evidence.Ignored);
        var file = Assert.Single(package.Manifest.Evidence.Files);
        Assert.Equal("gpu-z-idle.png", file.File);
        Assert.Equal(EvidenceReview.Pending, file.Review);
        Assert.Equal(EvidenceTextScan.NotText, file.TextScan);
        Assert.DoesNotContain("Daves", Encoding.UTF8.GetString(package.Files[PackageFiles.Manifest]), StringComparison.Ordinal);
    }

    [Fact]
    public void Text_evidence_is_scanned_for_the_pc_s_identifiers_but_never_interpreted()
    {
        var clean = new EvidenceFile("pcie-probe-idle-20260929T120000Z.json", Encoding.UTF8.GetBytes("""{"devices":[{"name":"GPU"}]}"""));
        var leaking = new EvidenceFile("pcie-probe-load-20260929T121000Z.json", Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("{\"serial\":\"" + TrackAFixture.Serial.Value + "\"}")).ToArray());

        var files = TrackAFixture.Package(TrackAFixture.Baseline(evidence: [clean, leaking])).Manifest.Evidence.Files;

        Assert.Equal(EvidenceTextScan.NoIdentifierFound, files.Single(file => file.File == clean.Name).TextScan);
        Assert.Equal(EvidenceTextScan.IdentifierFound, files.Single(file => file.File == leaking.Name).TextScan);
    }

    [Fact]
    public void Unreadable_identifier_kinds_are_reported_and_change_the_verdict()
    {
        var manifest = TrackAFixture.Package(TrackAFixture.Baseline(unread: ["network MAC address", "disk serial number"])).Manifest;

        Assert.Equal(PrivacyVerdict.PassedWithUncheckedKinds, manifest.Privacy.Verdict);
        Assert.Equal(["disk serial number", "network MAC address"], manifest.Privacy.UncheckedIdentifierKinds);
    }

    [Fact]
    public void Content_files_are_deterministic_and_the_session_time_reaches_only_the_manifest()
    {
        var early = TrackAFixture.Package(TrackAFixture.Baseline(session: SessionId.At(TrackAFixture.Started)));
        var late = TrackAFixture.Package(TrackAFixture.Baseline(TrackAFixture.Prepare(TrackAFixture.Raw(reversed: true)), session: SessionId.At(TrackAFixture.Started.AddDays(3))));

        foreach (var name in new[] { PackageFiles.ScrubbedRaw, PackageFiles.Facts, PackageFiles.Results })
        {
            Assert.Equal(early.Files[name], late.Files[name]);
        }

        Assert.NotEqual(early.Files[PackageFiles.Manifest], late.Files[PackageFiles.Manifest]);
        Assert.DoesNotContain(early.Manifest.SessionId, Encoding.UTF8.GetString(early.Files[PackageFiles.Facts]), StringComparison.Ordinal);
        Assert.Equal(Comparison.Identical, early.Manifest.Determinism.NormalizeAcrossCultures);
    }

    [Fact]
    public void A_normaliser_defect_is_a_failed_stage_and_the_scrubbed_scan_still_travels_to_reproduce_it()
    {
        var prepared = CapturePipeline.Prepare(TrackAFixture.Raw(), null, text => text, _ => throw new InvalidOperationException("defect"), facts => CheckRunner.Run(facts, FixtureGate.Validated));

        var package = TrackAFixture.Package(TrackAFixture.Baseline(prepared));

        Assert.Contains(package.Manifest.Stages, stage => stage.Stage == Stages.Normalize && stage.Status == StageStatus.Failed && stage.Detail == "InvalidOperationException: defect");
        Assert.Contains(package.Manifest.Stages, stage => stage.Stage == Stages.Checks && stage.Status == StageStatus.Skipped);
        Assert.True(package.Files.ContainsKey(PackageFiles.ScrubbedRaw));
        Assert.False(package.Files.ContainsKey(PackageFiles.Facts));
        Assert.Equal(PackageStatus.NotPromotable, package.Manifest.Package.Status);
        Assert.Contains(PackageReasons.StageFailed, package.Manifest.Package.Reasons);
    }

    [Fact]
    public void A_check_defect_is_a_failed_stage_not_a_determinism_failure()
    {
        var prepared = CapturePipeline.Prepare(TrackAFixture.Raw(), null, text => text, Normalizer.Normalize, _ => throw new InvalidOperationException("check defect"));

        var manifest = TrackAFixture.Package(TrackAFixture.Baseline(prepared)).Manifest;

        Assert.Contains(manifest.Stages, stage => stage.Stage == Stages.Checks && stage.Status == StageStatus.Failed);
        Assert.Contains(manifest.Stages, stage => stage.Stage == Stages.Determinism && stage.Status == StageStatus.Skipped);
        Assert.Equal([PackageReasons.StageFailed], manifest.Package.Reasons);
        Assert.Empty(manifest.Checks.Ran);
        Assert.Equal(6, manifest.Checks.NotImplemented.Count);
    }

    [Fact]
    public void A_nondeterministic_normaliser_fails_the_determinism_record()
    {
        var calls = 0;
        var prepared = CapturePipeline.Prepare(TrackAFixture.Raw(), null, text => text,
            raw => calls++ == 0 ? Normalizer.Normalize(raw) : Normalizer.Normalize(new RawSnapshot(raw.SchemaVersion, raw.CapturedAtUtc.AddSeconds(calls), raw.App, raw.Os, raw.Elevated, raw.Sources)),
            facts => CheckRunner.Run(facts, FixtureGate.Validated));

        var manifest = TrackAFixture.Package(TrackAFixture.Baseline(prepared)).Manifest;

        Assert.Equal(Comparison.Differs, manifest.Determinism.NormalizeRepeated);
        Assert.Contains(PackageReasons.DeterminismFailed, manifest.Package.Reasons);
    }

    [Fact]
    public void An_uncommitted_build_makes_a_package_that_can_t_be_accepted()
    {
        var raw = TrackAFixture.WithApp(TrackAFixture.Raw(), new AppInfo("Mesura", "0.1.0", Reading.Missing<string>(MissingReason.NotPresent, "built from uncommitted changes")));

        var manifest = TrackAFixture.Package(TrackAFixture.Baseline(TrackAFixture.Prepare(raw))).Manifest;

        Assert.Null(manifest.Producer.Commit);
        Assert.Equal("built from uncommitted changes", manifest.Producer.CommitMissing);
        Assert.Contains(PackageReasons.BuildNotCommitted, manifest.Package.Reasons);
    }

    [Theory]
    [InlineData(RawSources.Steam, ScenarioCatalog.SteamStorage)]
    [InlineData(RawSources.Pcie, ScenarioCatalog.PcieIdleLoad)]
    [InlineData(RawSources.Display, ScenarioCatalog.Clone)]
    [InlineData(RawSources.Display, ScenarioCatalog.DisplayHighRefresh)]
    public void A_missing_source_leaves_a_usable_package_and_makes_only_its_scenarios_unavailable(string source, string scenario)
    {
        var raw = TrackAFixture.Raw(change: sources => source switch
        {
            RawSources.Steam => sources with { Steam = RawBuilder.Unavailable<SteamData>(MissingReason.NotPresent) },
            RawSources.Pcie => sources with { Pcie = RawBuilder.Unavailable<PcieData>(MissingReason.AccessDenied) },
            _ => sources with { Display = RawBuilder.Unavailable<DisplayData>(MissingReason.SourceFailed) },
        });
        var prepared = TrackAFixture.Prepare(raw);
        var step = ScenarioCatalog.Require(scenario).Steps[0].Name;

        var baseline = TrackAFixture.Package(TrackAFixture.Baseline(prepared));
        var scoped = TrackAFixture.Package(TrackAFixture.Capture(scenario, step, prepared));

        Assert.Equal(PackageStatus.Ready, baseline.Manifest.Package.Status);
        Assert.Equal(ScenarioStatus.Completed, baseline.Manifest.Scenario.Status);
        Assert.Contains(baseline.Manifest.Sources, entry => entry.Source == source && entry.Status == SourceStatus.Unavailable);
        Assert.Equal(ScenarioStatus.Unavailable, scoped.Manifest.Scenario.Status);
        Assert.Equal([source], scoped.Manifest.Scenario.UnavailableRequiredSources);
    }

    [Fact]
    public void Not_applicable_and_skipped_are_recorded_as_declared_with_the_scan_beside_them()
    {
        var notApplicable = TrackAFixture.Package(TrackAFixture.Capture(ScenarioCatalog.Clone, null, disposition: ScenarioDisposition.NotApplicable));
        var skipped = TrackAFixture.Package(TrackAFixture.Capture(ScenarioCatalog.Clone, null, disposition: ScenarioDisposition.Skipped));

        Assert.Equal(ScenarioStatus.NotApplicable, notApplicable.Manifest.Scenario.Status);
        Assert.Equal(ScenarioDisposition.NotApplicable, notApplicable.Manifest.Scenario.OperatorDisposition);
        Assert.Equal(ScenarioStatus.Skipped, skipped.Manifest.Scenario.Status);
        Assert.True(notApplicable.Files.ContainsKey(PackageFiles.Facts));
        Assert.Equal(PackageStatus.Ready, notApplicable.Manifest.Package.Status);
    }

    [Fact]
    public void An_unconfirmed_procedure_makes_the_scenario_incomplete_never_completed()
    {
        Observation[] answers(string foreground) =>
        [
            TrackAFixture.Consent(),
            TrackAFixture.Answer(ObservationCatalog.GameName, "Test Game"),
            TrackAFixture.Answer(ObservationCatalog.GameDisplayMode, "borderless"),
            TrackAFixture.Answer(ObservationCatalog.StableGameplayReached, "yes"),
            TrackAFixture.Answer(ObservationCatalog.GameStayedInForeground, foreground),
        ];

        Assert.Equal(ScenarioStatus.Completed, TrackAFixture.Package(TrackAFixture.Capture(ScenarioCatalog.GameLoad, null, answers: answers("yes"))).Manifest.Scenario.Status);
        var unknown = TrackAFixture.Package(TrackAFixture.Capture(ScenarioCatalog.GameLoad, null, answers: answers(""))).Manifest.Scenario;
        Assert.Equal(ScenarioStatus.Incomplete, unknown.Status);
        Assert.Equal(["gameStayedInForeground"], unknown.UnconfirmedProcedures);
        Assert.Equal(ScenarioStatus.Incomplete, TrackAFixture.Package(TrackAFixture.Capture(ScenarioCatalog.GameLoad, null, answers: answers("no"))).Manifest.Scenario.Status);
    }

    [Fact]
    public void The_pcie_step_needs_its_external_evidence_but_never_reads_it()
    {
        var withoutEvidence = TrackAFixture.Package(TrackAFixture.Capture(ScenarioCatalog.PcieIdleLoad, ScenarioCatalog.IdleStep,
            answers: [TrackAFixture.Consent(), TrackAFixture.Answer(ObservationCatalog.IdleProcedureFollowed, "yes")]));
        Assert.Equal(ScenarioStatus.NeedsManualEvidence, withoutEvidence.Manifest.Scenario.Status);
        Assert.Equal([EvidenceTool.GpuZ, EvidenceTool.Hwinfo, EvidenceTool.PcieProbe], withoutEvidence.Manifest.Scenario.MissingEvidenceTools);

        var withEvidence = TrackAFixture.Package(TrackAFixture.Capture(ScenarioCatalog.PcieIdleLoad, ScenarioCatalog.IdleStep,
            answers: [TrackAFixture.Consent(), TrackAFixture.Answer(ObservationCatalog.IdleProcedureFollowed, "yes"), TrackAFixture.Answer(ObservationCatalog.GpuZBusInterface, "PCIe x16 4.0 @ x16 1.1")],
            evidence:
            [
                new EvidenceFile("gpu-z-idle.png", [1]),
                new EvidenceFile("hwinfo-idle.png", [2]),
                new EvidenceFile("pcie-probe-idle-20260929T120000Z.json", Encoding.UTF8.GetBytes("{}")),
                new EvidenceFile("gpu-z-load.png", [3]), // the other step's: recorded, doesn't count here
            ]));
        Assert.Equal(ScenarioStatus.Completed, withEvidence.Manifest.Scenario.Status);
        Assert.False(withEvidence.Manifest.Evidence.Files.Single(file => file.File == "gpu-z-load.png").BelongsToStep);
        Assert.Equal(withEvidence.Manifest.SessionId, withEvidence.Manifest.Scenario.Series!.Id);

        // GPU-Z's reading stays an external observation: no fact about the link changes.
        Assert.Equal(withoutEvidence.Files[PackageFiles.Facts], withEvidence.Files[PackageFiles.Facts]);
    }

    [Fact]
    public void Game_names_need_the_owner_s_consent_and_a_no_stops_acceptance()
    {
        var yes = TrackAFixture.Package(TrackAFixture.Baseline(answers: [TrackAFixture.Consent("yes"), TrackAFixture.Answer(ObservationCatalog.PhysicalChassisType, "desktop")]));
        var no = TrackAFixture.Package(TrackAFixture.Baseline(answers: [TrackAFixture.Consent("no"), TrackAFixture.Answer(ObservationCatalog.PhysicalChassisType, "desktop")]));
        var unknown = TrackAFixture.Package(TrackAFixture.Baseline(answers: [TrackAFixture.Consent(""), TrackAFixture.Answer(ObservationCatalog.PhysicalChassisType, "desktop")]));
        var missing = TrackAFixture.Package(TrackAFixture.Baseline(answers: [TrackAFixture.Answer(ObservationCatalog.PhysicalChassisType, "desktop")]));

        Assert.Equal(PackageStatus.Ready, yes.Manifest.Package.Status);
        Assert.Equal([PackageReasons.ConsentNotGiven], no.Manifest.Package.Reasons);
        Assert.Equal(PackageStatus.Ready, unknown.Manifest.Package.Status); // the owner may record it at acceptance
        Assert.Equal([PackageReasons.AnnotationsIncomplete], missing.Manifest.Package.Reasons);
        Assert.Equal(["gameNamesConsent"], missing.Manifest.Annotations.Unanswered);
    }

    [Fact]
    public void The_repeat_scan_separates_documented_run_dependent_values_from_other_differences()
    {
        var raw = TrackAFixture.Raw();
        var later = new RawSnapshot(raw.SchemaVersion, raw.CapturedAtUtc.AddSeconds(3), raw.App, raw.Os, raw.Elevated, raw.Sources with
        {
            Power = SourceEntry.Ok("test.power", 2, new PowerData(Reading.Known(true), Reading.Known(false))),
        });

        var same = TrackAFixture.Package(TrackAFixture.Baseline(TrackAFixture.Prepare(raw, raw))).Manifest.RepeatScan;
        var changed = TrackAFixture.Package(TrackAFixture.Baseline(TrackAFixture.Prepare(raw, later))).Manifest.RepeatScan;

        Assert.True(same.Performed);
        Assert.Equal(0, same.ExpectedDifferences + same.OtherDifferenceCount);
        Assert.Equal(Comparison.Identical, same.Results);
        Assert.Equal(2, changed.ExpectedDifferences); // capturedAtUtc, power durationMs
        Assert.Contains("/sources/power/status", changed.OtherDifferences);
        Assert.False(TrackAFixture.Package(TrackAFixture.Baseline()).Manifest.RepeatScan.Performed);
    }
}
