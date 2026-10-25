namespace Mesura.Core.Config;

/// <summary>
/// The transferable-settings allowlists for the supported applications, as data. Every path here was read from a real
/// installation on the reference machine (keys only); values are never baked in. Adding or adjusting a setting is an
/// edit to this list, not a code change. Each port also declares the categories it deliberately excludes, so the
/// export preview can show them.
/// </summary>
public static class ConfigCatalog
{
    public static IReadOnlyList<ConfigPortDefinition> All { get; } = [Steam(), Brave(), Vencord(), Discord(), Spotify()];

    public static ConfigPortDefinition? ById(string id) =>
        All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private static ConfigSetting App(string id, string name, string path, TransferConfidence c, string note, bool subtree = false, string source = "main") =>
        new(id, name, path, ConfigScope.Application, c, note, subtree, source);

    private static ConfigSetting Acct(string id, string name, string path, TransferConfidence c, string note) =>
        new(id, name, path, ConfigScope.Account, c, note, Source: "account");

    // ---- Steam -----------------------------------------------------------------------------------------------------
    // config.vdf is per-install (Application); localconfig.vdf is per-account (Account), written only once the account
    // folder exists and Steam is closed. Library-folder paths are never carried (drive-specific); login/auth excluded.
    private static ConfigPortDefinition Steam() => new(
        "steam", "Steam",
        [
            new("install", Format.Vdf, "Per-install client config (config/config.vdf)."),
            new("account", Format.Vdf, "Per-account local config (userdata/<id>/config/localconfig.vdf)."),
        ],
        [
            App("steam.autoUpdateWindow", "Auto-update window enabled",
                "InstallConfigStore/Software/Valve/Steam/AutoUpdateWindowEnabled", TransferConfidence.Reliable,
                "Whether downloads are limited to the configured auto-update window.", source: "install"),
            App("steam.shaderCacheDisabled", "Shader pre-caching disabled",
                "InstallConfigStore/Software/Valve/Steam/ShaderCacheManager/DisableShaderCache", TransferConfidence.BestEffort,
                "Vulkan/GL shader pre-cache toggle (per install).", source: "install"),
            App("steam.musicVolume", "Music player volume",
                "InstallConfigStore/Music/MusicVolume", TransferConfidence.BestEffort, "Steam Music volume.", source: "install"),

            Acct("steam.overlay.enabled", "In-game overlay enabled",
                "UserLocalConfigStore/system/EnableGameOverlay", TransferConfidence.Reliable, "Steam overlay on/off."),
            Acct("steam.overlay.restoreTabs", "Overlay restores browser tabs",
                "UserLocalConfigStore/system/InGameOverlayRestoreBrowserTabs", TransferConfidence.BestEffort, "Reopen overlay browser tabs."),
            Acct("steam.overlay.screenshotNotification", "Screenshot notification",
                "UserLocalConfigStore/system/InGameOverlayScreenshotNotification", TransferConfidence.BestEffort, "Show a notification on screenshot."),
            Acct("steam.overlay.screenshotSound", "Screenshot sound",
                "UserLocalConfigStore/system/InGameOverlayScreenshotPlaySound", TransferConfidence.BestEffort, "Play a sound on screenshot."),
            Acct("steam.overlay.pushToTalkKey", "Overlay push-to-talk key",
                "UserLocalConfigStore/system/PushToTalkKey", TransferConfidence.BestEffort, "Overlay push-to-talk binding (a key name, not a credential)."),
            Acct("steam.library.lowBandwidth", "Library low-bandwidth mode",
                "UserLocalConfigStore/LibraryLowBandwidthMode", TransferConfidence.Reliable, "Reduce library network use."),
            Acct("steam.library.lowPerf", "Library low-performance mode",
                "UserLocalConfigStore/LibraryLowPerfMode", TransferConfidence.Reliable, "Reduce library animations/effects."),
            Acct("steam.library.noCommunityContent", "Library community content off",
                "UserLocalConfigStore/LibraryDisableCommunityContent", TransferConfidence.Reliable, "Hide community content in the library."),
            Acct("steam.news.notifyAvailableGames", "Notify about available games",
                "UserLocalConfigStore/news/NotifyAvailableGames", TransferConfidence.BestEffort, "Game-availability notifications."),
            Acct("steam.streaming.enabled", "Remote Play streaming enabled",
                "UserLocalConfigStore/streaming_v2/EnableStreaming", TransferConfidence.BestEffort, "Remote Play on/off (no device names carried)."),
            Acct("steam.streaming.p2pScope", "Remote Play network scope",
                "UserLocalConfigStore/streaming_v2/P2PScopeV2", TransferConfidence.BestEffort, "Remote Play discovery scope."),
        ],
        [
            new("Login & accounts", "loginusers.vdf, config.vdf Accounts, SharedAuth, Offline/CloudKey tickets — login and authentication state."),
            new("Steam Guard / sessions", "Remembered-login and Steam Guard data — never read; you sign in manually."),
            new("Friends & family", "Friends list, family group, player caches — personal and account-specific."),
            new("Library folder paths", "Drive-specific paths; re-added on the destination via Steam's Storage Manager."),
            new("Recent & played games", "Play history, app tickets, server browser history — personal activity."),
            new("Connection state", "CM WebSocket servers, cell IDs, download-rate history — machine/session state."),
        ]);

    // ---- Brave -----------------------------------------------------------------------------------------------------
    // Default/Preferences (JSON). A curated set of portable UI/behaviour prefs; the whole profile is never copied.
    private static ConfigPortDefinition Brave() => new(
        "brave", "Brave",
        [ new("main", Format.Json, "Default/Preferences (Chromium JSON).") ],
        [
            App("brave.ntp.showClock", "New tab: show clock", "/brave/new_tab_page/show_clock", TransferConfidence.BestEffort, "New-tab clock."),
            App("brave.ntp.clockFormat", "New tab: clock format", "/brave/new_tab_page/clock_format", TransferConfidence.BestEffort, "12/24-hour clock."),
            App("brave.ntp.showStats", "New tab: show stats", "/brave/new_tab_page/show_stats", TransferConfidence.BestEffort, "Shields stats card."),
            App("brave.ntp.showNews", "New tab: show Brave News", "/brave/new_tab_page/show_brave_news", TransferConfidence.BestEffort, "News on new tab."),
            App("brave.ntp.showRewards", "New tab: show Rewards", "/brave/new_tab_page/show_rewards", TransferConfidence.BestEffort, "Rewards widget."),
            App("brave.ntp.showVpn", "New tab: show VPN", "/brave/new_tab_page/show_brave_vpn", TransferConfidence.BestEffort, "VPN widget."),
            App("brave.ntp.showOptions", "New tab: layout options", "/brave/new_tab_page/shows_options", TransferConfidence.BestEffort, "New-tab layout selection."),
            App("brave.ntp.backgroundType", "New tab: background type", "/brave/new_tab_page/background/type", TransferConfidence.BestEffort, "Background kind."),
            App("brave.ntp.backgroundRandom", "New tab: random background", "/brave/new_tab_page/background/random", TransferConfidence.BestEffort, "Rotate backgrounds."),
            App("brave.aichat.toolbarButton", "Leo: toolbar button", "/brave/ai_chat/show_toolbar_button", TransferConfidence.BestEffort, "Show Leo button."),
            App("brave.aichat.contextMenu", "Leo: context menu", "/brave/ai_chat/context_menu_enabled", TransferConfidence.BestEffort, "Leo in context menu."),
            App("brave.aichat.storage", "Leo: history storage", "/brave/ai_chat/storage_enabled", TransferConfidence.BestEffort, "Keep Leo history."),
            App("brave.ads.sponsored", "Sponsored new-tab images", "/brave/brave_ads/sponsored/enabled", TransferConfidence.BestEffort, "Sponsored backgrounds."),
            App("brave.vpn.showButton", "VPN toolbar button", "/brave/brave_vpn/show_button", TransferConfidence.BestEffort, "VPN button in toolbar."),
            App("brave.rewards.locationBarButton", "Rewards button in address bar", "/brave/rewards/show_brave_rewards_button_in_location_bar", TransferConfidence.BestEffort, "Rewards button."),
            App("brave.today.optedIn", "Brave News opted in", "/brave/today/opted_in", TransferConfidence.BestEffort, "News opt-in."),
            App("brave.today.toolbarButton", "Brave News toolbar button", "/brave/today/should_show_toolbar_button", TransferConfidence.BestEffort, "News toolbar button."),
            App("brave.shields.advancedView", "Shields advanced view", "/brave/shields/advanced_view_enabled", TransferConfidence.BestEffort, "Default Shields panel view."),
            App("brave.shields.statsBadge", "Shields stats badge", "/brave/shields/stats_badge_visible", TransferConfidence.BestEffort, "Blocked-count badge."),
            App("brave.speedreader", "Speedreader enabled", "/brave/speedreader/feature_enabled", TransferConfidence.BestEffort, "Speedreader."),
            App("brave.waybackMachine", "Wayback Machine prompt", "/brave/wayback_machine_enabled", TransferConfidence.BestEffort, "404 Wayback prompt."),
            App("brave.wallet.toolbarIcon", "Wallet toolbar icon", "/brave/wallet/show_wallet_icon_on_toolbar", TransferConfidence.BestEffort, "Wallet icon."),
            App("brave.search.ntpSearch", "New-tab search bar", "/brave/brave_search/show-ntp-search", TransferConfidence.BestEffort, "Search box on new tab."),
            App("brave.closeLastTab", "Allow closing last tab", "/brave/enable_closing_last_tab", TransferConfidence.BestEffort, "Close-last-tab behaviour."),
            App("brave.confirmWindowClose", "Confirm before closing window", "/brave/enable_window_closing_confirm", TransferConfidence.BestEffort, "Warn on multi-tab close."),
            App("brave.spellcheck", "Spell checking", "/browser/enable_spellchecking", TransferConfidence.Reliable, "Browser spell check."),
            App("brave.pinPwaButton", "Pin PWA install button", "/browser/pin_pwa_install_button", TransferConfidence.BestEffort, "PWA install button."),
            App("brave.localTheme", "Saved local theme", "/browser/theme/saved_local_theme", TransferConfidence.BestEffort, "Chosen theme id."),
            App("brave.acceptLanguages", "Accept-Languages", "/intl/accept_languages", TransferConfidence.Reliable, "Preferred languages sent to sites."),
            App("brave.selectedLanguages", "UI languages", "/intl/selected_languages", TransferConfidence.Reliable, "Chosen UI languages."),
            App("brave.translate", "Offer to translate", "/translate/enabled", TransferConfidence.Reliable, "Translate prompts."),
        ],
        [
            new("Passwords & autofill", "password_manager, autofill form data, account_values — never read."),
            new("Sign-in & sync", "signin, sync, gaia_cookie, enterprise_profile_guid — account identity and sync tokens."),
            new("Cookies & history", "Separate profile databases (Cookies, History, Login Data) are never touched."),
            new("Window geometry", "window_placement — tied to this machine's monitors, not a preference worth carrying."),
            new("Activity & telemetry", "stats, rewards captcha state, in-product-help counters, news channel history — personal/transient."),
            new("Extensions", "No extensions are registered in this profile; extension state is not transferred."),
        ]);

    // ---- Vencord ---------------------------------------------------------------------------------------------------
    // settings.json (JSON). Behaviour/UI prefs, theme links, and the whole plugins object (the key setting). The cloud
    // sync section is excluded so the destination doesn't inherit an "authenticated" flag. quickCss.css and theme
    // files are copied as whole files by the Platform port (noted below), not via this JSON allowlist.
    private static ConfigPortDefinition Vencord() => new(
        "vencord", "Vencord (Discord mod)",
        [ new("main", Format.Json, "settings/settings.json.") ],
        [
            App("vencord.plugins", "Plugins and their settings", "/plugins", TransferConfidence.Reliable,
                "Every enabled plugin and its configuration, as one block.", subtree: true),
            App("vencord.enabledThemes", "Enabled themes", "/enabledThemes", TransferConfidence.Reliable, "Selected local themes (files copied separately)."),
            App("vencord.themeLinks", "Online theme links", "/themeLinks", TransferConfidence.Reliable, "Remote theme URLs."),
            App("vencord.useQuickCss", "Use QuickCSS", "/useQuickCss", TransferConfidence.Reliable, "Enable QuickCSS."),
            App("vencord.notifyUpdates", "Notify about updates", "/notifyAboutUpdates", TransferConfidence.Reliable, "Update notices."),
            App("vencord.autoUpdate", "Auto-update", "/autoUpdate", TransferConfidence.Reliable, "Auto-update Vencord."),
            App("vencord.autoUpdateNotification", "Auto-update notification", "/autoUpdateNotification", TransferConfidence.BestEffort, "Notify on auto-update."),
            App("vencord.reactDevtools", "Enable React DevTools", "/enableReactDevtools", TransferConfidence.BestEffort, "Developer setting."),
            App("vencord.frameless", "Frameless window", "/frameless", TransferConfidence.BestEffort, "Frameless client window."),
            App("vencord.transparent", "Transparent window", "/transparent", TransferConfidence.BestEffort, "Window transparency."),
            App("vencord.winCtrlQ", "Ctrl+Q to quit", "/winCtrlQ", TransferConfidence.BestEffort, "Quit shortcut."),
            App("vencord.disableMinSize", "Disable minimum window size", "/disableMinSize", TransferConfidence.BestEffort, "Allow tiny window."),
            App("vencord.winNativeTitleBar", "Native title bar", "/winNativeTitleBar", TransferConfidence.BestEffort, "Use Windows title bar."),
            App("vencord.nativeTitleBar", "Native title bar (alt)", "/nativeTitleBar", TransferConfidence.BestEffort, "Native title bar flag."),
            App("vencord.eagerPatches", "Eager patches", "/eagerPatches", TransferConfidence.BestEffort, "Apply patches eagerly."),
            App("vencord.windowsMaterial", "Window material", "/windowsMaterial", TransferConfidence.BestEffort, "Mica/acrylic backdrop."),
            App("vencord.notifications", "Notification settings", "/notifications", TransferConfidence.BestEffort, "Toast position, timeout, native/custom.", subtree: true),
        ],
        [
            new("Cloud sync session", "cloud/authenticated and cloud/url — a destination must link cloud sync itself; the session flag is not carried."),
            new("Plugin secrets", "API keys, session tokens, passwords and webhooks a plugin stores (Translate's Kagi session or DeepL key, a Last.fm key) — never carried; a destination keeps its own."),
            new("Discord account", "Discord's own login, token and local storage live outside Vencord and are never read."),
        ]);

    // ---- Discord (client) ------------------------------------------------------------------------------------------
    // %APPDATA%\discord\settings.json (JSON) holds desktop client options only; account data lives in Local Storage etc.
    private static ConfigPortDefinition Discord() => new(
        "discord", "Discord",
        [ new("main", Format.Json, "discord/settings.json (desktop client options).") ],
        [
            App("discord.audioSubsystem", "Audio subsystem", "/audioSubsystem", TransferConfidence.Reliable, "Standard/legacy audio engine."),
            App("discord.minimizeToTray", "Minimize to tray", "/MINIMIZE_TO_TRAY", TransferConfidence.Reliable, "Keep running in the tray."),
            App("discord.offloadAdmControls", "Offload audio controls", "/offloadAdmControls", TransferConfidence.BestEffort, "Audio control offload."),
            App("discord.backgroundColor", "Splash background colour", "/BACKGROUND_COLOR", TransferConfidence.BestEffort, "Loading-splash colour."),
            App("discord.chromiumSwitches", "Chromium switches", "/chromiumSwitches", TransferConfidence.BestEffort, "Extra Chromium flags.", subtree: true),
        ],
        [
            new("Window geometry", "WINDOW_BOUNDS, IS_MAXIMIZED, IS_MINIMIZED — machine-specific."),
            new("Developer flag", "DANGEROUS_ENABLE_DEVTOOLS — left at its safe default on the destination."),
            new("Account & session", "Login token, Local Storage, IndexedDB, Session Storage — never read."),
        ]);

    // ---- Spotify ---------------------------------------------------------------------------------------------------
    // %APPDATA%\Spotify\prefs (key=value). Most Spotify settings are account-side; only safe client prefs are carried.
    private static ConfigPortDefinition Spotify() => new(
        "spotify", "Spotify",
        [ new("main", Format.Prefs, "prefs (key=value).") ],
        [
            App("spotify.language", "Display language", "language", TransferConfidence.Reliable, "Client UI language."),
            App("spotify.autostartMode", "Autostart mode", "app.autostart-mode", TransferConfidence.BestEffort, "Launch-at-login behaviour."),
        ],
        [
            new("Login & credentials", "autologin.username, saved_credentials, blob, canonical_username — sign-in state, never read."),
            new("Machine/session state", "storage.last-location (a drive path), core.clock_delta, launched-version — machine-specific."),
        ]);
}
