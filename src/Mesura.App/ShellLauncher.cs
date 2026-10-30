using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Mesura.App;

/// <summary>
/// The app's one owner of a process launch (architecture §2.3, "Open a Windows Settings page or a URL for the user"):
/// it opens the Windows page a guided or manual operation names, and relaunches Mesura elevated when the user asks.
/// Both run only on a click, and neither changes anything by itself. Targets are allowlisted, so a hand-edited catalog
/// can't make the app launch an arbitrary program.
/// </summary>
public static class ShellLauncher
{
    private static readonly string[] AllowedPrefixes = ["ms-settings:", "windowsdefender:", "https://"];
    private static readonly string[] AllowedApplets = ["firewall.cpl"];

    /// <summary>Whether a catalog target is something the app will open.</summary>
    public static bool CanOpen(string? target) =>
        !string.IsNullOrWhiteSpace(target)
        && (AllowedPrefixes.Any(p => target.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            || AllowedApplets.Any(a => target.Equals(a, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Opens a Windows settings page, a Windows Security page, an allowlisted applet or an https link.</summary>
    /// <returns>An error to show, or <c>null</c> when it opened.</returns>
    public static string? Open(string target)
    {
        if (!CanOpen(target))
        {
            return Loc.Format("Shell.CantOpen", target);
        }

        return Launch(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    /// <summary>Opens one of Mesura's own folders (history, edited catalogs) in File Explorer; nothing outside them.</summary>
    /// <returns>An error to show, or <c>null</c> when it opened.</returns>
    public static string? OpenFolder(string folder)
    {
        var own = Path.GetFullPath(Mesura.Setup.Windows.CatalogFiles.SharedFolder).TrimEnd('\\') + "\\";
        var full = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
        if (!full.StartsWith(own, StringComparison.OrdinalIgnoreCase))
        {
            return Loc.Format("Shell.OnlyOwnFolders", folder);
        }

        return Directory.Exists(full)
            ? Launch(new ProcessStartInfo(full) { UseShellExecute = true })
            : Loc.Get("Shell.FolderMissing");
    }

    private static readonly Lazy<string?> FolderExposure = new(() => Mesura.Core.Support.ProgramFolderTrust.Why(AppContext.BaseDirectory));

    /// <summary>
    /// Why administrator mode is never offered from this folder (who can write there), or <c>null</c> when the folder is protected (docs/security.md). There is no setting
    /// that overrides it: a per-user install runs without administrator rights, an all-users install (Program Files) has them.
    /// </summary>
    public static string? ElevationBlockedReason => FolderExposure.Value;

    /// <summary>
    /// Starts this executable again elevated (a UAC prompt), with the same arguments plus
    /// <see cref="App.RelaunchedArgument"/>, so the new instance waits for this one to close instead of handing over to it.
    /// </summary>
    /// <returns>An error to show (including a declined prompt), or <c>null</c> when the elevated instance started.</returns>
    public static string? RelaunchElevated(IEnumerable<string> arguments)
    {
        if (ElevationBlockedReason is { } why)
        {
            return Loc.Format("Shell.ElevationBlocked", why);
        }

        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            return Loc.Get("Shell.NoExecutable");
        }

        var info = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
        foreach (var argument in arguments.Where(a => !a.Equals(App.RelaunchedArgument, StringComparison.OrdinalIgnoreCase)).Append(App.RelaunchedArgument))
        {
            info.ArgumentList.Add(argument);
        }

        return Launch(info);
    }

    private static string? Launch(ProcessStartInfo info)
    {
        try
        {
#pragma warning disable RS0030 // The app's one launch owner: open a settings page/URL or relaunch elevated, on a click (class remarks).
            using var process = Process.Start(info);
#pragma warning restore RS0030
            return null;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            return Loc.Get("Shell.Declined");
        }
        catch (Win32Exception e)
        {
            return Loc.Format("Shell.Failed", e.Message);
        }
    }
}
