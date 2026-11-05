using System.Globalization;

namespace Mesura.Core.GpuControl;

/// <summary>
/// What a graphics driver says about variable refresh (G-SYNC, G-SYNC Compatible, FreeSync, Adaptive-Sync) for ONE display. <see cref="Possible"/> is whether the driver says it can be used on that display
/// (NVIDIA: <c>vrrPossible</c>; AMD: FreeSync supported); <see cref="Enabled"/> whether it is on. Both are <c>null</c> when the driver did not answer, with the reason in <see cref="Problem"/>.
/// </summary>
public sealed record VrrDisplayFact(string Vendor, int Index, string Name, string? Output, double? RefreshHz, bool? Possible, bool? Enabled, string? Problem);

/// <summary>Where one display stands.</summary>
public enum VrrState
{
    /// <summary>The driver says it is on for this display.</summary>
    Active,

    /// <summary>The driver says it is possible and it is off: something can turn it on.</summary>
    CanEnable,

    /// <summary>The driver says it is not possible for this display.</summary>
    NotAvailable,

    /// <summary>The driver did not say.</summary>
    Unknown,
}

public sealed record VrrDisplayVerdict(VrrDisplayFact Fact, VrrState State, string Explanation, string? Action);

public sealed record VrrAssessment(IReadOnlyList<VrrDisplayVerdict> Displays, bool MixedRefreshRates, string Summary);

/// <summary>
/// The owner's G1 policy (2026-11-04): variable refresh is turned on where the hardware can do it, judged for each display and each GPU on what the DRIVER says, never assumed from a monitor's model or a
/// configuration. A display the driver does not call possible is left alone and explained (not forced); several displays with different refresh rates are each judged on their own; and no frame-rate cap
/// is ever set or suggested as part of turning variable refresh on: enabling it and limiting performance are different things.
/// </summary>
public static class DisplayVrr
{
    public static VrrAssessment Assess(IEnumerable<VrrDisplayFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var verdicts = facts.Select(Judge).ToList();

        // Different refresh rates among the displays that could use variable refresh: noted, because a single driver-wide frame limit could not be right for all of them; nothing is set.
        var rates = verdicts.Where(v => v.State is VrrState.Active or VrrState.CanEnable && v.Fact.RefreshHz is not null).Select(v => Math.Round(v.Fact.RefreshHz!.Value)).Distinct().ToList();
        var mixed = rates.Count > 1;

        var active = verdicts.Count(v => v.State == VrrState.Active);
        var can = verdicts.Count(v => v.State == VrrState.CanEnable);
        var no = verdicts.Count(v => v.State == VrrState.NotAvailable);
        var unknown = verdicts.Count(v => v.State == VrrState.Unknown);
        var summary = verdicts.Count == 0
            ? "No display was reported by a graphics driver that offers variable-refresh information."
            : string.Create(CultureInfo.InvariantCulture, $"{verdicts.Count} display(s): {active} with variable refresh on, {can} that can turn it on, {no} that the driver says cannot, {unknown} unknown.")
                + (mixed ? " The displays run at different refresh rates; Mesura sets no frame-rate limit, and a single driver-wide limit would suit only one of them." : string.Empty);
        return new(verdicts, mixed, summary);
    }

    private static VrrDisplayVerdict Judge(VrrDisplayFact fact)
    {
        if (fact.Possible is null)
        {
            return new(fact, VrrState.Unknown, $"The {fact.Vendor} driver did not say whether variable refresh is possible on this display ({fact.Problem ?? "no answer"}). Nothing is changed on a guess.", null);
        }

        if (fact.Possible is false)
        {
            return new(fact, VrrState.NotAvailable,
                "The driver says variable refresh is not possible on this display. Either the monitor does not offer Adaptive-Sync / FreeSync / G-SYNC Compatible on this connection, or it is switched off in the monitor's own menu, or the port or cable does not carry it. The driver does not say which, and Mesura cannot change a monitor's menu.",
                "Check the monitor's menu for Adaptive-Sync / FreeSync / G-SYNC Compatible, then read again.");
        }

        if (fact.Enabled is true)
        {
            return new(fact, VrrState.Active, "Variable refresh is on for this display.", null);
        }

        return new(fact, VrrState.CanEnable, "The driver says variable refresh is possible on this display and it is off.", fact.Vendor == "AMD"
            ? $"mesura gpu amd set freesync on --display {fact.Index.ToString(CultureInfo.InvariantCulture)} (preview; add --apply to write it)"
            : "Turn on G-SYNC for this display in NVIDIA Control Panel (Set up G-SYNC), or through the NVIDIA profile's G-SYNC settings. Mesura does not change the NVIDIA profile from this report.");
    }
}
