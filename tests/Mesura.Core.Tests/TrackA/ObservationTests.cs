using System.Text;
using Mesura.Core.TrackA;

namespace Mesura.Core.Tests.TrackA;

/// <summary>Operator observations (spec §15, §16): a closed, typed catalog; unknown and n/a are answers; notes are only notes.</summary>
public sealed class ObservationTests
{
    [Fact]
    public void Every_field_has_a_defined_role_kind_and_values_and_the_ids_are_unique()
    {
        Assert.Equal(ObservationCatalog.All.Count, ObservationCatalog.All.Select(field => field.Id).Distinct().Count());
        Assert.All(ObservationCatalog.All, field =>
        {
            Assert.Matches("^[a-z][A-Za-z0-9]*$", field.Id);
            Assert.False(string.IsNullOrWhiteSpace(field.Question));
            Assert.Equal(field.Kind is ObservationKind.Choice or ObservationKind.MultiChoice, field.Choices.Count > 0);
            Assert.All(field.Choices, choice => Assert.Matches("^[A-Za-z0-9][A-Za-z0-9]*$", choice));
            Assert.Equal(field.Role == ObservationRole.ExternalToolObservation, field.Tool is not null);
        });

        // Free text is only allowed in the note (spec §15).
        Assert.Equal([ObservationCatalog.OperatorNote], ObservationCatalog.All.Where(field => field.Kind == ObservationKind.Note));
        Assert.Equal(ObservationRole.ContextualNote, ObservationCatalog.OperatorNote.Role);
    }

    [Fact]
    public void The_four_roles_the_spec_names_and_consent_exist()
    {
        Assert.Equal(
            [ObservationRole.OperatorGroundTruth, ObservationRole.ExternalToolObservation, ObservationRole.ProcedureCompleted, ObservationRole.ContextualNote, ObservationRole.Consent],
            Enum.GetValues<ObservationRole>());
    }

    [Theory]
    [InlineData("desktop", "desktop")]
    [InlineData("DESKTOP", "desktop")]
    [InlineData("2", "laptop")]
    [InlineData("  miniCompact ", "miniCompact")]
    public void A_choice_is_read_by_its_token_or_its_number(string answer, string expected)
    {
        var observation = TrackAFixture.Answer(ObservationCatalog.PhysicalChassisType, answer);

        Assert.Equal(ObservationState.Known, observation.State);
        Assert.Equal(expected, observation.Text);
        Assert.Equal(ObservationRole.OperatorGroundTruth, observation.Role);
    }

    [Theory]
    [InlineData("")]
    [InlineData("u")]
    [InlineData("Unknown")]
    public void Empty_or_unknown_is_unknown_so_nobody_has_to_guess(string answer)
    {
        var observation = TrackAFixture.Answer(ObservationCatalog.PhysicalPcieSlot, answer);

        Assert.Equal(ObservationState.Unknown, observation.State);
        Assert.Null(observation.Value);
    }

    [Fact]
    public void Not_applicable_is_accepted_only_where_it_means_something()
    {
        Assert.Equal(ObservationState.NotApplicable, TrackAFixture.Answer(ObservationCatalog.DynamicRefreshIntended, "n/a").State);
        Assert.Equal(ObservationState.NotApplicable, TrackAFixture.Answer(ObservationCatalog.MotherboardOutputIntent, "NA").State);

        var (observation, error) = ObservationCatalog.PhysicalChassisType.Read("n/a");
        Assert.Null(observation);
        Assert.NotNull(error);
        Assert.Throws<ArgumentException>(() => new Observation(ObservationCatalog.PhysicalChassisType.Id, ObservationRole.OperatorGroundTruth, ObservationState.NotApplicable));
    }

    [Theory]
    [InlineData("tower")]
    [InlineData("5")]
    [InlineData("0")]
    public void An_undefined_choice_is_refused(string answer)
    {
        var (observation, error) = ObservationCatalog.PhysicalChassisType.Read(answer);

        Assert.Null(observation);
        Assert.Contains("desktop", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Integers_multi_choices_and_labels_are_typed()
    {
        Assert.Equal(new ObservedNumber(144), TrackAFixture.Answer(ObservationCatalog.MonitorSpecMaxRefreshHz, "144").Value);
        Assert.NotNull(ObservationCatalog.MonitorSpecMaxRefreshHz.Read("14").Error);
        Assert.NotNull(ObservationCatalog.MonitorSpecMaxRefreshHz.Read("144 Hz").Error);
        Assert.NotNull(ObservationCatalog.MonitorSpecMaxRefreshHz.Read("-1").Error);

        // Several choices are kept in the catalog's order, whatever order they were typed in.
        Assert.Equal(new ObservedChoices(["hdd", "nvmeSsd"]), TrackAFixture.Answer(ObservationCatalog.InstalledDriveKinds, "nvmeSsd, hdd").Value);
        Assert.NotNull(ObservationCatalog.InstalledDriveKinds.Read("hdd, floppy").Error);

        Assert.Equal("PCIEX16_1", TrackAFixture.Answer(ObservationCatalog.PhysicalPcieSlot, "PCIEX16_1").Text);
        Assert.NotNull(ObservationCatalog.PhysicalPcieSlot.Read(new string('x', ObservationField.LabelMaxLength + 1)).Error);
        Assert.NotNull(ObservationCatalog.PhysicalPcieSlot.Read("a\tb").Error);
    }

    [Fact]
    public void An_observation_must_match_its_field_s_role_tool_and_kind()
    {
        var gpuZ = ObservationCatalog.GpuZBusInterface;

        Assert.Throws<ArgumentException>(() => new Observation("hostLink", ObservationRole.OperatorGroundTruth, ObservationState.Unknown));
        Assert.Throws<ArgumentException>(() => new Observation(gpuZ.Id, ObservationRole.OperatorGroundTruth, ObservationState.Known, "GPU-Z", new ObservedText("PCIe x16 4.0")));
        Assert.Throws<ArgumentException>(() => new Observation(gpuZ.Id, ObservationRole.ExternalToolObservation, ObservationState.Known, "HWiNFO", new ObservedText("PCIe x16 4.0")));
        Assert.Throws<ArgumentException>(() => new Observation(ObservationCatalog.MonitorSpecMaxRefreshHz.Id, ObservationRole.OperatorGroundTruth, ObservationState.Known, null, new ObservedText("144")));
        Assert.Throws<ArgumentException>(() => new Observation(gpuZ.Id, ObservationRole.ExternalToolObservation, ObservationState.Unknown, "GPU-Z", new ObservedText("x")));
    }

    [Fact]
    public void An_observations_file_round_trips_and_keeps_the_catalog_order()
    {
        var document = TrackAFixture.Observations(ScenarioCatalog.PcieIdleLoad, ScenarioCatalog.IdleStep, TrackAFixture.Session,
            TrackAFixture.Answer(ObservationCatalog.OperatorNote, "slot shares lanes with M2_2"),
            TrackAFixture.Answer(ObservationCatalog.GpuZBusInterface, "PCIe x16 4.0 @ x16 1.1"),
            TrackAFixture.Answer(ObservationCatalog.PhysicalPcieSlot, ""));

        var json = TrackAJson.Serialize(document);
        var read = TrackAJson.Deserialize<OperatorObservations>(json, OperatorObservations.CurrentSchemaVersion);

        Assert.Equal(json, TrackAJson.Serialize(read));
        Assert.Equal(["physicalPcieSlot", "gpuZBusInterface", "operatorNote"], read.Observations.Select(observation => observation.Field));
        Assert.Contains("\"tool\": \"GPU-Z\"", Encoding.UTF8.GetString(json), StringComparison.Ordinal);
        Assert.Contains("\"role\": \"externalToolObservation\"", Encoding.UTF8.GetString(json), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"schemaVersion":1,"cohortId":"PC-042","sessionId":"session-20260929T120000Z","scenario":"baseline","observations":[{"field":"physicalChassisType","role":"operatorGroundTruth","state":"known","value":"tower"}]}""")] // invalid enum
    [InlineData("""{"schemaVersion":1,"cohortId":"PC-042","sessionId":"session-20260929T120000Z","scenario":"baseline","observations":[{"field":"hostLink","role":"operatorGroundTruth","state":"unknown"}]}""")] // undefined field
    [InlineData("""{"schemaVersion":1,"cohortId":"PC-042","sessionId":"session-20260929T120000Z","scenario":"baseline","observations":[{"field":"physicalPcieSlot","role":"operatorGroundTruth","state":"unknown"}]}""")] // not asked in baseline
    [InlineData("""{"schemaVersion":1,"cohortId":"PC-042","sessionId":"session-20260929T120000Z","scenario":"baseline","observations":[{"field":"physicalChassisType","role":"contextualNote","state":"unknown"}]}""")] // wrong role
    [InlineData("""{"schemaVersion":1,"cohortId":"PC-042","sessionId":"session-20260929T120000Z","scenario":"baseline","observations":[{"field":"physicalChassisType","role":"operatorGroundTruth","state":"notApplicable"}]}""")] // n/a not allowed
    [InlineData("""{"schemaVersion":1,"cohortId":"PC-042","sessionId":"session-20260929T120000Z","scenario":"baseline","observations":[{"field":"physicalChassisType","role":"operatorGroundTruth","state":"unknown"},{"field":"physicalChassisType","role":"operatorGroundTruth","state":"unknown"}]}""")] // twice
    [InlineData("""{"schemaVersion":1,"cohortId":"PC-042","sessionId":"session-20260929T120000Z","scenario":"benchmark","observations":[]}""")] // unknown scenario
    [InlineData("""{"schemaVersion":1,"cohortId":"PC-042","sessionId":"session-20260929T120000Z","scenario":"baseline","observations":[],"hostLink":"gen4"}""")] // extra property
    [InlineData("""{"schemaVersion":1,"cohortId":"PC-042","sessionId":"session-20260929T120000Z","scenario":"baseline"}""")] // missing observations
    [InlineData("""{"schemaVersion":2,"cohortId":"PC-042","sessionId":"session-20260929T120000Z","scenario":"baseline","observations":[]}""")] // unsupported version
    [InlineData("""{"schemaVersion":1,"cohortId":"PC-042","sessionId":"session-20260929T120000Z","scenario":"baseline","observations":[{"field":"monitorSpecMaxRefreshHz","role":"operatorGroundTruth","state":"known","value":"144"}]}""")] // kind mismatch (also not asked)
    public void The_reader_refuses_anything_outside_the_catalog(string json)
    {
        Assert.Throws<TrackAFormatException>(() => TrackAJson.Deserialize<OperatorObservations>(Encoding.UTF8.GetBytes(json), OperatorObservations.CurrentSchemaVersion));
    }

    [Fact]
    public void Unanswered_questions_are_listed_and_unknown_counts_as_an_answer()
    {
        var answered = TrackAFixture.Observations(ScenarioCatalog.Clone, null, TrackAFixture.Session,
            TrackAFixture.Answer(ObservationCatalog.CloneModeIntended, "yes"),
            TrackAFixture.Answer(ObservationCatalog.PhysicalMonitorsConnected, ""));
        var partial = TrackAFixture.Observations(ScenarioCatalog.Clone, null, TrackAFixture.Session,
            TrackAFixture.Answer(ObservationCatalog.CloneModeIntended, "yes"));

        Assert.Empty(answered.Unanswered(ScenarioDisposition.Performed, gameNamesPresent: false));
        Assert.Equal(["physicalMonitorsConnected"], partial.Unanswered(ScenarioDisposition.Performed, gameNamesPresent: false));
        Assert.Equal(["physicalMonitorsConnected", "gameNamesConsent"], partial.Unanswered(ScenarioDisposition.Performed, gameNamesPresent: true));
        Assert.Empty(partial.Unanswered(ScenarioDisposition.NotApplicable, gameNamesPresent: false)); // not asked when n/a
    }
}
