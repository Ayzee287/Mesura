using System.Text;
using Mesura.Core.Apps;

namespace Mesura.Setup.Windows.Apps;

/// <summary>
/// Whether Vencord is installed, read from Discord's own files, since winget can't see a patch (redesign 2026-10 §5).
/// The Vencord installer patches stable Discord's newest version folder: it keeps Discord's original archive as
/// <c>resources\_app.asar</c> and puts a small loader in its place as <c>resources\app.asar</c> (a file, or a folder in
/// older installers) that requires Vencord's <c>patcher.js</c>. Both were read on PC-000. A Discord update installs a
/// new version folder without the patch, so Vencord then reads "not installed", which is the truth: it needs patching
/// again. Read-only: it opens at most the first 64 KB of the loader.
/// </summary>
public static class VencordDetector
{
    private const int MaxLoaderBytes = 64 * 1024;

    /// <param name="localAppData">The signed-in user's <c>%LOCALAPPDATA%</c>.</param>
    public static InstallState Detect(string localAppData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppData);
        var discord = Path.Combine(localAppData, "Discord");
        if (!Directory.Exists(discord))
        {
            return InstallState.NotInstalled; // no stable Discord to patch
        }

        var newest = Directory.EnumerateDirectories(discord, "app-*")
            .Select(d => (Path: d, Version: Version.TryParse(Path.GetFileName(d)["app-".Length..], out var v) ? v : null))
            .Where(d => d.Version is not null)
            .OrderByDescending(d => d.Version)
            .Select(d => d.Path)
            .FirstOrDefault();
        if (newest is null)
        {
            return InstallState.NotInstalled;
        }

        var resources = Path.Combine(newest, "resources");
        if (!File.Exists(Path.Combine(resources, "_app.asar")))
        {
            return InstallState.NotInstalled; // Discord's archive is still in place: not patched
        }

        var loader = Path.Combine(resources, "app.asar");
        var text = File.Exists(loader) ? Head(loader)
            : Directory.Exists(loader) && File.Exists(Path.Combine(loader, "index.js")) ? Head(Path.Combine(loader, "index.js"))
            : "";
        return text.Contains("vencord", StringComparison.OrdinalIgnoreCase) ? InstallState.Installed : InstallState.NotInstalled;
    }

    private static string Head(string path)
    {
        using var stream = File.OpenRead(path);
        var buffer = new byte[Math.Min(MaxLoaderBytes, stream.Length)];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }
}
