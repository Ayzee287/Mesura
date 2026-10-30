using Microsoft.Win32;

namespace Mesura.Updating;

public enum InstallScope
{
    /// <summary>For every user: Program Files, registered in HKLM; changing it needs administrator rights.</summary>
    Machine,

    /// <summary>For one user: <c>%LOCALAPPDATA%\Programs\Mesura</c>, registered in HKCU.</summary>
    User,
}

/// <summary>What the installer recorded about where Mesura is installed (<c>Software\Mesura</c>, HKLM or HKCU).</summary>
public sealed record InstallInfo(InstallScope Scope, string Folder, string Version, string Language = "en")
{
    public string Executable => Path.Combine(Folder, "Mesura.exe");

    public string CommandLine => Path.Combine(Folder, "cli", "mesura.exe");
}

/// <summary>
/// Reads the installer's registration. A copy of Mesura that isn't the registered one (unzipped by hand, run from a USB stick,
/// a build folder) is "portable": it can check for updates, but an update for it means running the installer, not replacing files.
/// </summary>
public static class InstallRegistration
{
    public const string KeyPath = @"Software\Mesura";

    public static InstallInfo? Read()
    {
        foreach (var (hive, scope) in new[] { (RegistryHive.LocalMachine, InstallScope.Machine), (RegistryHive.CurrentUser, InstallScope.User) })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = root.OpenSubKey(KeyPath);
                if (key?.GetValue("InstallDir") is string { Length: > 0 } folder)
                {
                    var exists = File.Exists(Path.Combine(folder, "Mesura.exe"));
                    if (exists)
                    {
                        return new InstallInfo(scope, NormalizeFolder(folder), key.GetValue("Version") as string ?? "", key.GetValue("Language") is string { Length: > 0 } language ? language : "en");
                    }
                }
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // Unreadable counts as not installed.
            }
        }

        return null;
    }

    /// <summary>
    /// The folder without its trailing separator. Windows Installer stores directories with one, and a command-line argument that ends in a
    /// backslash before its closing quote is read as an escaped quote (the installer then waits on an error box nobody sees).
    /// </summary>
    internal static string NormalizeFolder(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    /// <summary>Whether <paramref name="executable"/> is the installed copy (the one the installer registered).</summary>
    public static bool IsInstalledCopy(string executable, InstallInfo? info = null)
    {
        info ??= Read();
        return info is not null && string.Equals(Path.GetFullPath(executable), info.Executable, StringComparison.OrdinalIgnoreCase);
    }
}
