using System.ComponentModel;
using System.Diagnostics;

namespace Mesura.Updating;

/// <summary>
/// The one place the update component starts a process (architecture §2.3): <c>msiexec</c> for the update, the updater copy
/// of the command line, and Mesura again afterwards. Each only on the user's click on Install (or the updater's own sequence
/// that click started), and only with files the manifest's signature vouches for.
/// </summary>
#pragma warning disable RS0030 // The one process launcher of the update component (class remarks).
public sealed class InstallerLauncher : IInstallerRunner
{
    public int Run(ApplyRequest request, IReadOnlyList<string> arguments)
    {
        // msiexec reads its own command line: a property with spaces is NAME="value", not "NAME=value" (which is what an argument list would produce, and which
        // makes msiexec show its usage box and wait on it).
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), BuildCommandLine(arguments)) { UseShellExecute = false, CreateNoWindow = false };

        using var process = Process.Start(start) ?? throw new InvalidOperationException("msiexec didn't start.");
        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>The command line msiexec understands for these arguments: options and paths quoted when they have spaces, properties as NAME="value".</summary>
    internal static string BuildCommandLine(IReadOnlyList<string> arguments)
    {
        static string Quote(string text) => text.Length > 0 && !text.Any(c => c is ' ' or '	' or '"') ? text : "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        return string.Join(' ', arguments.Select(argument =>
        {
            var equals = argument.IndexOf('=', StringComparison.Ordinal);
            var isProperty = equals > 0 && !argument.StartsWith('/') && argument[..equals].All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
            return isProperty ? argument[..(equals + 1)] + Quote(argument[(equals + 1)..]) : Quote(argument);
        }));
    }

    public void WaitUntilReplaceable(ApplyRequest request, TimeSpan timeout)
    {
        var folder = request.InstallFolder ?? Path.GetDirectoryName(request.RestartExecutable);
        if (folder is null || !Directory.Exists(folder))
        {
            return;
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && Locked(folder))
        {
            Thread.Sleep(1000);
        }
    }

    private static bool Locked(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories).Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false; // not ours to replace without rights: the installer deals with that
            }
        }

        return false;
    }

    /// <summary>One updater per user session: a named mutex in the session's namespace (an elevated updater of the same user sees it too).</summary>
    public IDisposable? AcquireSingleInstance()
    {
        var mutex = new Mutex(initiallyOwned: true, @"Local\MesuraUpdateApply", out var created);
        if (!created)
        {
            mutex.Dispose();
            return null;
        }

        return new Gate(mutex);
    }

    private sealed class Gate(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    public string? InstalledVersion(ApplyRequest request) => InstallRegistration.Read()?.Version;

    /// <summary>
    /// Waits until the install a killed or crashed client started has ended, finished or rolled back. The install's own verbose log says so: the service writes
    /// it to the end and its last line is "MainEngineThread is returning"; a log that has stopped growing for a while (or never appeared) means nothing more is going
    /// on. (Windows Installer's <c>_MSIExecute</c> mutex is no signal: it belongs to the client and goes with it.)
    /// </summary>
    public void WaitUntilInstallerIdle(ApplyRequest request, TimeSpan timeout) => WaitUntilIdle(request.LogPath, timeout, QuietPeriod);

    internal static void WaitUntilIdle(string logPath, TimeSpan timeout, TimeSpan quietPeriod)
    {
        var deadline = DateTime.UtcNow + timeout;
        var lastSize = -1L;
        var lastChange = DateTime.UtcNow;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var log = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (log.Length != lastSize)
                {
                    lastSize = log.Length;
                    lastChange = DateTime.UtcNow;
                }

                var tail = new byte[(int)Math.Min(log.Length, 8192)];
                log.Seek(log.Length - tail.Length, SeekOrigin.Begin);
                log.ReadExactly(tail);

                // The log is ANSI or UTF-16 depending on how msiexec was asked; the sentence is ASCII either way once the zero bytes are dropped.
                if (System.Text.Encoding.Latin1.GetString(tail).Replace("\0", "", StringComparison.Ordinal).Contains("MainEngineThread is returning", StringComparison.Ordinal))
                {
                    return;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // No log yet, or it can't be read right now: the quiet period below decides.
            }

            if (DateTime.UtcNow - lastChange > quietPeriod)
            {
                return;
            }

            Thread.Sleep(500);
        }
    }

    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(10);

    public bool WaitForExit(int processId, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.WaitForExit(timeout);
        }
        catch (ArgumentException)
        {
            return true; // no such process: it has already gone
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    public void Restart(string executable, string arguments)
    {
        if (!File.Exists(executable))
        {
            return;
        }

        // After a machine-wide update this process is elevated: Mesura is started as the signed-in user, unelevated, never with the updater's administrator rights
        // (and never as another account that approved the prompt). Without a desktop shell to take the token from, it starts the ordinary way.
        if (Environment.IsPrivilegedProcess && ShellProcess.TryStart(executable, arguments, Path.GetDirectoryName(executable)!))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(executable, arguments) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! });
    }

    /// <summary>
    /// Copies the command line out of the program folder (the installer replaces that folder whole, so the updater can't run
    /// from it) and starts the copy to apply the staged update, elevated when the install is for every user and this process
    /// isn't. The caller then exits, so no file of the installed version is open when the installer needs it.
    /// </summary>
    /// <returns><c>null</c> when started; otherwise what stopped it.</returns>
    public static string? StartUpdater(UpdateStore store, string commandLineExecutable, IReadOnlyList<string> arguments, bool elevate)
    {
        try
        {
            var copy = store.CopyIn(commandLineExecutable, Path.Combine("run-" + Guid.NewGuid().ToString("N")[..8], "mesura.exe"));
            var start = new ProcessStartInfo(copy) { UseShellExecute = elevate, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(copy)! };
            if (elevate)
            {
                start.Verb = "runas";
            }

            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            Process.Start(start);
            return null;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            return "elevation-declined";
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return e.Message;
        }
    }

    /// <summary>
    /// Starts the installed command line's <c>update stage</c> for a machine-wide install, elevated through the usual prompt when this process isn't
    /// (docs/security.md): the installed program (Program Files) is what runs elevated, never a copy from the user's folders.
    /// </summary>
    /// <returns><c>null</c> when started; <c>elevation-declined</c> when the prompt was refused; otherwise what stopped it.</returns>
    public static string? StartStage(string installedCommandLine, IReadOnlyList<string> arguments, bool elevate)
    {
        try
        {
            var start = new ProcessStartInfo(installedCommandLine) { UseShellExecute = elevate, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(installedCommandLine)! };
            if (elevate)
            {
                start.Verb = "runas";
            }

            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            Process.Start(start);
            return null;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            return "elevation-declined";
        }
        catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException)
        {
            return e.Message;
        }
    }

    /// <summary>Starts the protected copy of the updater (already elevated: it is started by the stage, which is) with the verified files.</summary>
    public static string? StartStaged(string stagedExecutable, IReadOnlyList<string> arguments)
    {
        try
        {
            var start = new ProcessStartInfo(stagedExecutable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(stagedExecutable)! };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            Process.Start(start);
            return null;
        }
        catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException)
        {
            return e.Message;
        }
    }

    /// <summary>Starts the installer package for the user to go through (a copy of Mesura that isn't installed).</summary>
    public static string? OpenInstaller(string packagePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), $"/i \"{packagePath}\"") { UseShellExecute = true });
            return null;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return e.Message;
        }
    }
}
