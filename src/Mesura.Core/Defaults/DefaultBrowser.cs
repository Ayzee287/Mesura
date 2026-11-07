using Mesura.Core.Windows;

namespace Mesura.Core.Defaults;

/// <summary>A browser Mesura recognises by the program identifier Windows keeps for it.</summary>
/// <param name="Id">The catalog id of the app (<c>brave</c>); empty for a browser that is not in the Apps list (Edge).</param>
/// <param name="ProgIdPrefix">What the browser's <c>ProgId</c> starts with (<c>BraveHTML</c>; Firefox adds a hash, <c>FirefoxURL-308046B0AF4A39CB</c>).</param>
/// <param name="SettingsName">The name Windows lists the app under on its Default apps page.</param>
public sealed record BrowserChoice(string Id, string Name, string ProgIdPrefix, string SettingsName)
{
    /// <summary>The page of Settings for this app (<c>ms-settings:defaultapps?registeredAppUser=…</c>): the user makes the choice there; Windows protects it from programs.</summary>
    public string SettingsUri => "ms-settings:defaultapps?registeredAppUser=" + Uri.EscapeDataString(SettingsName);
}

/// <summary>Which browser opens web links for the signed-in user, as Windows records it.</summary>
/// <param name="ProgId">The program identifier Windows keeps for <c>https</c> links, or <c>null</c> when the user never chose (Windows then uses its own default, Edge).</param>
/// <param name="Browser">The recognised browser; <c>null</c> for a program Mesura does not know.</param>
/// <param name="Chosen">Whether the user (or an installer, through Settings) chose it, as opposed to Windows' own default.</param>
public sealed record DefaultBrowserState(string? ProgId, BrowserChoice? Browser, bool Chosen)
{
    public bool Is(string id) => Browser is { } b && b.Id.Equals(id, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The default browser (0.7). Windows protects the choice from programs (the per-user choice carries a hash only Windows' own pages produce), so Mesura does what is honest: it reads the
/// choice, shows it, opens the exact page of Settings for the app the user wants, and reads the choice again afterwards. It never writes the choice and never forges the hash.
/// </summary>
public static class DefaultBrowsers
{
    public const string UserChoiceKey = @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice";

    public static BrowserChoice Edge { get; } = new("", "Microsoft Edge", "MSEdgeHTM", "Microsoft Edge");

    public static IReadOnlyList<BrowserChoice> Known { get; } =
    [
        Edge,
        new("brave", "Brave", "BraveHTML", "Brave"),
        new("chrome", "Google Chrome", "ChromeHTML", "Google Chrome"),
        new("firefox", "Firefox", "FirefoxURL", "Firefox"),
    ];

    /// <summary>The browser a program identifier belongs to, or <c>null</c>.</summary>
    public static BrowserChoice? ByProgId(string? progId) =>
        string.IsNullOrWhiteSpace(progId) ? null : Known.FirstOrDefault(k => progId.StartsWith(k.ProgIdPrefix, StringComparison.OrdinalIgnoreCase));

    public static BrowserChoice? ByAppId(string appId) => Known.FirstOrDefault(k => k.Id.Length > 0 && k.Id.Equals(appId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads the choice for the signed-in user. Unknown user or an unreadable key reads as "not chosen": nothing is claimed.</summary>
    public static DefaultBrowserState Read(ISystemReader reader)
    {
        var reading = reader.ReadRegistry("HKCU", UserChoiceKey, "ProgId");
        if (!reading.ValueExists || string.IsNullOrWhiteSpace(reading.Value))
        {
            return new DefaultBrowserState(null, Edge, Chosen: false);
        }

        return new DefaultBrowserState(reading.Value, ByProgId(reading.Value), Chosen: true);
    }
}
