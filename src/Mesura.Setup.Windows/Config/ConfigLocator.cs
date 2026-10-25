using Mesura.Core.Config;
using Microsoft.Win32;

namespace Mesura.Setup.Windows.Config;

/// <summary>One config file a port uses, resolved to a real path on this machine.</summary>
/// <param name="SourceId">The <see cref="ConfigSource.Id"/> it fills.</param>
/// <param name="Format">Its on-disk format.</param>
/// <param name="Path">The resolved full path (may not exist).</param>
/// <param name="Exists">Whether the file is present.</param>
public sealed record LocatedConfigFile(string SourceId, Format Format, string Path, bool Exists);

/// <summary>
/// Finds each supported application's configuration files on this machine. It reads only standard locations and the
/// documented Steam path from the registry; it never guesses drive layouts or assumes a Windows user name, so the
/// paths it returns suit whatever machine it runs on. Finding a file is read-only.
/// </summary>
public static class ConfigLocator
{
    /// <summary>Steam's install path from <c>HKCU\Software\Valve\Steam\SteamPath</c>, or <c>null</c> if Steam is not installed.</summary>
    public static string? SteamPath
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var path = key?.GetValue("SteamPath") as string;
            return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        }
    }

    private static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>Resolves every source file of a port. Absent files are returned with <see cref="LocatedConfigFile.Exists"/> false.</summary>
    public static IReadOnlyList<LocatedConfigFile> Locate(ConfigPortDefinition port)
    {
        ArgumentNullException.ThrowIfNull(port);
        return port.Id switch
        {
            "steam" => Steam(),
            "brave" => One(port, LocalAppData, "BraveSoftware", "Brave-Browser", "User Data", "Default", "Preferences"),
            "vencord" => One(port, AppData, "Vencord", "settings", "settings.json"),
            "discord" => One(port, AppData, "discord", "settings.json"),
            "spotify" => One(port, AppData, "Spotify", "prefs"),
            _ => [],
        };
    }

    /// <summary>The path a located source should be written back to on import, or <c>null</c> if it can't be placed.</summary>
    public static string? ResolveForWrite(ConfigPortDefinition port, string sourceId) =>
        Locate(port).FirstOrDefault(f => f.SourceId == sourceId)?.Path;

    private static LocatedConfigFile[] One(ConfigPortDefinition port, params string[] parts)
    {
        var format = port.Sources[0].Format;
        var path = Path.Combine(parts);
        return [new LocatedConfigFile(port.Sources[0].Id, format, path, File.Exists(path))];
    }

    private static LocatedConfigFile[] Steam()
    {
        var root = SteamPath;
        var installPath = root is null ? "" : Path.Combine(root, "config", "config.vdf");
        var install = new LocatedConfigFile("install", Format.Vdf, installPath, root is not null && File.Exists(installPath));
        var account = LocateSteamAccount(root);
        return [install, account];
    }

    /// <summary>
    /// The per-account <c>localconfig.vdf</c>. When several accounts have signed in on this machine, the most recently
    /// written one is used (Steam's own "last account"), without reading any login file.
    /// </summary>
    private static LocatedConfigFile LocateSteamAccount(string? root)
    {
        if (root is null)
        {
            return new LocatedConfigFile("account", Format.Vdf, "", false);
        }

        var userdata = Path.Combine(root, "userdata");
        if (Directory.Exists(userdata))
        {
            var candidates = Directory.EnumerateDirectories(userdata)
                .Select(dir => Path.Combine(dir, "config", "localconfig.vdf"))
                .Where(File.Exists)
                .Select(p => (Path: p, Written: File.GetLastWriteTimeUtc(p)))
                .OrderByDescending(x => x.Written)
                .ToList();
            if (candidates.Count > 0)
            {
                return new LocatedConfigFile("account", Format.Vdf, candidates[0].Path, true);
            }
        }

        // Not signed in yet: name where it will be, so import can report "sign in first".
        return new LocatedConfigFile("account", Format.Vdf, Path.Combine(userdata, "<account>", "config", "localconfig.vdf"), false);
    }
}
