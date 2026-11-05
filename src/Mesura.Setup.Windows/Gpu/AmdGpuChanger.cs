using System.Globalization;
using Mesura.Core.GpuControl;
using Mesura.Platform.Windows.Gpu.Adlx;

namespace Mesura.Setup.Windows.Gpu;

/// <summary>One requested change: which switch, on which GPU (and display, for FreeSync), to on or off.</summary>
public sealed record AmdChange(GpuFeature Feature, int GpuIndex, int? DisplayIndex, bool Enable);

/// <summary>How a request ended, in words, with the preview it was based on and the record that was kept.</summary>
public sealed record AmdChangeOutcome(GpuChangeStatus Status, string Message, GpuChangePreview? Preview, GpuChangeRecord? Record);

/// <summary>
/// Changes AMD GPU switches through ADLX (0.6): Anti-Lag (the original, level 0), SmartAccess Memory and FreeSync on one display. This is the only place Mesura calls an ADLX setter; ADLX is
/// read in Platform, which has no write call. Every change has the same four steps:
/// <list type="number">
/// <item><b>Preview</b>: fresh readings, what would be written and what the driver does beside it, and what blocks it. Nothing is written.</item>
/// <item><b>Record first</b>: the state from before, kept on disk, then the write.</item>
/// <item><b>Independent read-back</b>: ADLX is closed and opened again and the switch is read by the new session; "applied" is said only when that reading shows the requested state. A driver that answers
/// success but leaves the state alone is "not confirmed".</item>
/// <item><b>Rollback</b>: from the record, the target and everything the driver changed beside it (Chill, Boost) go back, and a new session reads them again.</item>
/// </list>
/// Anti-Lag Next (level 1) and Anti-Lag+ are never enabled or selected: a request that finds level 1 is refused, and no level setter exists in this class. The command line runs it only with <c>--apply</c>. Since 0.6.0 the profile engine reaches it, for the original Anti-Lag only,
/// through <c>WindowsWriter</c> (in the command line directly; from the window through <c>AmdCommandLineRunner</c>, so a crash inside AMD's library never ends the window).
/// </summary>
public sealed class AmdGpuChanger
{
    // Slots of the setters in the C interface structs of ADLX 1.5 and 2.0; tools\check-adlx-abi.ps1 checks them together with the readers' slots.
    private const int SetEnabledSlot = 5;
    private const int ChillSetEnabledSlot = 8;
    private const int BoostSetEnabledSlot = 7;

    private readonly SetupStore _store;
    private readonly Func<AdlxEntryPoints?> _entry;
    private readonly Func<DateTimeOffset> _now;

    public AmdGpuChanger(SetupStore store)
        : this(store, static () => AdlxEntryPoints.LoadSystemLibrary(out _), null)
    {
    }

    internal AmdGpuChanger(SetupStore store, Func<AdlxEntryPoints?> entry, Func<DateTimeOffset>? now)
    {
        _store = store;
        _entry = entry;
        _now = now ?? (static () => DateTimeOffset.UtcNow);
    }

    // ---- preview ---------------------------------------------------------------------------------------------------------------------------------------------

    public AmdChangeOutcome Preview(AmdChange change)
    {
        var entry = _entry();
        if (entry is null)
        {
            return new(GpuChangeStatus.Failed, "ADLX is not available on this PC (no AMD driver with ADLX).", null, null);
        }

        using var session = AdlxSession.TryOpen(entry, out var failure, out var result);
        if (session is null)
        {
            return new(GpuChangeStatus.Failed, $"ADLX would not start ({failure}, {result}).", null, null);
        }

        using var context = Context.Open(session, change.GpuIndex, null, change.DisplayIndex, change.Feature == GpuFeature.FreeSync, out var error);
        if (context is null)
        {
            return new(GpuChangeStatus.Failed, error ?? "The GPU could not be opened.", null, null);
        }

        var preview = BuildPreview(context, change);
        return new(GpuChangeStatus.NotWritten, preview.CanApply ? preview.Plan : preview.Blockers.Count > 0 ? string.Join(" ", preview.Blockers) : "Already in that state; nothing to change.", preview, null);
    }

    private static GpuChangePreview BuildPreview(Context context, AmdChange change)
    {
        var target = ReadTarget(context, change.Feature);
        var related = ReadRelated(context, change.Feature);
        var blockers = new List<string>();
        var effects = new List<string>();
        var name = change.Feature switch { GpuFeature.AntiLag => "Anti-Lag", GpuFeature.SmartAccessMemory => "SmartAccess Memory", _ => "FreeSync" };

        if (change.Feature == GpuFeature.FreeSync && context.Display is null)
        {
            blockers.Add("There is no display with that index.");
        }
        else if (target.Supported is not true)
        {
            blockers.Add(target.Supported is false
                ? $"The driver says {name} is not supported here." + (change.Feature == GpuFeature.FreeSync ? " The monitor may have Adaptive-Sync switched off in its own menu or not offer it; Mesura cannot change that." : change.Feature == GpuFeature.SmartAccessMemory ? " It also needs Above 4G Decoding and Re-Size BAR in the BIOS, which Mesura never changes." : string.Empty)
                : $"The driver does not offer {name} ({target.Note ?? "no answer"}).");
        }
        else if (target.Enabled is null)
        {
            blockers.Add($"The state of {name} could not be read ({target.Note ?? "no answer"}); nothing is written on a guess.");
        }

        if (change.Feature == GpuFeature.AntiLag && target.Supported is true && change.Enable)
        {
            if (target.Level is 1)
            {
                blockers.Add("Anti-Lag Next is the selected level; Mesura never enables or changes it.");
            }
            else if (target.Level is not null and not 0)
            {
                blockers.Add($"Anti-Lag reports an unknown level ({target.Level}); only the original (level 0) is ever enabled.");
            }
            else if (target.Level is null && target.Note is { } note && note.Contains("level:", StringComparison.Ordinal))
            {
                blockers.Add("The Anti-Lag level could not be read; nothing is written until it is known to be the original (0).");
            }

            if (related.TryGetValue("Chill", out var chill) && chill.Enabled is true)
            {
                effects.Add("Radeon Chill is on: the driver turns it off while Anti-Lag is on. Mesura records its state and puts it back on undo.");
            }

            if (related.TryGetValue("Boost", out var boost) && boost.Enabled is true)
            {
                effects.Add("Radeon Boost is on: the driver may change it too (it depends on Chill). Recorded and put back on undo.");
            }
        }

        if (change.Feature == GpuFeature.SmartAccessMemory && change.Enable)
        {
            effects.Add("The driver may need a restart before SmartAccess Memory is in effect; until then the read-back will not call it applied. The BIOS is not touched.");
        }

        var already = blockers.Count == 0 && target.Enabled == change.Enable;
        var plan = $"Turn {name} {(change.Enable ? "on" : "off")} on {context.GpuName}{(context.DisplayName is { } d ? $", display '{d}'" : string.Empty)} (now {(target.Enabled is { } e ? e ? "on" : "off" : "unknown")}). "
            + "The state is recorded first, written, then read again by a new ADLX session.";
        return new(change.Feature, context.GpuName, context.DisplayName, change.Enable, target, related, plan, effects, blockers, already);
    }

    // ---- apply -----------------------------------------------------------------------------------------------------------------------------------------------

    public AmdChangeOutcome Apply(AmdChange change)
    {
        var entry = _entry();
        if (entry is null)
        {
            return new(GpuChangeStatus.Failed, "ADLX is not available on this PC (no AMD driver with ADLX).", null, null);
        }

        GpuChangePreview preview;
        GpuChangeRecord record;
        AdlxResult written;
        using (var session = AdlxSession.TryOpen(entry, out var failure, out var result))
        {
            if (session is null)
            {
                return new(GpuChangeStatus.Failed, $"ADLX would not start ({failure}, {result}).", null, null);
            }

            using var context = Context.Open(session, change.GpuIndex, null, change.DisplayIndex, change.Feature == GpuFeature.FreeSync, out var error);
            if (context is null)
            {
                return new(GpuChangeStatus.Failed, error ?? "The GPU could not be opened.", null, null);
            }

            preview = BuildPreview(context, change);
            if (!preview.CanApply)
            {
                return new(GpuChangeStatus.NotWritten, preview.Blockers.Count > 0 ? string.Join(" ", preview.Blockers) : "Already in that state; nothing was written.", preview, null);
            }

            var id = $"{_now():yyyyMMdd'T'HHmmssfff'Z'}-gpu-{change.Feature.ToString().ToLowerInvariant()}";
            record = new GpuChangeRecord(id, _now(), "AMD", change.Feature, context.GpuName, context.GpuId, context.DisplayName, change.DisplayIndex, change.Enable, preview.Current, preview.Related,
                GpuChangeStatus.Written, "Recorded before the write.", null, null);
            _store.WriteGpuChange(id, record.ToJson());   // before the driver is touched

            var toggle = TargetToggle(context, change.Feature);
            if (toggle.Value is null)
            {
                return Finish(record with { Status = GpuChangeStatus.Failed, Detail = $"The switch could not be obtained for writing: {toggle.Result}." }, preview);
            }

            using (toggle.Value)
            {
                written = SetEnabled(toggle.Value, SetEnabledSlot, change.Enable);
            }
        }

        // The independent reading: a new session.
        var (after, relatedAfter, readProblem) = ReadFresh(record);
        var status = Classify(written, after, record, change);
        var detail = $"The driver answered {written} to the write. " + (readProblem ?? $"A new session reads {Describe(after)}.");
        var kept = record with { Status = status, Detail = detail, After = after, RelatedAfter = relatedAfter };
        _store.WriteGpuChange(record.Id, kept.ToJson());

        // A change that did not take must not leave what the driver changed beside it (Chill) altered: put that back.
        if (status is GpuChangeStatus.Failed or GpuChangeStatus.NotConfirmed && relatedAfter is not null && RelatedDiffers(record.RelatedBefore, relatedAfter))
        {
            var restored = RestoreRelated(kept);
            kept = kept with { Detail = kept.Detail + " " + restored };
            _store.WriteGpuChange(record.Id, kept.ToJson());
        }

        return new(status, StatusWords(status, change, written), preview, kept);
    }

    private AmdChangeOutcome Finish(GpuChangeRecord record, GpuChangePreview preview)
    {
        _store.WriteGpuChange(record.Id, record.ToJson());
        return new(record.Status, record.Detail ?? string.Empty, preview, record);
    }

    private static GpuChangeStatus Classify(AdlxResult written, GpuFeatureState? after, GpuChangeRecord record, AmdChange change)
    {
        if (after is null)
        {
            return GpuChangeStatus.NotConfirmed;
        }

        if (written is not (AdlxResult.Ok or AdlxResult.AlreadyEnabled or AdlxResult.ResetNeeded))
        {
            return GpuChangeStatus.Failed;
        }

        var levelKept = change.Feature != GpuFeature.AntiLag || after.Level == record.Before.Level;
        if (after.Enabled == change.Enable && levelKept)
        {
            return GpuChangeStatus.Applied;
        }

        return written == AdlxResult.ResetNeeded ? GpuChangeStatus.RestartNeeded : GpuChangeStatus.NotConfirmed;
    }

    private static string StatusWords(GpuChangeStatus status, AmdChange change, AdlxResult written) => status switch
    {
        GpuChangeStatus.Applied => $"{change.Feature} is {(change.Enable ? "on" : "off")}: a new ADLX session reads it.",
        GpuChangeStatus.RestartNeeded => "The driver took the change but it needs a restart before it is in effect. Not confirmed.",
        GpuChangeStatus.NotConfirmed => $"The driver answered {written} but the independent reading does not show the requested state. Not reported as applied.",
        GpuChangeStatus.Failed => $"The driver refused the change ({written}).",
        _ => status.ToString(),
    };

    // ---- revert ----------------------------------------------------------------------------------------------------------------------------------------------

    public AmdChangeOutcome Revert(string recordId, bool apply)
    {
        var record = _store.ReadGpuChange(recordId) is { } json ? GpuChangeRecord.FromJson(json) : null;
        if (record is null)
        {
            return new(GpuChangeStatus.Failed, $"No readable GPU change '{recordId}'.", null, null);
        }

        if (record.Status is GpuChangeStatus.Reverted or GpuChangeStatus.NotWritten)
        {
            return new(record.Status, $"Nothing to undo: that change is {record.Status}.", null, record);
        }

        var entry = _entry();
        if (entry is null)
        {
            return new(GpuChangeStatus.Failed, "ADLX is not available on this PC (no AMD driver with ADLX).", null, record);
        }

        using (var session = AdlxSession.TryOpen(entry, out var failure, out var result))
        {
            if (session is null)
            {
                return new(GpuChangeStatus.Failed, $"ADLX would not start ({failure}, {result}).", null, record);
            }

            using var context = Context.Open(session, -1, record.GpuUniqueId, record.DisplayIndex, record.Feature == GpuFeature.FreeSync, out var error, record.DisplayName);
            if (context is null)
            {
                return new(GpuChangeStatus.Failed, error ?? "The GPU of that change was not found.", null, record);
            }

            var now = ReadTarget(context, record.Feature);
            var relatedNow = ReadRelated(context, record.Feature);
            var steps = new List<string>();
            if (now.Enabled is not null && now.Enabled != record.Before.Enabled && record.Before.Enabled is { } wasOn)
            {
                steps.Add($"turn {record.Feature} {(wasOn ? "on" : "off")}");
            }

            foreach (var (name, before) in record.RelatedBefore)
            {
                if (relatedNow.TryGetValue(name, out var current) && before.Enabled is { } was && current.Enabled is { } isNow && was != isNow)
                {
                    steps.Add($"put {name} back {(was ? "on" : "off")}");
                }
            }

            var plan = steps.Count == 0 ? "The state already equals the state from before; nothing to write." : "Would " + string.Join(", then ", steps) + ", and read it again in a new session.";
            if (!apply)
            {
                return new(GpuChangeStatus.NotWritten, plan, null, record);
            }

            if (steps.Count > 0)
            {
                if (now.Enabled is not null && now.Enabled != record.Before.Enabled && record.Before.Enabled is { } on)
                {
                    var toggle = TargetToggle(context, record.Feature);
                    if (toggle.Value is not null)
                    {
                        using (toggle.Value)
                        {
                            _ = SetEnabled(toggle.Value, SetEnabledSlot, on);
                        }
                    }
                }

                RestoreRelatedIn(context, record.RelatedBefore);
            }
        }

        var (after, relatedAfter, readProblem) = ReadFresh(record);
        var restored = after is not null && after.Enabled == record.Before.Enabled && (record.Feature != GpuFeature.AntiLag || after.Level == record.Before.Level)
            && relatedAfter is not null && !RelatedDiffers(record.RelatedBefore, relatedAfter);
        var status = restored ? GpuChangeStatus.Reverted : GpuChangeStatus.RevertIncomplete;
        var kept = record with
        {
            Status = status,
            Detail = (restored ? "Put back; a new session reads the state from before." : "Put back, but a new reading differs from the state before.") + (readProblem is null ? $" Now: {Describe(after)}." : $" {readProblem}"),
            After = after,
            RelatedAfter = relatedAfter,
        };
        _store.WriteGpuChange(record.Id, kept.ToJson());
        return new(status, kept.Detail ?? string.Empty, null, kept);
    }

    // ---- shared ----------------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The state in a NEW session: what the driver says now, independent of the session that wrote. The DLL is loaded again for it (<c>_entry()</c>): a session unloads the DLL when it ends, and
    /// initialising ADLX a second time on a DLL that stayed loaded crashed amdadlx64.dll on the real card.
    /// </summary>
    private (GpuFeatureState? Target, IReadOnlyDictionary<string, GpuFeatureState>? Related, string? Problem) ReadFresh(GpuChangeRecord record)
    {
        var entry = _entry();
        if (entry is null)
        {
            return (null, null, "ADLX could not be loaded again for the read-back.");
        }

        using var session = AdlxSession.TryOpen(entry, out var failure, out var result);
        if (session is null)
        {
            return (null, null, $"ADLX would not start again for the read-back ({failure}, {result}).");
        }

        using var context = Context.Open(session, -1, record.GpuUniqueId, record.DisplayIndex, record.Feature == GpuFeature.FreeSync, out var error, record.DisplayName);
        return context is null ? (null, null, $"The read-back could not open the GPU: {error}") : (ReadTarget(context, record.Feature), ReadRelated(context, record.Feature), null);
    }

    private string RestoreRelated(GpuChangeRecord record)
    {
        var entry = _entry();
        if (entry is null)
        {
            return "Chill and Boost could not be put back (ADLX could not be loaded again).";
        }

        using var session = AdlxSession.TryOpen(entry, out _, out _);
        if (session is null)
        {
            return "Chill and Boost could not be put back (ADLX would not start).";
        }

        using var context = Context.Open(session, -1, record.GpuUniqueId, record.DisplayIndex, record.Feature == GpuFeature.FreeSync, out _, record.DisplayName);
        if (context is null)
        {
            return "Chill and Boost could not be put back (the GPU was not found).";
        }

        RestoreRelatedIn(context, record.RelatedBefore);
        return "Chill and Boost, changed by the driver beside the switch, were put back.";
    }

    private static void RestoreRelatedIn(Context context, IReadOnlyDictionary<string, GpuFeatureState> before)
    {
        var current = ReadRelated(context, GpuFeature.AntiLag);
        foreach (var (name, state) in before)
        {
            if (state.Enabled is not { } was || !current.TryGetValue(name, out var now) || now.Enabled is not { } isNow || was == isNow)
            {
                continue;
            }

            var found = name == "Chill" ? context.S3d?.Chill(context.Gpu) : context.S3d?.Boost(context.Gpu);
            if (found?.Value is { } toggle)
            {
                using (toggle)
                {
                    _ = SetEnabled(toggle, name == "Chill" ? ChillSetEnabledSlot : BoostSetEnabledSlot, was);
                }
            }
        }
    }

    private static bool RelatedDiffers(IReadOnlyDictionary<string, GpuFeatureState> before, IReadOnlyDictionary<string, GpuFeatureState> after) =>
        before.Any(b => b.Value.Enabled is not null && after.TryGetValue(b.Key, out var a) && a.Enabled is not null && a.Enabled != b.Value.Enabled);

    private static string Describe(GpuFeatureState? state) => state is null
        ? "nothing"
        : state.Enabled is { } e ? $"{(e ? "on" : "off")}{(state.Level is { } l ? $", level {l.ToString(CultureInfo.InvariantCulture)}" : string.Empty)}" : "unknown";

    private static AdlxReading<AdlxToggle> TargetToggle(Context context, GpuFeature feature) => feature switch
    {
        GpuFeature.AntiLag => context.S3d is { } s ? s.AntiLag(context.Gpu) : new(AdlxResult.NotFound, null),
        GpuFeature.SmartAccessMemory => context.Tuning is { } t ? t.SmartAccessMemory(context.Gpu) : new(AdlxResult.NotFound, null),
        _ => context.DisplayServices is { } d && context.Display is { } display ? d.FreeSync(display) : new(AdlxResult.NotFound, null),
    };

    private static GpuFeatureState ReadTarget(Context context, GpuFeature feature) => StateOf(TargetToggle(context, feature), feature == GpuFeature.AntiLag);

    private static IReadOnlyDictionary<string, GpuFeatureState> ReadRelated(Context context, GpuFeature feature)
    {
        if (feature != GpuFeature.AntiLag || context.S3d is not { } s)
        {
            return new Dictionary<string, GpuFeatureState>();
        }

        return new Dictionary<string, GpuFeatureState>
        {
            ["Chill"] = StateOf(s.Chill(context.Gpu), false),
            ["Boost"] = StateOf(s.Boost(context.Gpu), false),
        };
    }

    /// <summary>A switch as the driver says it now: supported, on, and Anti-Lag's level. A failed answer is unknown, never "off".</summary>
    private static GpuFeatureState StateOf(AdlxReading<AdlxToggle> found, bool withLevel)
    {
        if (found.Value is null)
        {
            return new(null, null, null, $"not offered ({found.Result})");
        }

        using var toggle = found.Value;
        var supportedReading = toggle.IsSupported();
        var supported = supportedReading.Known();
        if (supported is not true)
        {
            return new(supported, null, null, supportedReading.Ok ? null : $"IsSupported: {supportedReading.Result}");
        }

        var enabled = toggle.IsEnabled();
        var note = enabled.Ok ? null : $"IsEnabled: {enabled.Result}";
        int? level = null;
        if (withLevel)
        {
            var reading = toggle.AntiLagLevel();
            if (reading.Ok)
            {
                level = reading.Value;
            }
            else
            {
                note = (note is null ? string.Empty : note + "; ") + (reading.Result == AdlxResult.UnknownInterface ? "no level interface (the original Anti-Lag only)" : $"level: {reading.Result}");
            }
        }

        return new(true, enabled.Known(), level, note);
    }

    /// <summary>The one ADLX setter call of Mesura: <c>SetEnabled(adlx_bool)</c> of a switch, in the slot given.</summary>
    private static unsafe AdlxResult SetEnabled(AdlxToggle toggle, int slot, bool enable) =>
        (AdlxResult)((delegate* unmanaged[Stdcall]<nint, byte, int>)AdlxSession.Slot(toggle.Pointer, slot))(toggle.Pointer, enable ? (byte)1 : (byte)0);

    /// <summary>The objects one change needs: the GPU, the services, and the display for FreeSync. Released in order.</summary>
    private sealed class Context : IDisposable
    {
        private readonly List<IDisposable> _owned = [];

        public required AdlxGpu Gpu { get; init; }

        public string GpuName { get; private init; } = "unknown GPU";

        public int? GpuId { get; private init; }

        public Adlx3DServices? S3d { get; private init; }

        public AdlxTuningServices? Tuning { get; private init; }

        public AdlxDisplayServices? DisplayServices { get; private init; }

        public AdlxDisplay? Display { get; private init; }

        public string? DisplayName { get; private init; }

        public static Context? Open(AdlxSession session, int gpuIndex, int? gpuUniqueId, int? displayIndex, bool needDisplay, out string? error, string? expectedDisplayName = null)
        {
            error = null;
            var system = new AdlxSystem(session);
            var listed = system.Gpus();
            if (listed.Value is null)
            {
                error = $"The GPU list could not be read ({listed.Result}).";
                return null;
            }

            using var list = listed.Value;
            AdlxGpu? chosen = null;
            for (uint i = 0; i < list.Size; i++)
            {
                var one = list.At(i);
                if (one.Value is null)
                {
                    continue;
                }

                var matches = gpuUniqueId is { } id ? one.Value.UniqueId() is { Ok: true } u && u.Value == id : (int)i == gpuIndex;
                if (matches && chosen is null)
                {
                    chosen = one.Value;
                }
                else
                {
                    one.Value.Dispose();
                }
            }

            if (chosen is null)
            {
                error = gpuUniqueId is null ? $"There is no GPU with index {gpuIndex}." : "The GPU of that change is not present any more.";
                return null;
            }

            var owned = new List<IDisposable> { chosen };
            var name = chosen.Name() is { Ok: true } n ? n.Value ?? "unknown GPU" : "unknown GPU";
            var id2 = chosen.UniqueId().Known();
            var s3d = system.Services3D().Value;
            var tuning = system.Tuning().Value;
            AdlxDisplayServices? displayServices = null;
            AdlxDisplay? display = null;
            string? displayName = null;
            if (needDisplay)
            {
                displayServices = system.Displays().Value;
                if (displayServices is not null && displayIndex is { } index)
                {
                    using var displays = displayServices.Displays().Value;
                    if (displays is not null && index >= 0 && index < displays.Size && displays.At((uint)index).Value is { } found)
                    {
                        display = found;
                        displayName = found.Name() is { Ok: true } dn ? dn.Value : null;
                        if (expectedDisplayName is not null && !string.Equals(displayName, expectedDisplayName, StringComparison.Ordinal))
                        {
                            found.Dispose();
                            display = null;
                            displayName = null;
                            error = "The display of that change is not at the same place any more (its name differs).";
                        }
                    }
                }
            }

            foreach (var o in new IDisposable?[] { s3d, tuning, displayServices, display })
            {
                if (o is not null)
                {
                    owned.Add(o);
                }
            }

            if (needDisplay && display is null)
            {
                foreach (var o in owned)
                {
                    o.Dispose();
                }

                error ??= "There is no display with that index.";
                return null;
            }

            return new Context { Gpu = chosen, GpuName = name, GpuId = id2, S3d = s3d, Tuning = tuning, DisplayServices = displayServices, Display = display, DisplayName = displayName }
                .Own(owned);
        }

        private Context Own(List<IDisposable> items)
        {
            _owned.AddRange(items);
            return this;
        }

        public void Dispose()
        {
            for (var i = _owned.Count - 1; i >= 0; i--)
            {
                _owned[i].Dispose();
            }

            _owned.Clear();
        }
    }
}
