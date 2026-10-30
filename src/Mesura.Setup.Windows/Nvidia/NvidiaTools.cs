using System.Diagnostics;
using System.Security.Principal;
using Mesura.Core.Support;
using Microsoft.Win32;

namespace Mesura.Setup.Windows.Nvidia;

/// <summary>NVIDIA Profile Inspector as found on this PC: the executable and its file version.</summary>
public sealed record ProfileInspector(string Path, Version Version)
{
    /// <summary>
    /// The oldest version whose command line can import in merge mode: 3.0.2.1's release notes say "Import can explicitly
    /// use either Merge or Replace". Without the switch, a silent import replaces each profile, and on the Base Profile
    /// that first restores the driver defaults (NVIDIA Profile Inspector source, App.xaml.cs, read 2026-10-23).
    /// </summary>
    public static readonly Version MergeImport = new(3, 0, 2, 1);

    public bool CanMerge => Version >= MergeImport;
}

/// <summary>An export of NVIDIA's customised profiles: the file's bytes, or why there are none.</summary>
public sealed record ProfileExport(byte[]? Content, string? Error);

/// <summary>What the setup asks of the NVIDIA tools (the real ones, or a test's stand-in).</summary>
public interface INvidiaTools
{
    /// <summary>NVCleanstall's executable, or <c>null</c> when it isn't installed.</summary>
    string? FindNvCleanstall();

    /// <summary>Starts NVCleanstall for the user; an error to show, or <c>null</c> when it started.</summary>
    string? LaunchNvCleanstall(string path);

    ProfileInspector? FindProfileInspector();

    /// <summary>Exports every customised profile (<c>-exportCustomized</c>).</summary>
    ProfileExport Export(ProfileInspector inspector, CancellationToken cancellationToken);

    /// <summary>Merges a profile file into the driver's profiles (<c>-silent -mergeImport</c>); an error, or <c>null</c>.</summary>
    string? MergeImport(ProfileInspector inspector, string nipPath, CancellationToken cancellationToken);
}

/// <summary>
/// The setup layer's one owner of the NVIDIA tool launches (architecture §2.3, D031): it starts NVCleanstall for the user,
/// and runs NVIDIA Profile Inspector to export the customised profiles or merge a backup back. Nothing else is launched,
/// no driver is installed here, and an import always names the merge mode. NVIDIA Profile Inspector exits with code 0
/// whether or not an import worked, so the setup verifies every import by exporting again.
/// </summary>
public sealed class NvidiaTools : INvidiaTools
{
    /// <summary>
    /// Why a tool must not be started from <paramref name="executable"/> with the rights of this process, or <c>null</c> when it may. An
    /// elevated Mesura that started a program out of a folder any program the user runs can change (a per-user winget package) would give
    /// whoever replaced that file administrator rights (docs/security.md); not elevated, the tool only has the user's own rights.
    /// </summary>
    public static string? Refusal(string toolName, string executable, bool elevated)
    {
        if (!elevated || ProgramFolderTrust.Why(Path.GetDirectoryName(executable) ?? executable) is not { } why)
        {
            return null;
        }

        return $"{toolName} at {executable} is in a folder that programs running as you can change ({why}), so Mesura won't start it with administrator rights. "
            + "Install it for all users (winget install --scope machine) so it sits in Program Files, then try again.";
    }

    private static bool Elevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private const string NvCleanstallName = "NVCleanstall";
    private const string InspectorExe = "nvidiaProfileInspector.exe";
    private const string InspectorPackage = "Orbmu2k.nvidiaProfileInspector_";
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(90);

    public string? FindNvCleanstall()
    {
        // NVCleanstall's Inno setup registers an uninstall entry named after it, with its folder; winget's manifest
        // carries a malformed product code, so the entry is found by name rather than by that code.
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                foreach (var name in uninstall?.GetSubKeyNames() ?? [])
                {
                    using var entry = uninstall!.OpenSubKey(name);
                    if (entry?.GetValue("DisplayName") is string display && display.StartsWith(NvCleanstallName, StringComparison.OrdinalIgnoreCase)
                        && entry.GetValue("InstallLocation") is string { Length: > 0 } folder
                        && Path.Combine(folder, NvCleanstallName + ".exe") is var exe && File.Exists(exe))
                    {
                        return exe;
                    }
                }
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // Not readable in this view: try the other.
            }
        }

        return null;
    }

    public string? LaunchNvCleanstall(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.GetFileName(path).Equals(NvCleanstallName + ".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            return $"NVCleanstall isn't at {path}.";
        }

        if (Refusal(NvCleanstallName, path, Elevated()) is { } refused)
        {
            return refused;
        }

        try
        {
#pragma warning disable RS0030 // The setup layer's NVIDIA tool owner: start NVCleanstall for the user, on their confirmed setup (class remarks).
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) });
#pragma warning restore RS0030
            return process is null ? "NVCleanstall didn't start." : null;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return $"NVCleanstall didn't start: {e.Message}";
        }
    }

    public ProfileInspector? FindProfileInspector()
    {
        // winget installs the portable package under its Packages folder (per user, or machine-wide when elevated).
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinGet", "Packages"),
        };
        var found = roots.Where(Directory.Exists)
            .SelectMany(r => Directory.EnumerateDirectories(r, InspectorPackage + "*"))
            .Select(d => Path.Combine(d, InspectorExe))
            .Where(File.Exists)
            .Select(p => new ProfileInspector(p, FileVersion(p)))
            .OrderBy(i => ProgramFolderTrust.IsUserWritable(Path.GetDirectoryName(i.Path)!) ? 1 : 0) // a protected copy first: it's the only one an elevated Mesura may run
            .ThenByDescending(i => i.Version)
            .FirstOrDefault();
        return found;
    }

    private static Version FileVersion(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
    }

    public ProfileExport Export(ProfileInspector inspector, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        var folder = Path.GetDirectoryName(inspector.Path)!;
        var started = DateTime.Now.AddSeconds(-2);
        if (Run(inspector, ["-exportCustomized"], cancellationToken) is { } error)
        {
            return new ProfileExport(null, error);
        }

        // NVIDIA Profile Inspector writes CustomProfiles_<date>_<time>.nip beside itself; the path can't be chosen.
        var file = new DirectoryInfo(folder).EnumerateFiles("CustomProfiles_*.nip")
            .Where(f => f.LastWriteTime >= started)
            .OrderByDescending(f => f.LastWriteTime)
            .FirstOrDefault();
        return file is null
            ? new ProfileExport(null, "NVIDIA Profile Inspector finished without writing an export.")
            : new ProfileExport(File.ReadAllBytes(file.FullName), null);
    }

    public string? MergeImport(ProfileInspector inspector, string nipPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        if (!inspector.CanMerge)
        {
            return $"NVIDIA Profile Inspector {inspector.Version} is older than {ProfileInspector.MergeImport}, which can import without replacing profiles.";
        }

        return File.Exists(nipPath) ? Run(inspector, ["-silent", "-mergeImport", nipPath], cancellationToken) : $"The profile file {nipPath} is missing.";
    }

    /// <returns>An error, or <c>null</c> when it ran and exited.</returns>
    private static string? Run(ProfileInspector inspector, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (Refusal("NVIDIA Profile Inspector", inspector.Path, Elevated()) is { } refused)
        {
            return refused;
        }

        var info = new ProcessStartInfo(inspector.Path) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(inspector.Path) };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
#pragma warning disable RS0030 // The setup layer's NVIDIA tool owner: NVIDIA Profile Inspector's export and merge import (class remarks).
            using var process = Process.Start(info);
#pragma warning restore RS0030
            if (process is null)
            {
                return "NVIDIA Profile Inspector didn't start.";
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ToolTimeout);
            try
            {
                process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // It shows its errors in a dialog even when silent; leave it for the user to read rather than kill it.
                return "NVIDIA Profile Inspector didn't finish; it may be showing a message. Read it, close it, and check the profiles.";
            }

            return null;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return $"NVIDIA Profile Inspector didn't start: {e.Message}";
        }
    }
}
