using System.Globalization;

namespace Mesura.Core.Apps;

/// <summary>
/// Pure parsing of winget's console output and exit codes, so the install service's decisions are testable without
/// launching anything. Decisions rest on exit codes and package ids, never on message text, which winget localises.
/// </summary>
public static class WingetOutput
{
    /// <summary><c>APPINSTALLER_CLI_ERROR_NO_APPLICATIONS_FOUND</c>: <c>winget list</c> matched nothing.</summary>
    public const int NoApplicationsFound = unchecked((int)0x8A150014);

    /// <summary>
    /// Reads an install state from <c>winget list --exact --id &lt;wingetId&gt;</c>: exit 0 with the id in the table is
    /// installed, "no applications found" is not installed, and anything else (not launched, a source error, output
    /// without the id) is unknown rather than guessed.
    /// </summary>
    public static InstallState DetectState(WingetRunResult list, string wingetId)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentException.ThrowIfNullOrWhiteSpace(wingetId);

        if (!list.Launched)
        {
            return InstallState.Unknown;
        }

        if (list.ExitCode == NoApplicationsFound)
        {
            return InstallState.NotInstalled;
        }

        return list.ExitCode == 0 && ContainsIdToken(list.Output, wingetId) ? InstallState.Installed : InstallState.Unknown;
    }

    private static bool ContainsIdToken(string output, string wingetId) =>
        output.Split(['\n', '\r', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Any(token => token.Equals(wingetId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether a line of winget's streamed output is console animation (spinner frames, progress bars) rather than a
    /// message worth showing or logging.
    /// </summary>
    public static bool IsNoise(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var t = line.Trim();
        return t.Length == 0
            || t is "-" or "\\" or "|" or "/"
            || t.Contains('█', StringComparison.Ordinal) || t.Contains('▒', StringComparison.Ordinal);
    }

    /// <summary>Formats a winget exit code as an unsigned hex string, as winget documents its codes.</summary>
    public static string FormatExitCode(int exitCode) =>
        "0x" + ((uint)exitCode).ToString("X8", CultureInfo.InvariantCulture);

    // The documented winget return codes worth naming (winget-cli doc/windows/package-manager/winget/returnCodes.md,
    // checked 2026-10-18). Anything else is reported as a failure with its hex code.
    private static readonly Dictionary<uint, (InstallStatus Status, string Detail)> KnownCodes = new()
    {
        [0x8A150061] = (InstallStatus.AlreadyInstalled, "Already installed; winget made no change."),
        [0x8A15002B] = (InstallStatus.AlreadyInstalled, "Already installed, with no newer version to install."),
        [0x8A15010D] = (InstallStatus.AlreadyInstalled, "Another version is already installed."),
        [0x8A15010E] = (InstallStatus.AlreadyInstalled, "A newer version is already installed."),
        [0x8A150109] = (InstallStatus.SucceededRebootRequired, "Installed; restart Windows to finish."),
        [0x8A15010B] = (InstallStatus.SucceededRebootRequired, "Installed; the installer is restarting Windows."),
        [0x8A15010C] = (InstallStatus.Cancelled, "The installation was cancelled."),
        [0x8A150005] = (InstallStatus.Cancelled, "Cancelled."),
        [0x8A150014] = (InstallStatus.Failed, "winget found no package with this id."),
        [0x8A150010] = (InstallStatus.Failed, "None of the package's installers apply to this system."),
        [0x8A150011] = (InstallStatus.Failed, "The downloaded installer's hash did not match its manifest."),
        [0x8A150101] = (InstallStatus.Failed, "The application is running; close it and try again."),
        [0x8A150103] = (InstallStatus.Failed, "A file is in use; close the application and try again."),
        [0x8A150102] = (InstallStatus.Failed, "Another installation is in progress; try again later."),
        [0x8A150104] = (InstallStatus.Failed, "A dependency of this package is missing."),
        [0x8A150110] = (InstallStatus.Failed, "The package's dependencies failed to install."),
        [0x8A150105] = (InstallStatus.Failed, "The disk is full."),
        [0x8A150107] = (InstallStatus.Failed, "No network connection."),
        [0x8A15010A] = (InstallStatus.Failed, "Restart Windows, then try again."),
        [0x8A15010F] = (InstallStatus.Failed, "Blocked by policy."),
    };

    /// <summary>Turns a <c>winget install</c> exit code into an outcome status and a human detail.</summary>
    public static (InstallStatus Status, string Detail) DescribeInstall(int exitCode)
    {
        if (exitCode == 0)
        {
            return (InstallStatus.Succeeded, "Installed.");
        }

        return KnownCodes.TryGetValue((uint)exitCode, out var known)
            ? (known.Status, $"{known.Detail} ({FormatExitCode(exitCode)})")
            : (InstallStatus.Failed, $"winget exited with {FormatExitCode(exitCode)}.");
    }
}
