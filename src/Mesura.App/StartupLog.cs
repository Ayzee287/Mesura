using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Mesura.App;

/// <summary>
/// A small plain-text log of how far Mesura got when it started and of any error that stopped it, kept at
/// <c>%LOCALAPPDATA%\Mesura\logs\mesura-startup.log</c> (the temp folder when that can't be written). It exists for the
/// case the in-app session log can't cover: Mesura doesn't open, so there is no window to read it in. It holds the version,
/// Windows build, the command line and one line per startup stage, then the exception text of a failure; no settings, no
/// file contents, no account names beyond what Windows puts in an exception message. Every method swallows its own errors:
/// logging must never be the reason the app doesn't start.
/// </summary>
#pragma warning disable RS0030 // The one file write that exists before the stores do: the startup log, class remarks.
internal static class StartupLog
{
    private const long MaxBytes = 256 * 1024;
    private static readonly object Gate = new();

    public static string Path { get; } = Locate();

    private static string Locate()
    {
        foreach (var root in new[] { Mesura.Core.Support.DataFolder.Local(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)), System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Mesura") })
        {
            try
            {
                if (string.IsNullOrEmpty(root))
                {
                    continue;
                }

                var folder = System.IO.Path.Combine(root, "logs");
                Directory.CreateDirectory(folder);
                var file = System.IO.Path.Combine(folder, "mesura-startup.log");
                using (File.Open(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    // opened for writing now: a folder that exists but can't be written (access denied) must not be chosen
                }

                return file;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // try the next place
            }
        }

        return System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mesura-startup.log");
    }

    /// <summary>Starts a launch's section: what is running and where.</summary>
    public static void Begin(string[] args)
    {
        try
        {
            if (File.Exists(Path) && new FileInfo(Path).Length > MaxBytes)
            {
                File.Move(Path, Path + ".old", overwrite: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // an unrotated log is still a log
        }

        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        Note($"==== Mesura {version} starting: pid {Environment.ProcessId}, {RuntimeInformation.ProcessArchitecture}, {RuntimeInformation.OSDescription}, "
            + $"Windows build {Environment.OSVersion.Version.Build}, .NET {Environment.Version}, args [{string.Join(' ', args)}], "
            + $"exe {Environment.ProcessPath}, 64-bit OS {Environment.Is64BitOperatingSystem}");
    }

    /// <summary>One line: a startup stage reached, or anything worth knowing.</summary>
    public static void Note(string text)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {text}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Debug.WriteLine($"Mesura startup log not written: {e.Message}");
        }
    }

    /// <summary>An error with its full text (type, message, stack, inner exceptions).</summary>
    public static void Fatal(string where, Exception? exception) =>
        Note($"ERROR in {where}: {exception?.ToString() ?? "(no exception object)"}");
}
#pragma warning restore RS0030
