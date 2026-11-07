namespace Mesura.Core.Windows;

/// <summary>Why an operation that tunes network adapters has nothing to do on this PC (0.7: told apart, so the words say what is true).</summary>
public enum AdapterGap
{
    /// <summary>Some adapter takes at least one of its settings.</summary>
    None,

    /// <summary>The PC has no physical adapter of the kind the operation is for (no Ethernet port, no Wi-Fi card).</summary>
    NoAdapter,

    /// <summary>There is a physical adapter of the right kind, but from another maker than the one the operation is for.</summary>
    OtherMaker,

    /// <summary>An adapter of the right kind and maker is there, but its driver offers none of the settings with the wanted values: the hardware or driver says no.</summary>
    DriverOffersNone,

    /// <summary>The adapters could not be read: nothing can be said.</summary>
    Unreadable,
}

/// <summary>The answer of <see cref="AdapterAvailability.Explain"/>: the gap, and what it was found against, for the words.</summary>
/// <param name="Kinds">The kinds of adapter the operation is for (ethernet, Wi-Fi).</param>
/// <param name="WantedMakers">The makers it is for; empty when it is for any maker.</param>
/// <param name="FoundMakers">The makers of the physical adapters of the right kind that were found (for <see cref="AdapterGap.OtherMaker"/> and <see cref="AdapterGap.DriverOffersNone"/>).</param>
public sealed record AdapterGapInfo(AdapterGap Gap, IReadOnlyList<AdapterKind> Kinds, IReadOnlyList<string> WantedMakers, IReadOnlyList<string> FoundMakers)
{
    /// <summary>The kind in a word for a sentence: <c>ethernet</c>, <c>Wi-Fi</c>, or <c>ethernet/Wi-Fi</c>.</summary>
    public string KindWord => string.Join("/", Kinds.Select(k => k == AdapterKind.WiFi ? "Wi-Fi" : k.ToString().ToLowerInvariant()));

    /// <summary>The kind as a code token: <c>ethernet</c>, <c>wifi</c>, or <c>any</c> when the operation is for more than one kind.</summary>
    public string KindToken => Kinds.Count == 1 ? (Kinds[0] == AdapterKind.WiFi ? "wifi" : Kinds[0].ToString().ToLowerInvariant()) : "any";
}

/// <summary>
/// Tells the ways an adapter operation can be "not here" apart (D050): no adapter of that kind at all, an adapter of another maker, a driver that offers none of the
/// settings, or an unreadable adapter list. Each is a different true statement; none of them is an error, and a driver that says no is not "absent".
/// </summary>
public static class AdapterAvailability
{
    public static AdapterGapInfo Explain(WindowsOperation op, IReadOnlyList<AdapterReading>? adapters)
    {
        ArgumentNullException.ThrowIfNull(op);
        var kinds = op.AdapterSettings.SelectMany(s => s.Kinds).Distinct().ToList();
        var wanted = op.AdapterSettings.SelectMany(s => s.Vendors).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (adapters is null)
        {
            return new AdapterGapInfo(AdapterGap.Unreadable, kinds, wanted, []);
        }

        var matching = adapters.Where(a => op.AdapterSettings.Any(s => s.Matches(a))).ToList();
        if (matching.Any(a => op.AdapterSettings.Any(s => s.Matches(a) && s.Wanted(a) is not null)))
        {
            return new AdapterGapInfo(AdapterGap.None, kinds, wanted, [.. Makers(matching)]);
        }

        if (matching.Count > 0)
        {
            return new AdapterGapInfo(AdapterGap.DriverOffersNone, kinds, wanted, [.. Makers(matching)]);
        }

        var sameKind = adapters.Where(a => a.Physical && kinds.Contains(a.Kind)).ToList();
        return sameKind.Count > 0 && wanted.Count > 0
            ? new AdapterGapInfo(AdapterGap.OtherMaker, kinds, wanted, [.. Makers(sameKind)])
            : new AdapterGapInfo(AdapterGap.NoAdapter, kinds, wanted, []);
    }

    private static IEnumerable<string> Makers(IEnumerable<AdapterReading> adapters) =>
        adapters.Select(a => !string.IsNullOrWhiteSpace(a.Vendor) ? a.Vendor : a.Description).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>The English sentence for the details and the log (the engine's own words; the interface has its translated short forms).</summary>
    public static string Sentence(AdapterGapInfo info) => info.Gap switch
    {
        AdapterGap.NoAdapter => $"This PC has no physical {info.KindWord} network adapter" + (info.WantedMakers.Count > 0 ? $" (this one is for {string.Join("/", info.WantedMakers)})." : "."),
        AdapterGap.OtherMaker => $"This PC's {info.KindWord} adapter is from {string.Join("/", info.FoundMakers)}, not {string.Join("/", info.WantedMakers)}: this one is only for {string.Join("/", info.WantedMakers)} adapters.",
        AdapterGap.DriverOffersNone => $"The driver of the {info.KindWord} adapter{(info.FoundMakers.Count > 0 ? $" ({string.Join("/", info.FoundMakers)})" : "")} offers none of these settings with the wanted values, so nothing is written to it.",
        _ => "The network adapters couldn't be read.",
    };
}
