using Mesura.Core.Apps;
using Microsoft.Win32;

namespace Mesura.Setup.Windows.Apps;

/// <summary>
/// Finds the Visual C++ runtimes installed on this PC (read-only), the way <see cref="VcRuntimes"/> describes: the v14
/// runtime from Microsoft's documented <c>VC\Runtimes</c> key, the 2005 to 2013 runtimes from the machine's uninstall
/// entries (both registry views). winget's own list isn't used: it misses runtimes another installer put there.
/// </summary>
public static class VcRuntimeDetector
{
    private const string V14Key = @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>The runtimes found, each once, with the version installed; <c>null</c> when the registry couldn't be read.</summary>
    public static IReadOnlyList<InstalledRuntime>? Detect()
    {
        var found = new Dictionary<string, InstalledRuntime>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var machine32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var machine64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            foreach (var arch in new[] { "x64", "x86" })
            {
                // Microsoft documents the 32-bit view (WOW6432Node); the x64 runtime also writes the 64-bit one.
                if ((V14(machine32, arch) ?? V14(machine64, arch)) is { } version && VcRuntimes.V14(arch) is { } runtime)
                {
                    found[runtime.Id] = new InstalledRuntime(runtime.Id, version);
                }
            }

            foreach (var root in new[] { machine64, machine32 })
            {
                using var uninstall = root.OpenSubKey(UninstallKey);
                foreach (var name in uninstall?.GetSubKeyNames() ?? [])
                {
                    try
                    {
                        using var entry = uninstall!.OpenSubKey(name);
                        if (VcRuntimes.FromDisplayName(entry?.GetValue("DisplayName") as string) is { } runtime && !found.ContainsKey(runtime.Id))
                        {
                            found[runtime.Id] = new InstalledRuntime(runtime.Id, entry?.GetValue("DisplayVersion") as string ?? "");
                        }
                    }
                    catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                    {
                        // Another product's entry that can't be read (or vanished meanwhile) says nothing about the runtimes.
                    }
                }
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }

        return [.. VcRuntimes.All.Where(r => found.ContainsKey(r.Id)).Select(r => found[r.Id])];
    }

    /// <summary>The installed v14 version for an architecture, or <c>null</c> when it isn't installed.</summary>
    private static string? V14(RegistryKey root, string arch)
    {
        using var key = root.OpenSubKey(V14Key + arch);
        if (key?.GetValue("Installed") is not int installed || installed != 1)
        {
            return null;
        }

        return key.GetValue("Major") is int major && key.GetValue("Minor") is int minor && key.GetValue("Bld") is int build
            ? VcRuntimes.V14Version(major, minor, build)
            : (key.GetValue("Version") as string ?? "").TrimStart('v');
    }

    /// <summary>One detector per runtime, by its id (<see cref="VcRuntime.Package"/> names it), reading the registry each time.</summary>
    public static IEnumerable<KeyValuePair<string, Func<InstallState>>> Detectors() =>
        VcRuntimes.All.Select(r => new KeyValuePair<string, Func<InstallState>>(r.Id,
            () => Detect() is not { } installed ? InstallState.Unknown
                : installed.Any(i => i.Id == r.Id) ? InstallState.Installed : InstallState.NotInstalled));
}
