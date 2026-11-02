using System.Diagnostics;
using System.Net.Http;
using System.Security.Principal;
using Mesura.Core.Nvidia;
using Mesura.Core.Setup;
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

/// <summary>What was done before the automated route started, for the checklist: the restore point and the NVIDIA profile backup.</summary>
public sealed record NvPreflight(string RestorePoint, bool RestorePointOk, string ProfileBackup, bool ProfileBackupOk);

/// <summary>How the automated NVCleanstall route went: not attempted (the guided route follows), attempted and stopped (NVCleanstall is left open for the user), or the installer started.</summary>
public sealed record NvAutomationOutcome(bool Attempted, bool InstallStarted, string Detail);

/// <summary>An export of NVIDIA's customised profiles: the file's bytes, or why there are none, and how the tool was run to get them (for the record).</summary>
public sealed record ProfileExport(byte[]? Content, string? Error, string? Route = null);

/// <summary>What the setup asks of the NVIDIA tools (the real ones, or a test's stand-in).</summary>
public interface INvidiaTools
{
    /// <summary>NVCleanstall's executable, or <c>null</c> when it isn't installed.</summary>
    string? FindNvCleanstall();

    /// <summary>Starts NVCleanstall for the user; an error to show, or <c>null</c> when it started.</summary>
    string? LaunchNvCleanstall(string path);

    /// <summary>
    /// The automated route: download NVIDIA's own package for <paramref name="target"/>, check its signature, open NVCleanstall at <paramref name="nvCleanstallPath"/>, drive its window with the
    /// target's configuration up to its finished page and press Install. The default is "not attempted" (the guided route: the user ticks the options in NVCleanstall).
    /// </summary>
    NvAutomationOutcome AutomateDriver(NvidiaTarget target, string nvCleanstallPath, NvPreflight preflight, Func<NvInstallChecklist, bool> confirmInstall, Action<string> progress,
        CancellationToken cancellationToken) => new(Attempted: false, InstallStarted: false, "");

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
    private readonly Func<bool> _elevated;
    private readonly IToolProcess _process;
    private readonly Func<string, string?> _folderTrust;
    private readonly ProfileInspectorStager _stager;

    public NvidiaTools() : this(Elevated, new ToolProcess(), ProgramFolderTrust.Why, new ProfileInspectorStager())
    {
    }

    /// <param name="elevated">Whether this process has administrator rights.</param>
    /// <param name="process">How a tool is started.</param>
    /// <param name="folderTrust">Why a folder can be changed by a non-administrator, or <c>null</c> when it is protected (<see cref="ProgramFolderTrust.Why"/>).</param>
    /// <param name="stager">Makes the verified copy of NVIDIA Profile Inspector an elevated Mesura runs when the installed one is where the user's programs can change it.</param>
    public NvidiaTools(Func<bool> elevated, IToolProcess process, Func<string, string?> folderTrust, ProfileInspectorStager stager)
    {
        _elevated = elevated;
        _process = process;
        _folderTrust = folderTrust;
        _stager = stager;
    }

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

    public NvAutomationOutcome AutomateDriver(NvidiaTarget target, string nvCleanstallPath, NvPreflight preflight, Func<NvInstallChecklist, bool> confirmInstall, Action<string> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(confirmInstall);
        ArgumentNullException.ThrowIfNull(progress);
        if (!Experimental.Enabled(Experimental.NvidiaDriverAutomation))
        {
            // Not part of the 0.5.5 release: the driver step is the guided one (NVCleanstall opens, the user ticks and installs there). Built, tested and held for the next update.
            return new(false, false, "");
        }

        if (!Elevated())
        {
            return new(false, false, "Mesura isn't running as administrator, and NVCleanstall is: Windows doesn't let one drive the other's window.");
        }

        if (string.IsNullOrWhiteSpace(target.DriverUrl))
        {
            return new(false, false, "NVIDIA's lookup gave no download address for the driver.");
        }

        if (LaunchPreconditions(nvCleanstallPath) is { } refused)
        {
            return new(false, false, refused);
        }

        string package;
        try
        {
            progress("Downloading NVIDIA's driver package");
            var folder = ProtectedToolFolder.Persistent("drivers");
            if (folder is null)
            {
                return new(false, false, "A folder only administrators can write to couldn't be made for the driver package, and Mesura does not keep it anywhere else while it has administrator rights.");
            }

            package = new NvidiaOfficialSetup().DownloadAsync(new Uri(target.DriverUrl), folder, elevated: true, cancellationToken).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is InvalidOperationException or HttpRequestException or IOException or UriFormatException or UnauthorizedAccessException)
        {
            return new(false, false, $"The driver package couldn't be downloaded and checked: {e.Message}");
        }

        progress("Opening NVCleanstall");
        if (LaunchNvCleanstall(nvCleanstallPath) is { } launchError)
        {
            return new(false, false, launchError);
        }

        var window = new UiaNvCleanstallWindow();
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!window.IsOpen && DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            Thread.Sleep(1000);
        }

        if (!window.IsOpen)
        {
            return new(true, false, "NVCleanstall's window did not appear.");
        }

        progress("Setting NVCleanstall up");
        var run = NvCleanstallPages.Run(window, target.Config, package, span =>
        {
            Thread.Sleep(span);
            return !cancellationToken.IsCancellationRequested;
        });
        if (run.Status != NvRunStatus.Completed)
        {
            return new(true, false, $"{run.Detail}{(run.StoppedAt.Length > 0 ? $" (page: {run.StoppedAt})" : "")}");
        }

        // The owner's conditions before Install: the restore point, the backup, the tweaks as NVCleanstall has them, the signature box, then a separate confirmation.
        var expected = target.Config.Tweaks.Where(NvCleanstallIds.TweakBoxes.ContainsKey).ToList();
        var checklist = new NvInstallChecklist(preflight.RestorePoint, preflight.RestorePointOk, preflight.ProfileBackup, preflight.ProfileBackupOk, expected, run.TweaksTicked ?? [],
            target.Config.Tweaks.Contains("rebuild-signature", StringComparer.OrdinalIgnoreCase), run.SignatureRebuild, Path.GetFileName(package), target.DriverAtStart, target.Latest);
        if (!checklist.CanInstall)
        {
            _ = confirmInstall(checklist); // shows what is missing; the answer cannot matter
            return new(true, false, $"Install was not pressed: {string.Join(" ", checklist.Blockers)}");
        }

        if (!confirmInstall(checklist))
        {
            return new(true, false, "NVCleanstall's installer is ready and Install was not pressed (not confirmed).");
        }

        if (NvCleanstallPages.PressInstall(window) is { } notPressed)
        {
            return new(true, false, notPressed);
        }

        return new(true, true, "NVCleanstall built the package and started NVIDIA's installer.");
    }

    private static string? LaunchPreconditions(string path) =>
        !Path.GetFileName(path).Equals(NvCleanstallName + ".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path) ? $"NVCleanstall isn't at {path}."
        : Refusal(NvCleanstallName, path, Elevated());

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
            .OrderBy(i => _folderTrust(Path.GetDirectoryName(i.Path)!) is null ? 0 : 1) // a protected copy first: an elevated Mesura runs it as it is
            .ThenByDescending(i => i.Version)
            .FirstOrDefault();
        return found;
    }

    private static Version FileVersion(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
    }

    // How an elevated Mesura runs NVIDIA Profile Inspector (docs/security.md, 2026-11-02). It needs administrator rights only to WRITE the driver's profiles (its README: "must be started
    // elevated to write changes to the NVIDIA driver profile database"), so:
    //   - the tool is somewhere only administrators can change (Program Files): started as it is;
    //   - it is where the user's programs can change it (the per-user winget package): never started with administrator rights as it is. An export only reads, so it is tried with the
    //     user's own unelevated token first; if that gives nothing (or an empty list, which an unelevated read must not be trusted to say), and always for an import, a verified copy
    //     (ProfileInspectorStager) in a folder only administrators can write to is run instead, and only when its hash is a release Mesura knows.
    //   - Mesura not elevated: the tool is started as it is, with the user's own rights (as before).

    public ProfileExport Export(ProfileInspector inspector, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        var folder = Path.GetDirectoryName(inspector.Path)!;
        if (!_elevated() || _folderTrust(folder) is null)
        {
            return ExportBeside(inspector, asShellUser: false, cancellationToken) with { Route = _elevated() ? "as installed, in a folder only administrators can change" : "with your own rights" };
        }

        var unelevated = ExportBeside(inspector, asShellUser: true, cancellationToken);
        unelevated = unelevated with { Route = "with your own rights, not as administrator" };
        if (unelevated.Content is not null && !IsEmpty(unelevated.Content))
        {
            return unelevated;
        }

        var staged = _stager.Stage(inspector, cancellationToken);
        if (staged.Inspector is null)
        {
            // No verified copy to run. A list read with the user's own rights stands as it is, even empty; nothing is claimed that wasn't read.
            return unelevated.Content is not null ? unelevated : new ProfileExport(null, $"{unelevated.Error} {staged.Refusal}".Trim());
        }

        try
        {
            var elevated = ExportBeside(staged.Inspector, asShellUser: false, cancellationToken);
            return elevated.Content is not null ? elevated with { Route = "as administrator, from a copy of the tool Mesura verified" } : unelevated.Content is null ? elevated : unelevated;
        }
        finally
        {
            _stager.Cleanup(Path.GetDirectoryName(staged.Inspector.Path)!);
        }
    }

    private static bool IsEmpty(byte[] export)
    {
        try
        {
            return NipFile.SettingCount(NipFile.Parse(NipFile.Decode(export))) == 0;
        }
        catch (FormatException)
        {
            return false; // unreadable is not "nothing customised": the backup step reports it
        }
    }

    private ProfileExport ExportBeside(ProfileInspector inspector, bool asShellUser, CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(inspector.Path)!;
        var started = DateTime.Now.AddSeconds(-2);
        if (RunError(_process.Run(inspector.Path, ["-exportCustomized"], folder, asShellUser, ToolTimeout, cancellationToken)) is { } error)
        {
            return new ProfileExport(null, error);
        }

        // NVIDIA Profile Inspector writes CustomProfiles_<date>_<time>.nip beside itself; the path can't be chosen.
        var file = new DirectoryInfo(folder).EnumerateFiles("CustomProfiles_*.nip")
            .Where(f => f.LastWriteTime >= started)
            .OrderByDescending(f => f.LastWriteTime)
            .FirstOrDefault();
        if (file is null)
        {
            return new ProfileExport(null, "NVIDIA Profile Inspector finished without writing an export.");
        }

        var bytes = File.ReadAllBytes(file.FullName);
        try
        {
#pragma warning disable RS0030 // The setup layer's NVIDIA tool owner: the export file this run just caused is removed once its bytes are kept (class remarks).
            file.Delete();
#pragma warning restore RS0030
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left beside the tool; the bytes are kept either way.
        }

        return new ProfileExport(bytes, null);
    }

    public string? MergeImport(ProfileInspector inspector, string nipPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        if (!inspector.CanMerge)
        {
            return $"NVIDIA Profile Inspector {inspector.Version} is older than {ProfileInspector.MergeImport}, which can import without replacing profiles.";
        }

        if (!File.Exists(nipPath))
        {
            return $"The profile file {nipPath} is missing.";
        }

        var folder = Path.GetDirectoryName(inspector.Path)!;
        if (!_elevated())
        {
            return RunError(_process.Run(inspector.Path, ["-silent", "-mergeImport", nipPath], folder, asShellUser: false, ToolTimeout, cancellationToken));
        }

        // Elevated: the tool and the file it imports both come from a folder only administrators can write to, so neither can change after it is checked.
        var tool = inspector;
        string? stagedFolder = null;
        if (_folderTrust(folder) is not null)
        {
            var staged = _stager.Stage(inspector, cancellationToken);
            if (staged.Inspector is null)
            {
                return staged.Refusal;
            }

            tool = staged.Inspector;
            stagedFolder = Path.GetDirectoryName(tool.Path)!;
        }

        try
        {
            if (stagedFolder is null)
            {
                var (made, refusal) = _stager.NewFolder();
                if (made is null)
                {
                    return refusal;
                }

                stagedFolder = made;
            }

            var (copy, copyError) = _stager.StageImportFile(stagedFolder, nipPath);
            return copy is null
                ? copyError
                : RunError(_process.Run(tool.Path, ["-silent", "-mergeImport", copy], Path.GetDirectoryName(tool.Path)!, asShellUser: false, ToolTimeout, cancellationToken));
        }
        finally
        {
            if (stagedFolder is not null)
            {
                _stager.Cleanup(stagedFolder);
            }
        }
    }

    /// <returns>An error, or <c>null</c> when it ran and exited.</returns>
    private static string? RunError(ToolRun run) =>
        !run.Started ? $"NVIDIA Profile Inspector didn't start: {run.Error}."
        : run.TimedOut ? "NVIDIA Profile Inspector didn't finish; it may be showing a message. Read it, close it, and check the profiles." // it shows its errors in a dialog even when silent; left for the user to read, not killed
        : null;
}
