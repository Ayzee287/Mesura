using Mesura.Core.Support;

namespace Mesura.Setup.Windows.Apps;

/// <summary>
/// Which <c>winget.exe</c> an <i>elevated</i> Mesura may start (0.3.0-rc2, docs/security.md). Started by bare name, Windows looks in the program's own folder,
/// the current folder and every folder of PATH, and the user's PATH has a folder any program running as the user can write to; an elevated Mesura that took
/// whatever it found there would give a planted <c>winget.exe</c> administrator rights. So an elevated launch names the file itself and accepts only what a
/// non-administrator cannot have put there: a file in a protected folder, or the genuine App Execution Alias App Installer puts in the user's
/// <c>WindowsApps</c> folder (a reparse point to the packaged program; it is the one place winget normally is, and a plain file there is not accepted).
/// </summary>
public static class WingetLocator
{
    public sealed record Located(string? Path, string? Refusal);

    /// <param name="pathVariable">The PATH of this process.</param>
    /// <param name="localAppData">The signed-in user's local application data folder.</param>
    /// <param name="fileExists">Whether a file exists.</param>
    /// <param name="isAlias">Whether the file is a reparse point (an App Execution Alias).</param>
    /// <param name="whyWritable">Why a non-administrator can change a folder, or <c>null</c> when it is protected (<see cref="ProgramFolderTrust.Why"/>).</param>
    public static Located ForElevatedLaunch(string pathVariable, string localAppData, Func<string, bool> fileExists, Func<string, bool> isAlias, Func<string, string?> whyWritable)
    {
        ArgumentNullException.ThrowIfNull(pathVariable);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(isAlias);
        ArgumentNullException.ThrowIfNull(whyWritable);
        var userAliases = System.IO.Path.Combine(localAppData, "Microsoft", "WindowsApps");

        foreach (var entry in pathVariable.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var folder = entry.Trim('"');
            if (!System.IO.Path.IsPathFullyQualified(folder))
            {
                continue; // a relative entry means "wherever the current folder is": never searched for an elevated launch
            }

            var candidate = System.IO.Path.Combine(folder, "winget.exe");
            if (!fileExists(candidate))
            {
                continue;
            }

            // The first winget on the path is the one a shell would run, so it is the one judged: a bad one in front is not skipped past.
            if (string.Equals(System.IO.Path.TrimEndingDirectorySeparator(folder), userAliases, StringComparison.OrdinalIgnoreCase) && isAlias(candidate))
            {
                return new Located(candidate, null);
            }

            return whyWritable(folder) is { } why
                ? new Located(null, $"Mesura won't start {candidate} with administrator rights: it is a plain file in a folder that programs running as you can change ({why}). Start Mesura without elevation to install apps, or fix the PATH.")
                : new Located(candidate, null);
        }

        return new Located(null, null); // none found: winget isn't installed, and the launch fails the way it always did
    }

    /// <summary>The real thing: this process's PATH and file system.</summary>
    public static Located ForElevatedLaunch() => ForElevatedLaunch(
        Environment.GetEnvironmentVariable("PATH") ?? "",
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        System.IO.File.Exists,
        path => (System.IO.File.GetAttributes(path) & System.IO.FileAttributes.ReparsePoint) != 0,
        folder => System.IO.Directory.Exists(folder) ? ProgramFolderTrust.Why(folder) : "the folder doesn't exist");
}
