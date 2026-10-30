using Mesura.Core.Apps;

namespace Mesura.Setup.Windows.Apps;

/// <summary>
/// Detects AyuGram Desktop by its files. It is a portable app: winget unpacks it under the user's winget packages folder, but a copy
/// made from AyuGram's own installer or release zip sits in Program Files or the user's programs folder, and winget doesn't list it.
/// Only a file whose product name says AyuGram counts, so an unrelated <c>AyuGram.exe</c> doesn't.
/// </summary>
public static class AyuGramDetector
{
    public static InstallState Detect(string localAppData, string programFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppData);
        var candidates = new List<string>
        {
            Path.Combine(programFiles, "AyuGram", "AyuGram.exe"),
            Path.Combine(localAppData, "Programs", "AyuGram", "AyuGram.exe"),
            Path.Combine(localAppData, "AyuGram", "AyuGram.exe"),
        };

        var winget = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(winget))
        {
            foreach (var folder in Directory.EnumerateDirectories(winget, "RadolynLabs.AyuGramDesktop_*"))
            {
                candidates.Add(Path.Combine(folder, "AyuGram.exe"));
                candidates.AddRange(Directory.EnumerateFiles(folder, "AyuGram.exe", SearchOption.AllDirectories).Take(1));
            }
        }

        return candidates.Any(IsAyuGram) ? InstallState.Installed : InstallState.NotInstalled;
    }

    private static bool IsAyuGram(string path)
    {
        try
        {
            return File.Exists(path)
                && System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductName?.Contains("AyuGram", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
