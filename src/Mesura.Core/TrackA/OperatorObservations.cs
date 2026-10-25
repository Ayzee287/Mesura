using System.Text.Json.Serialization;

namespace Mesura.Core.TrackA;

/// <summary>
/// The operator's answers for one session (<c>operator-observations.json</c>, architecture §4.7): only what the software
/// can't read, each with its role. It sits beside the normalised facts and never feeds them: nothing in the normaliser
/// or the checks reads this type.
/// </summary>
public sealed record OperatorObservations
{
    public const int CurrentSchemaVersion = 1;

    [JsonConstructor]
    public OperatorObservations(int schemaVersion, string cohortId, string sessionId, string scenario, IReadOnlyList<Observation> observations, string? step = null)
    {
        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion), schemaVersion, $"Only operator-observations schema version {CurrentSchemaVersion} exists.");
        }

        ArgumentNullException.ThrowIfNull(observations);
        var definition = ScenarioCatalog.Require(scenario);
        var allowed = definition.Step(step).Fields.Concat(CommonFields).Select(field => field.Id).ToHashSet(StringComparer.Ordinal);

        if (observations.Any(observation => observation is null))
        {
            throw new ArgumentException("An observation list has no null item.", nameof(observations));
        }

        if (observations.FirstOrDefault(observation => !allowed.Contains(observation.Field)) is { } stray)
        {
            throw new ArgumentException($"{stray.Field} is not asked in {scenario}{(step is null ? "" : "/" + step)}.", nameof(observations));
        }

        if (observations.DistinctBy(observation => observation.Field).Count() != observations.Count)
        {
            throw new ArgumentException("Each field is answered once.", nameof(observations));
        }

        SchemaVersion = schemaVersion;
        CohortId = TrackA.CohortId.Require(cohortId, nameof(cohortId));
        SessionId = TrackA.SessionId.Require(sessionId, nameof(sessionId));
        Scenario = scenario;
        Step = step;
        Observations = [.. observations.OrderBy(observation => ObservationCatalog.OrderOf(observation.Field))];
    }

    /// <summary>Asked in every step: the note, and the consent when the package would hold game names.</summary>
    public static IReadOnlyList<ObservationField> CommonFields { get; } = [ObservationCatalog.GameNamesConsent, ObservationCatalog.OperatorNote];

    public int SchemaVersion { get; }

    public string CohortId { get; }

    public string SessionId { get; }

    public string Scenario { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Step { get; }

    /// <summary>In <see cref="ObservationCatalog.All"/>'s order.</summary>
    public IReadOnlyList<Observation> Observations { get; }

    public Observation? Find(ObservationField field) => Observations.FirstOrDefault(observation => observation.Field == field.Id);

    /// <summary>
    /// The questions still unanswered for a session: the step's fields when the scenario was carried out, and the consent
    /// when the package holds game names. An "unknown" or "not applicable" answer is an answer.
    /// </summary>
    public IReadOnlyList<string> Unanswered(ScenarioDisposition disposition, bool gameNamesPresent)
    {
        var required = new List<ObservationField>();
        if (disposition == ScenarioDisposition.Performed)
        {
            required.AddRange(ScenarioCatalog.Require(Scenario).Step(Step).Fields.Where(field => !field.Optional));
        }

        if (gameNamesPresent)
        {
            required.Add(ObservationCatalog.GameNamesConsent);
        }

        return [.. required.Where(field => Find(field) is null).Select(field => field.Id)];
    }
}
