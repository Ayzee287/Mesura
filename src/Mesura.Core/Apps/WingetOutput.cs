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

    /// <summary>
    /// Reads whether an update is offered from <c>winget list --exact --id &lt;wingetId&gt; --upgrade-available</c>. winget exits 0 both when an
    /// update exists and when none does (and prints "No installed package found" in the second case, in the user's language), so the answer is
    /// the id appearing as a token of a table line, never the exit code alone or any message. "Not installed" is up to date: nothing to update.
    /// Anything else (not launched, a source error) is unknown, never "up to date".
    /// </summary>
    public static UpdateCheck ParseUpgradeAvailable(AppPackage package, WingetRunResult list)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(list);

        if (!list.Launched)
        {
            return new UpdateCheck(package, UpdateState.Unknown);
        }

        if (list.ExitCode == NoApplicationsFound)
        {
            return new UpdateCheck(package, UpdateState.UpToDate);
        }

        if (list.ExitCode != 0)
        {
            return new UpdateCheck(package, UpdateState.Unknown);
        }

        foreach (var line in list.Output.Split('\n'))
        {
            var tokens = line.Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
            var at = Array.FindIndex(tokens, token => token.Equals(package.WingetId, StringComparison.OrdinalIgnoreCase));
            if (at < 0)
            {
                continue;
            }

            // After the id: the installed version, the available one, and the source when winget has more than one. A version with a
            // qualifier ("< 1.0") or a name that moved the columns leaves the versions unread, not guessed.
            var rest = tokens.Skip(at + 1).ToList();
            if (rest.Count == 3 && rest[2] is "winget" or "msstore")
            {
                rest.RemoveAt(2);
            }

            return rest.Count == 2
                ? new UpdateCheck(package, UpdateState.Available, rest[0], rest[1])
                : new UpdateCheck(package, UpdateState.Available);
        }

        return new UpdateCheck(package, UpdateState.UpToDate);
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

    /// <summary>
    /// Turns a <c>winget upgrade</c> exit code into an outcome status and a human detail. "No newer version" is not a failure, and success
    /// is only a claim until the caller asks again whether an update is still offered.
    /// </summary>
    public static (InstallStatus Status, string Detail) DescribeUpgrade(int exitCode) => (uint)exitCode switch
    {
        0 => (InstallStatus.Succeeded, "Updated."),
        0x8A15002B => (InstallStatus.AlreadyInstalled, $"Already up to date; winget has no newer version. ({FormatExitCode(exitCode)})"),
        0x8A150014 => (InstallStatus.Failed, $"winget does not see this app as installed. ({FormatExitCode(exitCode)})"),
        0x8A150109 => (InstallStatus.SucceededRebootRequired, "Updated; restart Windows to finish."),
        0x8A15010B => (InstallStatus.SucceededRebootRequired, "Updated; the installer is restarting Windows."),
        _ => DescribeInstall(exitCode),
    };

    /// <summary>
    /// Turns a <c>winget uninstall</c> exit code into an outcome status and a human detail. Success is only a claim until the caller asks
    /// again whether the app is still installed (winget exits 0 for a Firefox uninstall that removed nothing).
    /// </summary>
    public static (InstallStatus Status, string Detail) DescribeUninstall(int exitCode) => (uint)exitCode switch
    {
        0 => (InstallStatus.Succeeded, "Uninstalled."),
        0x8A150014 => (InstallStatus.AlreadyInstalled, $"Not installed; nothing to remove. ({FormatExitCode(exitCode)})"),
        0x8A150109 => (InstallStatus.SucceededRebootRequired, "Uninstalled; restart Windows to finish."),
        0x8A15010B => (InstallStatus.SucceededRebootRequired, "Uninstalled; the uninstaller is restarting Windows."),
        0x8A150030 => (InstallStatus.Failed, $"The uninstaller failed. ({FormatExitCode(exitCode)})"),
        _ => DescribeInstall(exitCode),
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
