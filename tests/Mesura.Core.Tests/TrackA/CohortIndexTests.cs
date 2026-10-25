using System.Text;
using Mesura.Core.Checks;
using Mesura.Core.Privacy;
using Mesura.Core.Raw;
using Mesura.Core.Tests.Normalize;
using Mesura.Core.TrackA;

namespace Mesura.Core.Tests.TrackA;

/// <summary>The cohort index and coverage report (spec §27, §28, §32): generated, descriptive, closed-vocabulary.</summary>
public sealed class CohortIndexTests
{
    public static AcceptedSession Accepted(CapturedPackage package, IReadOnlyDictionary<string, byte[]>? evidence = null, AcceptanceDecisions? decisions = null) =>
        new(package.Manifest, TrackAJson.Deserialize<OperatorObservations>(package.Files[PackageFiles.Observations], 1), AcceptanceGateTests.Accept(package, evidence, decisions));

    public static CohortIndex Index(params CapturedPackage[] packages) => CohortIndex.Build(packages.Select(package => Accepted(package)));

    [Fact]
    public void An_empty_cohort_gives_an_empty_inventory()
    {
        var index = CohortIndex.Build([]);

        Assert.Empty(index.Machines);
        Assert.Equal(0, index.Summary.Single(tally => tally.Dimension == "machines").Machines);
        Assert.Contains("Machines: 0", CoverageReport.Render(index), StringComparison.Ordinal);
    }

    [Fact]
    public void The_inventory_answers_the_owner_s_questions_from_the_accepted_sessions()
    {
        var index = Index(TrackAFixture.Package(TrackAFixture.Baseline()));
        var machine = Assert.Single(index.Machines);

        Assert.Equal(TrackAFixture.Cohort, machine.CohortId);
        Assert.Equal([GpuVendor.Nvidia], machine.Inventory.DiscreteGpuVendors);
        Assert.Equal([GpuVendor.Intel], machine.Inventory.IntegratedGpuVendors);
        Assert.Equal([WindowsFamily.Windows10], machine.Inventory.Windows);
        Assert.Equal(["desktop"], machine.Inventory.ChassisObserved);
        Assert.Equal([SteamState.Registered], machine.Inventory.Steam);
        Assert.Equal([MediaKind.Hdd, MediaKind.Ssd], machine.Inventory.DiskMedia);
        Assert.Equal(165, machine.Inventory.HighestOfferedHz);
        Assert.Equal(CoverageState.Completed, machine.Scenarios.Single(item => item.Scenario == ScenarioCatalog.Baseline).State);
        Assert.Equal(CoverageState.Missing, machine.Scenarios.Single(item => item.Scenario == ScenarioCatalog.Clone).State);
        Assert.Contains(machine.CheckPaths, path => path.Check == CheckIds.RefreshBelowMax);
        Assert.Contains(machine.CheckPaths, path => path.Check == CheckIds.OnIntegratedGpu);
    }

    [Fact]
    public void One_machine_can_meet_several_targets_and_each_names_its_basis()
    {
        // The synthetic PC: NVIDIA card, operator says desktop, Steam, an HDD and an NVMe SSD, Windows 10, 165 Hz offered.
        var machine = Assert.Single(Index(TrackAFixture.Package(TrackAFixture.Baseline())).Machines);
        bool Met(string target) => machine.Targets.Single(result => result.Target == target).Met;

        Assert.True(Met("nvidiaDesktop"));
        Assert.True(Met("steamMultipleStorageKinds"));
        Assert.True(Met("hddAndSsd"));
        Assert.True(Met("windows10"));
        Assert.False(Met("amdDesktop"));
        Assert.False(Met("windows11Above60Hz"));
        Assert.False(Met("cloneMode"));
        Assert.Contains("operator:desktop", machine.Targets.Single(result => result.Target == "nvidiaDesktop").Basis);
        Assert.Contains("missing:scenario:cloneCompleted", machine.Targets.Single(result => result.Target == "cloneMode").Basis);
        Assert.Contains("machine:notObservable", machine.Targets.Single(result => result.Target == "dynamicRefresh").Basis);
    }

    [Fact]
    public void Operator_declarations_alone_never_meet_a_target_that_needs_the_machine()
    {
        // Clone declared and completed, but the machine shows no shared source: not met.
        var clone = TrackAFixture.Package(TrackAFixture.Capture(ScenarioCatalog.Clone, null, answers:
        [
            TrackAFixture.Consent(),
            TrackAFixture.Answer(ObservationCatalog.CloneModeIntended, "yes"),
            TrackAFixture.Answer(ObservationCatalog.PhysicalMonitorsConnected, "2"),
        ]));

        var machine = Assert.Single(Index(clone).Machines);

        Assert.Equal(CoverageState.Completed, machine.Scenarios.Single(item => item.Scenario == ScenarioCatalog.Clone).State);
        var target = machine.Targets.Single(result => result.Target == "cloneMode");
        Assert.False(target.Met);
        Assert.Contains("missing:machine:sharedSource", target.Basis);
    }

    [Fact]
    public void The_pcie_spike_counts_only_as_a_pair_of_one_series()
    {
        var idleSession = SessionId.At(TrackAFixture.Started);
        var loadSession = SessionId.At(TrackAFixture.Started.AddMinutes(10));
        var idleAnswers = TrackAFixture.Answers(ScenarioCatalog.PcieIdleLoad, ScenarioCatalog.IdleStep, (ObservationCatalog.IdleProcedureFollowed, "yes"));
        var loadAnswers = TrackAFixture.Answers(ScenarioCatalog.PcieIdleLoad, ScenarioCatalog.LoadStep,
            (ObservationCatalog.GameName, "Test Game"), (ObservationCatalog.StableGameplayReached, "yes"), (ObservationCatalog.GameStayedInForeground, "yes"));

        var idle = TrackAFixture.Package(TrackAFixture.Capture(ScenarioCatalog.PcieIdleLoad, ScenarioCatalog.IdleStep, session: idleSession, answers: idleAnswers));
        var load = TrackAFixture.Package(TrackAFixture.Capture(ScenarioCatalog.PcieIdleLoad, ScenarioCatalog.LoadStep, session: loadSession, answers: loadAnswers,
            series: new ManifestSeries(idleSession, idleSession, 600, true)));

        var alone = Assert.Single(Index(idle).Machines).Scenarios.Single(item => item.Scenario == ScenarioCatalog.PcieIdleLoad);
        var pair = Assert.Single(Index(idle, load).Machines).Scenarios.Single(item => item.Scenario == ScenarioCatalog.PcieIdleLoad);

        Assert.Equal(CoverageState.UnpairedStep, alone.State);
        Assert.Equal(CoverageState.NeedsManualEvidence, pair.State); // no GPU-Z, HWiNFO or probe files: the weaker step decides
        Assert.Equal(2, pair.Sessions);
    }

    [Fact]
    public void Storage_targets_count_internal_drives_only_never_the_kit_s_usb_stick()
    {
        // One NVMe SSD inside, plus the USB stick the kit runs from.
        var storage = new StorageData(
            [
                new PhysicalDisk("0", Reading.Known("PCI Slot 2 : Bus 1 : Device 0 : Function 0 : Adapter 0"), Reading.Known("TEST NVME"), Reading.Known(MediaKind.Ssd, 4), Reading.Known(BusKind.Nvme, 17), Reading.Known(HealthState.Healthy, 0), Reading.Known<IReadOnlyList<int>>([2]), Reading.Known(1_000_000_000_000UL)),
                new PhysicalDisk("1", Reading.Known("USB Port 3"), Reading.Known("TEST STICK"), Reading.Known(MediaKind.Unspecified, 0), Reading.Known(BusKind.Usb, 7), Reading.Known(HealthState.Healthy, 0), Reading.Known<IReadOnlyList<int>>([2]), Reading.Known(64_000_000_000UL)),
            ],
            [], [], []);
        var package = TrackAFixture.Package(TrackAFixture.Baseline(TrackAFixture.Prepare(TrackAFixture.Raw(change: sources => sources with { Storage = RawBuilder.Ok(storage) }))));

        var machine = Assert.Single(Index(package).Machines);

        Assert.False(machine.Targets.Single(result => result.Target == "steamMultipleStorageKinds").Met);
        Assert.False(machine.Targets.Single(result => result.Target == "hddAndSsd").Met);
        Assert.Contains(BusKind.Usb, machine.Inventory.DiskBuses); // still in the inventory, as observed
    }

    [Fact]
    public void Missing_adapter_data_never_reads_as_no_discrete_gpu()
    {
        var package = TrackAFixture.Package(TrackAFixture.Baseline(TrackAFixture.Prepare(TrackAFixture.Raw(change: sources => sources with
        {
            Adapters = RawBuilder.Unavailable<AdapterData>(MissingReason.SourceFailed),
        }))));

        var index = Index(package);

        Assert.Equal(TraitState.Unknown, Assert.Single(index.Machines).Inventory.DiscreteGpu);
        Assert.Equal(0, index.Summary.Single(tally => tally.Dimension == "discreteGpu" && tally.Value == "none").Machines);
        Assert.Equal(1, index.Summary.Single(tally => tally.Dimension == "discreteGpu" && tally.Value == "unknown").Machines);
    }

    [Fact]
    public void A_step_whose_evidence_was_all_rejected_at_review_needs_manual_evidence_in_the_index()
    {
        EvidenceFile[] files =
        [
            new("gpu-z-idle.png", [1]), new("hwinfo-idle.png", [2]), new("pcie-probe-idle-20260929T120000Z.json", Encoding.UTF8.GetBytes("{}")),
        ];
        var package = TrackAFixture.Package(TrackAFixture.Capture(ScenarioCatalog.PcieIdleLoad, ScenarioCatalog.IdleStep, evidence: files,
            answers: TrackAFixture.Answers(ScenarioCatalog.PcieIdleLoad, ScenarioCatalog.IdleStep, (ObservationCatalog.IdleProcedureFollowed, "yes"))));
        Assert.Equal(ScenarioStatus.Completed, package.Manifest.Scenario.Status);
        var present = files.ToDictionary(file => file.Name, file => file.Contents);
        AcceptedSession With(EvidenceDecision screenshots) => Accepted(package, present, AcceptanceGateTests.Reviewed(new Dictionary<string, EvidenceDecision>
        {
            ["gpu-z-idle.png"] = screenshots, ["hwinfo-idle.png"] = EvidenceDecision.Accepted, ["pcie-probe-idle-20260929T120000Z.json"] = EvidenceDecision.Accepted,
        }));

        Assert.Equal(ScenarioStatus.Completed, With(EvidenceDecision.Accepted).Status);
        Assert.Equal(ScenarioStatus.NeedsManualEvidence, With(EvidenceDecision.Rejected).Status);
        Assert.Equal(ScenarioStatus.NeedsManualEvidence, Assert.Single(CohortIndex.Build([With(EvidenceDecision.Rejected)]).Machines).Sessions.Single().Status);
    }

    [Fact]
    public void Sessions_of_one_cohort_id_that_show_different_machine_models_are_flagged()
    {
        var one = TrackAFixture.Package(TrackAFixture.Baseline(session: SessionId.At(TrackAFixture.Started)));
        var other = TrackAFixture.Package(TrackAFixture.Baseline(
            TrackAFixture.Prepare(TrackAFixture.Raw(change: sources => sources with { Adapters = RawBuilder.Unavailable<AdapterData>(MissingReason.SourceFailed) })),
            session: SessionId.At(TrackAFixture.Started.AddHours(1))));

        Assert.Equal(["modelSignatureDiffers"], Assert.Single(Index(one, other).Machines).Warnings);
        Assert.Empty(Assert.Single(Index(one).Machines).Warnings);
    }

    [Fact]
    public void The_index_is_deterministic_holds_only_tokens_and_passes_the_audit()
    {
        var packages = new[]
        {
            TrackAFixture.Package(TrackAFixture.Baseline(session: SessionId.At(TrackAFixture.Started))),
            TrackAFixture.Package(TrackAFixture.Capture(ScenarioCatalog.SteamStorage, null, session: SessionId.At(TrackAFixture.Started.AddHours(2)), answers:
            [
                TrackAFixture.Consent(),
                TrackAFixture.Answer(ObservationCatalog.GameLibraryDrive, "D"),
                TrackAFixture.Answer(ObservationCatalog.ExpectedGameDiskKind, "hdd"),
                TrackAFixture.Answer(ObservationCatalog.TestGameInstalledThere, "yes"),
                TrackAFixture.Answer(ObservationCatalog.InstalledDriveKinds, "hdd,nvmeSsd"),
                TrackAFixture.Answer(ObservationCatalog.OperatorNote, "a free-text note that must not reach the index"),
            ])),
        };

        var json = TrackAJson.Serialize(Index(packages));

        Assert.Equal(json, TrackAJson.Serialize(CohortIndex.Build(packages.Reverse().Select(package => Accepted(package)))));
        Assert.Empty(ShareableAudit.AuditJson(Corpus.IndexFile, json));
        Assert.All(JsonStrings.Of(json), text => Assert.Matches("^[A-Za-z0-9][A-Za-z0-9.:/_-]*$", text.Value));
        Assert.DoesNotContain("free-text", Encoding.UTF8.GetString(json), StringComparison.Ordinal);
        Assert.DoesNotContain("TEST NVME", Encoding.UTF8.GetString(json), StringComparison.Ordinal);
    }

    [Fact]
    public void The_coverage_report_is_an_inventory_never_a_score()
    {
        var report = CoverageReport.Render(Index(TrackAFixture.Package(TrackAFixture.Baseline())));

        Assert.StartsWith("TRACK A COVERAGE", report, StringComparison.Ordinal);
        Assert.Contains("Machines: 1 (PC-042)", report, StringComparison.Ordinal);
        Assert.Contains("nvidiaDesktop              PC-042", report, StringComparison.Ordinal);
        Assert.Contains("Checks 3-8 are not implemented", report, StringComparison.Ordinal);
        Assert.DoesNotContain("score", report.Replace("not a score", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("%", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_session_s_readme_is_generated_from_its_files()
    {
        var accepted = Accepted(TrackAFixture.Package(TrackAFixture.Baseline()));

        var readme = SessionReadme.Render(accepted);

        Assert.Equal(readme, SessionReadme.Render(accepted));
        Assert.Contains($"# {TrackAFixture.Cohort} - {TrackAFixture.Session}", readme, StringComparison.Ordinal);
        Assert.Contains("Not telemetry", readme, StringComparison.Ordinal);
        Assert.Empty(ShareableAudit.AuditText(Corpus.ReadmeFile, readme));
        Assert.DoesNotContain("\r", readme, StringComparison.Ordinal);
    }
}
