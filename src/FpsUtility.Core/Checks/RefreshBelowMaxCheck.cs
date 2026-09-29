using FpsUtility.Core.Facts;
using FpsUtility.Core.Raw;

namespace FpsUtility.Core.Checks;

/// <summary>
/// Check 1, <c>display.refresh-below-max</c> (architecture §6.5, §4.6): for each active output, is the signal's
/// refresh more than 1% below the highest progressive refresh Windows offers the output's desktop source at the
/// current desktop resolution?
/// </summary>
/// <remarks>
/// It says nothing about the monitor's own maximum (that is check 3, from the EDID, step 4), nothing about other
/// resolutions, colour depth or HDR, and nothing about frame rate or intent. The offered list is Windows' list for the
/// source, filtered to the connected monitor(s) and the current orientation.
/// </remarks>
public sealed class RefreshBelowMaxCheck : ICheck
{
    /// <summary>The aspect evaluated: the signal's refresh against the highest offered at this resolution (Decision C).</summary>
    public const string FindingCode = "refresh-vs-highest-offered";

    /// <summary>
    /// §6.5: Problem when <c>highest &gt; current × 1.01</c>. The tolerance is 1% of the <b>current</b> rate, so a current
    /// rate exactly 1% below the highest (99 of 100) is a Problem, and 100 of 101 is not.
    /// </summary>
    public const int TolerancePercent = 1;

    public string Id => CheckIds.RefreshBelowMax;

    public IReadOnlyList<CheckResult> Evaluate(NormalizedSnapshot facts, FixtureGate gate)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(gate);

        var session = facts.Os.Session;
        if (session is { IsKnown: true, Value: SessionType.Remote })
        {
            return [Machine()
                .Add(EvidenceRole.Applicability, FactPaths.Session, session)
                .Step(TraceKind.Applicability, "remote-session")
                .NotApplicable()];
        }

        var displays = facts.Displays;
        if (displays is { IsKnown: true, Value.Count: 0 })
        {
            return [Machine()
                .Add(EvidenceRole.Applicability, FactPaths.Displays, displays)
                .Step(TraceKind.Applicability, "no-active-display-path")
                .NotApplicable()];
        }

        if (!displays.IsKnown)
        {
            var unknown = Machine();
            RequireLocalSession(unknown, session);
            unknown.Require(EvidenceRole.Applicability, FactPaths.Displays, displays, "displays-missing");
            return [unknown.CannotDetermine()];
        }

        return [.. displays.Value!.Select((display, index) => Evaluate(facts, display, index, gate))];
    }

    /// <summary>
    /// Problem when the highest offered refresh exceeds the current one by more than <see cref="TolerancePercent"/>% of
    /// it: <c>100 × highest × den &gt; 101 × num</c>. Exact: no floating point, no division, and no overflow (the left
    /// side reaches about 2^70, so it is computed in 128 bits).
    /// </summary>
    public static bool IsBelow(RefreshRate current, int highestHz)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(highestHz);
        return (UInt128)100 * (uint)highestHz * current.Denominator > (UInt128)(100 + TolerancePercent) * current.Numerator;
    }

    /// <summary>
    /// The current rate is at least 1 Hz above the highest offered. Offered modes are listed in whole Hz and may be
    /// truncated (59.94 Hz listed as 59), so anything less than a whole hertz above is not a conflict.
    /// </summary>
    public static bool IsAboveOffered(RefreshRate current, int highestHz)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(highestHz);
        return (UInt128)current.Numerator >= ((UInt128)(uint)highestHz + 1) * current.Denominator;
    }

    /// <summary>Two exact rates are the same rate (60/1 and 60000/1000 are).</summary>
    public static bool AreEqual(RefreshRate a, RefreshRate b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return (UInt128)a.Numerator * b.Denominator == (UInt128)b.Numerator * a.Denominator;
    }

    private CheckResult Evaluate(NormalizedSnapshot facts, DisplayFacts display, int index, FixtureGate gate)
    {
        var result = new ResultBuilder(Id, FindingCode, display.Id);
        string At(string member) => FactPaths.Display(index, member);

        var connection = display.Connection;
        var kind = connection.IsKnown ? Policies.KindOf(connection.Value) : (DisplayKind?)null;
        if (kind == DisplayKind.Virtual)
        {
            return result
                .Add(EvidenceRole.Applicability, At(nameof(DisplayFacts.Connection)), connection)
                .Step(TraceKind.Applicability, "virtual-display", new NamedValue("connection", CheckValue.Of(connection.Value)))
                .NotApplicable();
        }

        RequireLocalSession(result, facts.Os.Session);
        if (result.Require(EvidenceRole.Applicability, At(nameof(DisplayFacts.Connection)), connection, "connection-missing"))
        {
            result.Step(TraceKind.Applicability, kind switch
            {
                DisplayKind.Internal => "internal-display",
                DisplayKind.External => "external-display",
                _ => "unrecognised-display",
            }, new NamedValue("connection", CheckValue.Of(connection.Value)));
        }

        var resolutionPath = At(nameof(DisplayFacts.DesktopResolution));
        var signalPath = At(nameof(DisplayFacts.SignalRefresh));
        var modesPath = At(nameof(DisplayFacts.OfferedModes));
        result.Require(EvidenceRole.Premise, resolutionPath, display.DesktopResolution, "desktop-resolution-missing");
        result.Require(EvidenceRole.Premise, signalPath, display.SignalRefresh, "signal-refresh-missing");
        result.Require(EvidenceRole.Premise, modesPath, display.OfferedModes, "offered-modes-missing");
        if (result.IsMissingSomething)
        {
            return result.CannotDetermine();
        }

        var resolution = display.DesktopResolution.Value!;
        var current = display.SignalRefresh.Value!;
        var progressive = display.OfferedModes.Value!
            .Where(mode => mode.Width == resolution.Width && mode.Height == resolution.Height && !mode.Interlaced)
            .ToList();
        if (progressive.Count == 0)
        {
            // Interlaced modes don't count, and the current signal's scan order isn't observed: no comparison is possible.
            return result.Missing("no-progressive-mode-at-resolution", modesPath, resolutionPath).CannotDetermine();
        }

        var highest = progressive.Max(mode => mode.RefreshHz);
        var compared = new[] { new NamedValue("current", CheckValue.Of(current)), new NamedValue("highestOffered", CheckValue.Of(highest)) };
        if (IsAboveOffered(current, highest))
        {
            return result
                .Step(TraceKind.Comparison, "current-above-offered", compared)
                .Missing("current-above-offered", signalPath, modesPath)
                .CannotDetermine();
        }

        var tolerance = new NamedValue("tolerancePercent", CheckValue.Of(TolerancePercent));
        var verdict = IsBelow(current, highest) ? Verdict.Problem : Verdict.Ok;
        result
            .Step(TraceKind.Comparison, FindingCode, [.. compared, tolerance])
            .Param("current", compared[0].Value)
            .Param("highestOffered", compared[1].Value)
            .Param("resolution", CheckValue.Of(resolution))
            .Param("connection", CheckValue.Of(connection.Value))
            .Param("tolerancePercent", tolerance.Value);

        var conditions = kind == DisplayKind.Internal ? "internal" : "external";
        if (kind == DisplayKind.Internal)
        {
            // Vendors lower an internal panel's refresh on battery on purpose, and may offer other modes then.
            var power = facts.Power.OnAcPower;
            if (power is { IsKnown: true, Value: true })
            {
                result.Add(EvidenceRole.Premise, FactPaths.OnAcPower, power);
                conditions = "internal/ac";
            }
            else
            {
                result.Downgrade(FactPaths.OnAcPower, power, power.IsKnown ? "on-battery" : "power-unknown");
            }

            if (verdict == Verdict.Problem)
            {
                // Under dynamic refresh the path may carry the lower virtual rate; the collector can't see that state yet.
                result.Step(TraceKind.Downgrade, "dynamic-refresh-unobserved");
            }
        }
        else if (kind == DisplayKind.Unrecognised)
        {
            result.Step(TraceKind.Downgrade, "connection-unrecognised");
        }

        var pathRefresh = display.PathRefresh;
        var pathAt = At(nameof(DisplayFacts.PathRefresh));
        if (!pathRefresh.IsKnown)
        {
            result.Downgrade(pathAt, pathRefresh, "path-refresh-unknown");
        }
        else if (!AreEqual(pathRefresh.Value!, current))
        {
            // The documented signature of dynamic refresh: a virtual rate on the path, the physical one on the signal.
            result.Downgrade(pathAt, pathRefresh, "path-signal-differ", new NamedValue("pathRefresh", CheckValue.Of(pathRefresh.Value!)));
        }
        else
        {
            result.Add(EvidenceRole.Premise, pathAt, pathRefresh);
        }

        var gpu = display.Gpu;
        if (gpu.IsKnown)
        {
            result.Add(EvidenceRole.Premise, At(nameof(DisplayFacts.Gpu)), gpu);
        }
        else
        {
            // No adapter claims the output: the one observable hint of an indirect (virtual) display driver.
            result.Downgrade(At(nameof(DisplayFacts.Gpu)), gpu, "display-adapter-unattributed");
        }

        var shared = display.SharesSourceWith;
        var sharedAt = At(nameof(DisplayFacts.SharesSourceWith));
        if (!shared.IsKnown)
        {
            result.Downgrade(sharedAt, shared, "shared-source-unknown");
        }
        else if (shared.Value!.Count > 0)
        {
            // Clone outputs share one source's mode list, which the platform limits by all of them.
            result.Downgrade(sharedAt, shared, "shared-source", new NamedValue("displays", CheckValue.Symbols(shared.Value)));
        }
        else
        {
            result.Add(EvidenceRole.Premise, sharedAt, shared);
        }

        return result.Decide(verdict, conditions, gate);
    }

    private static void RequireLocalSession(ResultBuilder result, Fact<SessionType> session)
    {
        if (result.Require(EvidenceRole.Applicability, FactPaths.Session, session, "session-missing"))
        {
            result.Step(TraceKind.Applicability, "local-session");
        }
    }

    private ResultBuilder Machine() => new(Id, FindingCode, ResultBuilder.Machine);
}
