namespace Mesura.Core.Config;

/// <summary>
/// Mesura's own starting configuration for a new installation (redesign 2026-10 §5): a few clutter- and
/// promotion-reducing values from the existing allowlists, as an ordinary bundle, so it is imported, previewed, backed up
/// and recorded exactly like a saved configuration. Every value's form was read from the owner's own installation
/// (PC-000, 2026-10-23): Brave's are JSON booleans in <c>Default\Preferences</c>, Spotify's a quoted word in
/// <c>prefs</c>. Apps without an entry here (Steam, Discord, Vencord) have no Mesura defaults: their allowlisted
/// settings are personal choices, so only a saved configuration restores them.
/// </summary>
public static class ConfigDefaults
{
    private static readonly IReadOnlyDictionary<string, ConfigBundleFile.AppEntry> Apps = new Dictionary<string, ConfigBundleFile.AppEntry>(StringComparer.Ordinal)
    {
        ["brave"] = new("Brave", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["brave.ads.sponsored"] = "false",
            ["brave.ntp.showRewards"] = "false",
            ["brave.ntp.showNews"] = "false",
            ["brave.ntp.showVpn"] = "false",
            ["brave.vpn.showButton"] = "false",
            ["brave.rewards.locationBarButton"] = "false",
            ["brave.wallet.toolbarIcon"] = "false",
            ["brave.today.toolbarButton"] = "false",
            ["brave.aichat.toolbarButton"] = "false",
        }),
        ["spotify"] = new("Spotify", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["spotify.autostartMode"] = "\"off\"",
        }),
    };

    /// <summary>Whether Mesura has defaults for this config port.</summary>
    public static bool Has(string portId) => Apps.ContainsKey(portId);

    /// <summary>The defaults as a bundle, stamped with <paramref name="createdUtc"/> (Core reads no clock).</summary>
    public static ConfigBundleFile Bundle(string createdUtc) =>
        new(ConfigBundleFile.FileKind, ConfigBundleFile.CurrentSchema, createdUtc, Apps);

    /// <summary>The setting ids Mesura's defaults set for a port, for the preview (empty when it has none).</summary>
    public static IReadOnlyCollection<string> SettingIds(string portId) =>
        Apps.TryGetValue(portId, out var entry) ? [.. entry.Values.Keys] : [];
}
