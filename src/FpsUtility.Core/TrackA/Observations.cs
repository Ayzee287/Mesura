using System.Globalization;
using System.Text.Json.Serialization;
using FpsUtility.Core.Raw.Serialization;

namespace FpsUtility.Core.TrackA;

/// <summary>
/// What kind of statement an operator observation is (architecture §4.7). Each field in
/// <see cref="ObservationCatalog"/> has exactly one role, and the JSON repeats it so a reader never has to guess.
/// None of them is a machine fact: the normaliser never reads an observation, so a role can't make one.
/// </summary>
[JsonConverter(typeof(CamelCaseEnumConverter<ObservationRole>))]
public enum ObservationRole
{
    /// <summary>What the operator knows about this PC that the software can't read: a slot label, the cabling, what they set in Windows.</summary>
    OperatorGroundTruth,

    /// <summary>What another program (GPU-Z, HWiNFO) showed, transcribed by the operator. Evidence about the tool's reading, never a fact.</summary>
    ExternalToolObservation,

    /// <summary>That the operator carried out a step of the scenario's procedure (idle wait, stable gameplay).</summary>
    ProcedureCompleted,

    /// <summary>Free text for a person. Never parsed.</summary>
    ContextualNote,

    /// <summary>The PC owner's permission, as the operator records it (keeping the Steam game list in the corpus).</summary>
    Consent,
}

/// <summary>Whether an asked question was answered with a value.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<ObservationState>))]
public enum ObservationState
{
    Known,

    /// <summary>The operator doesn't know. Always allowed: nobody is made to guess.</summary>
    Unknown,

    /// <summary>The question doesn't apply to this PC (only where the field allows it).</summary>
    NotApplicable,
}

/// <summary>The type of a field's value.</summary>
public enum ObservationKind
{
    /// <summary>One token from the field's list.</summary>
    Choice,

    /// <summary>One or more distinct tokens from the field's list, in the list's order.</summary>
    MultiChoice,

    /// <summary>A whole number in the field's range.</summary>
    Integer,

    /// <summary>A short single-line text as shown or printed somewhere (a slot label, a tool's reading, a game's name).</summary>
    Label,

    /// <summary>A single-line free-text note (only <see cref="ObservationCatalog.OperatorNote"/>).</summary>
    Note,
}

/// <summary>An observation's value: a closed set of kinds, never an <c>object</c>.</summary>
public abstract record ObservedValue
{
    private protected ObservedValue()
    {
    }
}

/// <summary>A choice token, a label or a note.</summary>
public sealed record ObservedText(string Text) : ObservedValue
{
    private readonly bool _notNull = Text is not null ? true : throw new ArgumentNullException(nameof(Text));
}

/// <summary>Several choice tokens.</summary>
public sealed record ObservedChoices : ObservedValue
{
    public ObservedChoices(IReadOnlyList<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Any(item => item is null))
        {
            throw new ArgumentException("A choice list has no null item.", nameof(items));
        }

        Items = [.. items];
    }

    public IReadOnlyList<string> Items { get; }

    public bool Equals(ObservedChoices? other) => other is not null && Items.SequenceEqual(other.Items, StringComparer.Ordinal);

    public override int GetHashCode() => Items.Count;
}

public sealed record ObservedNumber(int Number) : ObservedValue;

/// <summary>
/// One question the kit can ask (architecture §4.7): its id, role, value type and allowed values are fixed here, so an
/// observation file can't carry a field nobody defined.
/// </summary>
/// <param name="Id">The field's name in JSON (camelCase).</param>
/// <param name="Question">The prompt shown to the operator.</param>
/// <param name="Help">Where the answer comes from, shown under the prompt.</param>
/// <param name="Choices">The allowed tokens of a choice field, in order.</param>
/// <param name="AllowsNotApplicable">Whether "doesn't apply to this PC" is a meaningful answer.</param>
/// <param name="Optional">An empty answer records nothing (only the note); otherwise it records "unknown".</param>
/// <param name="Tool">For an external tool observation, the tool.</param>
public sealed record ObservationField(
    string Id,
    ObservationRole Role,
    ObservationKind Kind,
    string Question,
    string Help,
    IReadOnlyList<string> Choices,
    int Minimum,
    int Maximum,
    bool AllowsNotApplicable,
    bool Optional,
    string? Tool)
{
    public const int LabelMaxLength = 80;
    public const int NoteMaxLength = 500;

    /// <summary>Checks a value against the field. Returns the reason it doesn't fit, or <c>null</c>.</summary>
    public string? Problem(ObservedValue value)
    {
        ArgumentNullException.ThrowIfNull(value);

        switch (Kind, value)
        {
            case (ObservationKind.Choice, ObservedText text):
                return Choices.Contains(text.Text, StringComparer.Ordinal) ? null : $"'{text.Text}' is not one of: {string.Join(", ", Choices)}.";
            case (ObservationKind.MultiChoice, ObservedChoices list):
                if (list.Items.Count == 0 || list.Items.Distinct(StringComparer.Ordinal).Count() != list.Items.Count)
                {
                    return "Give one or more different values.";
                }

                if (list.Items.FirstOrDefault(item => !Choices.Contains(item, StringComparer.Ordinal)) is { } unknown)
                {
                    return $"'{unknown}' is not one of: {string.Join(", ", Choices)}.";
                }

                return list.Items.SequenceEqual(Choices.Where(list.Items.Contains), StringComparer.Ordinal) ? null : "The values are listed in the field's order.";
            case (ObservationKind.Integer, ObservedNumber number):
                return number.Number >= Minimum && number.Number <= Maximum
                    ? null
                    : string.Create(CultureInfo.InvariantCulture, $"A whole number from {Minimum} to {Maximum}.");
            case (ObservationKind.Label, ObservedText text):
                return TextProblem(text.Text, LabelMaxLength);
            case (ObservationKind.Note, ObservedText text):
                return TextProblem(text.Text, NoteMaxLength);
            default:
                return $"{Id} takes a {Kind} value.";
        }
    }

    /// <summary>
    /// Reads an operator's typed answer: empty is "unknown" (or nothing, for an optional field); <c>unknown</c> or
    /// <c>u</c> is unknown; <c>n/a</c> is not applicable where allowed; otherwise a value (a choice may be its number
    /// in the list; several choices are separated by commas or spaces).
    /// </summary>
    /// <returns>The observation, <c>null</c> for an optional field left empty, or the reason the answer can't be used.</returns>
    public (Observation? Observation, string? Error) Read(string? answer)
    {
        var text = (answer ?? "").Trim();

        if (text.Length == 0)
        {
            return Optional ? (null, null) : (new Observation(Id, Role, ObservationState.Unknown, Tool), null);
        }

        if (text.Equals("unknown", StringComparison.OrdinalIgnoreCase) || text.Equals("u", StringComparison.OrdinalIgnoreCase))
        {
            return (new Observation(Id, Role, ObservationState.Unknown, Tool), null);
        }

        if (text.Equals("n/a", StringComparison.OrdinalIgnoreCase) || text.Equals("na", StringComparison.OrdinalIgnoreCase))
        {
            return AllowsNotApplicable
                ? (new Observation(Id, Role, ObservationState.NotApplicable, Tool), null)
                : (null, "\"Not applicable\" isn't an answer to this question; answer, or leave it empty for unknown.");
        }

        ObservedValue? value = Kind switch
        {
            ObservationKind.Choice => Choice(text) is { } choice ? new ObservedText(choice) : null,
            ObservationKind.MultiChoice => MultiChoice(text),
            ObservationKind.Integer => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? new ObservedNumber(number) : null,
            _ => new ObservedText(text),
        };

        if (value is null)
        {
            return (null, Kind == ObservationKind.Integer ? "Type a whole number, or leave it empty for unknown." : $"Choose from: {string.Join(", ", Choices)}.");
        }

        return Problem(value) is { } problem ? (null, problem) : (new Observation(Id, Role, ObservationState.Known, Tool, value), null);
    }

    private string? Choice(string text)
    {
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 1 && index <= Choices.Count)
        {
            return Choices[index - 1];
        }

        return Choices.FirstOrDefault(choice => choice.Equals(text, StringComparison.OrdinalIgnoreCase));
    }

    private ObservedChoices? MultiChoice(string text)
    {
        var picked = new List<string>();
        foreach (var part in text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (Choice(part) is not { } choice)
            {
                return null;
            }

            picked.Add(choice);
        }

        return new ObservedChoices([.. Choices.Where(picked.Contains)]);
    }

    private static string? TextProblem(string text, int maxLength)
    {
        if (text.Length == 0 || text != text.Trim())
        {
            return "The text is empty or has leading or trailing spaces.";
        }

        if (text.Length > maxLength)
        {
            return string.Create(CultureInfo.InvariantCulture, $"At most {maxLength} characters.");
        }

        return text.Any(char.IsControl) ? "One line of text, without control characters." : null;
    }
}

/// <summary>
/// One answered question (architecture §4.7): the field, its role (and tool), whether it was answered, and the typed
/// value when it was. The constructor checks it against <see cref="ObservationCatalog"/>, so an observation of an
/// undefined field, with the wrong role, or with a value outside the field's type can't exist.
/// </summary>
public sealed record Observation
{
    [JsonConstructor]
    public Observation(string field, ObservationRole role, ObservationState state, string? tool = null, ObservedValue? value = null)
    {
        var definition = ObservationCatalog.Find(field) ?? throw new ArgumentException($"'{field}' is not an observation field.", nameof(field));

        if (role != definition.Role)
        {
            throw new ArgumentException($"{field} is a {definition.Role} observation, not {role}.", nameof(role));
        }

        if (!string.Equals(tool, definition.Tool, StringComparison.Ordinal))
        {
            throw new ArgumentException($"{field} names the tool '{definition.Tool}'.", nameof(tool));
        }

        switch (state)
        {
            case ObservationState.Known when value is null:
                throw new ArgumentException($"A known {field} has a value.", nameof(value));
            case ObservationState.Known when definition.Problem(value) is { } problem:
                throw new ArgumentException($"{field}: {problem}", nameof(value));
            case ObservationState.Unknown or ObservationState.NotApplicable when value is not null:
                throw new ArgumentException($"An unanswered {field} has no value.", nameof(value));
            case ObservationState.NotApplicable when !definition.AllowsNotApplicable:
                throw new ArgumentException($"{field} can't be not applicable.", nameof(state));
            case ObservationState.Known or ObservationState.Unknown or ObservationState.NotApplicable:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, null);
        }

        Field = field;
        Role = role;
        Tool = tool;
        State = state;
        Value = value;
    }

    public string Field { get; }

    public ObservationRole Role { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tool { get; }

    public ObservationState State { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ObservedValue? Value { get; }

    /// <summary>The value's text for a known choice, label or note field; otherwise <c>null</c>.</summary>
    [JsonIgnore]
    public string? Text => (Value as ObservedText)?.Text;
}
