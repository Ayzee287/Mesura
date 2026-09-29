namespace FpsUtility.Core.Checks;

/// <summary>
/// A condition a Confirmed result may rest on, unlocked by one committed fixture (architecture §6.3 policy, §4.6).
/// </summary>
/// <param name="Check">The check id.</param>
/// <param name="Condition">The condition key the check names, e.g. <c>problem/external</c>.</param>
/// <param name="Fixture">
/// The scrubbed fixture committed under D016 whose <b>owner-labelled</b> expected result agrees with this outcome. The
/// label comes from the owner's knowledge of the PC (known configuration, CPU-Z/GPU-Z), never from the check's own
/// output: a check that wrote its own label would unlock its own false positives.
/// </param>
public sealed record GatedCondition(string Check, string Condition, string Fixture)
{
    public string Check { get; } = Codes.CheckId(Check, nameof(Check));

    public string Condition { get; } = string.IsNullOrWhiteSpace(Condition) || !Condition.Contains('/', StringComparison.Ordinal)
        ? throw new ArgumentException("A condition key names the verdict and its conditions.", nameof(Condition))
        : Condition;

    public string Fixture { get; } = string.IsNullOrWhiteSpace(Fixture)
        ? throw new ArgumentException("A condition is unlocked by a named fixture.", nameof(Fixture))
        : Fixture;
}

/// <summary>
/// The fixture gate (architecture §6.3: "Confirmed only for conditions covered by a real-PC fixture"; Decision B,
/// 2026-09-29). A Problem or Ok with no downgrade is Confirmed only when its condition is unlocked here; otherwise it
/// stays Likely with a gate step. A value read straight from Windows is not a validated conclusion, so nothing is
/// unlocked by default.
/// </summary>
public sealed class FixtureGate
{
    public FixtureGate(IEnumerable<GatedCondition> unlocked)
    {
        ArgumentNullException.ThrowIfNull(unlocked);
        var list = unlocked.ToList();
        if (list.Any(condition => condition is null) || list.DistinctBy(condition => (condition.Check, condition.Condition)).Count() != list.Count)
        {
            throw new ArgumentException("Each condition is unlocked once.", nameof(unlocked));
        }

        Unlocked = [.. list.OrderBy(condition => condition.Check, StringComparer.Ordinal).ThenBy(condition => condition.Condition, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The product's gate. <b>Empty</b>: no committed fixture has an owner-labelled expected result yet, and PC-000
    /// unlocks nothing (it ran on battery and offers one refresh rate). Adding an entry needs that label.
    /// </summary>
    public static FixtureGate Validated { get; } = new([]);

    public IReadOnlyList<GatedCondition> Unlocked { get; }

    public bool Unlocks(string check, string condition) =>
        Unlocked.Any(entry => entry.Check == check && entry.Condition == condition);
}
