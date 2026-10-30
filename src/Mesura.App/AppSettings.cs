using System.IO;
using System.Text.Json;
using Mesura.Setup.Windows;

namespace Mesura.App;

/// <summary>
/// A profile Profiles remembers: where its file is, its name, what it holds (catalog app ids, how many apps' settings
/// and Windows settings, its outcome presets, whether it carries an NVIDIA configuration), when it was last saved,
/// opened or restored here, and how its last restore here ended (<c>Succeeded</c>, <c>Incomplete</c>,
/// <c>NeedsAttention</c>) with that run's id. Nothing in it is a setting's value.
/// </summary>
public sealed record RecentProfile(
    string Path,
    string Name,
    string LastUsedUtc,
    string Action,
    List<string>? Apps = null,
    int SettingsApps = 0,
    int WindowsSettings = 0,
    List<string>? Presets = null,
    bool Nvidia = false,
    string? LastRunId = null,
    string? LastStatus = null);

/// <summary>
/// Mesura's own preferences, kept in <c>%LOCALAPPDATA%\Mesura\Setup\app-settings.json</c> through the setup store (its
/// one file writer). A missing or unreadable file means the defaults; nothing here is about the PC itself.
/// </summary>
public sealed class AppSettings
{
    /// <summary>The interface language (<c>en</c>, <c>ru</c>); <c>null</c> follows Windows' display language.</summary>
    public string? Language { get; set; }

    /// <summary>How much interactive light the window draws: <c>full</c>, <c>reduced</c>, <c>off</c>; <c>null</c> decides by this PC (<see cref="LightField.Resolve"/>).</summary>
    public string? Lighting { get; set; }

    /// <summary>
    /// What Mesura does at start about administrator rights: <c>ask</c> (a choice each time: continue as you are, or restart as administrator),
    /// <c>normal</c> (never ask, run as you are) or <c>admin</c> (restart as administrator without asking). <c>null</c> is <c>ask</c>, or <c>normal</c>
    /// for a user who had turned the older <see cref="ElevateAtStart"/> switch off. It never makes an install in a user-writable folder elevate.
    /// </summary>
    public string? StartupMode { get; set; }

    /// <summary>The 0.1-0.3 switch for the prompt at start; kept so a saved choice carries over, replaced by <see cref="StartupMode"/>.</summary>
    public bool ElevateAtStart { get; set; } = true;

    /// <summary>The start-up mode in effect: the chosen one, else what the older switch implies.</summary>
    public string EffectiveStartupMode => StartupMode is "ask" or "normal" or "admin" ? StartupMode : ElevateAtStart ? "ask" : "normal";

    /// <summary>Whether Mesura makes a Windows restore point before it changes settings for the whole PC (when it runs as administrator).</summary>
    public bool RestorePointBeforeChanges { get; set; } = true;

    /// <summary>Where updates come from: <c>stable</c> (releases) or <c>preview</c> (release candidates, earlier and rougher).</summary>
    public string UpdateChannel { get; set; } = "stable";

    /// <summary>Whether Mesura asks the update server for news when it starts (never downloads or installs without a click).</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>A version the user chose to skip: not offered again; a newer one is.</summary>
    public string? SkippedUpdate { get; set; }

    /// <summary>"Remind me later": no update is offered before this time (UTC, ISO 8601).</summary>
    public string? UpdateRemindAfterUtc { get; set; }

    /// <summary>The highest update manifest sequence accepted so far: an older manifest is a replay and is refused.</summary>
    public long UpdateHighestSequence { get; set; }

    /// <summary>When the update server was last reached (UTC, ISO 8601); shown on Settings.</summary>
    public string? LastUpdateCheckUtc { get; set; }

    /// <summary>The user's saved configuration bundle (a file they chose), restored after installs and on Configurations.</summary>
    public string? SavedConfiguration { get; set; }

    /// <summary>The outcome last applied on Optimize (a primary preset id), which a saved profile records.</summary>
    public string? Outcome { get; set; }

    /// <summary>The install folder the user chose per app (catalog id: folder), for apps whose installer takes one; an app not listed installs where its installer decides.</summary>
    public Dictionary<string, string>? InstallFolders { get; set; }

    /// <summary>How apps are set up after an install: <c>defaults</c>, <c>saved</c> or <c>none</c>.</summary>
    public string ConfigureAfterInstall { get; set; } = "defaults";

    /// <summary>
    /// The NVIDIA driver configuration (D032: components, NVCleanstall tweaks, interrupt settings, the NVIDIA profile to
    /// import); <c>null</c> until changed, which means the default configuration (the owner's).
    /// </summary>
    public Mesura.Core.Nvidia.NvidiaDriverConfig? NvidiaConfig { get; set; }

    /// <summary>The Visual C++ runtimes the user ticked or unticked against the defaults (id: included).</summary>
    public Dictionary<string, bool>? RuntimeChoices { get; set; }

    /// <summary>The setup plan's cards the user switched off (<c>windows</c>, <c>apps</c>, <c>runtimes</c>, <c>nvidia</c>).</summary>
    public List<string>? PlanSectionsOff { get; set; }

    /// <summary>Profiles saved or opened here, newest first (at most <see cref="RecentLimit"/>): what Profiles lists.</summary>
    public List<RecentProfile>? RecentProfiles { get; set; }

    public const int RecentLimit = 6;

    /// <summary>Puts a profile first in <see cref="RecentProfiles"/>, replacing an older entry for the same file.</summary>
    public void Remember(RecentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var list = RecentProfiles ?? [];
        list.RemoveAll(p => string.Equals(p.Path, profile.Path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, profile);
        RecentProfiles = [.. list.Take(RecentLimit)];
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static AppSettings Load(SetupStore store)
    {
        try
        {
            return store.ReadAppSettings() is { } json ? JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings() : new AppSettings();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    /// <returns>What went wrong, or <c>null</c> when saved.</returns>
    public string? Save(SetupStore store)
    {
        try
        {
            store.WriteAppSettings(JsonSerializer.Serialize(this, Options));
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }
}
