using Mesura.Core.Apps;
using Mesura.Core.Windows;

namespace Mesura.Setup.Windows.Apps;

/// <summary>
/// The install service as the app and the CLI compose it: winget in the context <see cref="WingetContext"/> decides for
/// the signed-in user, and the detectors for apps winget can't see. A detector reads the signed-in user's own files, so
/// it reads them only when this process runs as that user; otherwise the app's state is unknown rather than read from
/// another account's profile. The Visual C++ runtimes have a detector each (<see cref="VcRuntimeDetector"/>).
/// </summary>
public static class AppDetectors
{
    /// <summary>The detectors by name (<see cref="AppPackage.Detector"/>) for the signed-in <paramref name="user"/>.</summary>
    public static IReadOnlyDictionary<string, Func<InstallState>> For(UserContext user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var sameUser = ProcessUser.Is(user);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var detectors = new Dictionary<string, Func<InstallState>>(StringComparer.OrdinalIgnoreCase)
        {
            ["vencord"] = () => sameUser ? VencordDetector.Detect(localAppData) : InstallState.Unknown,
        };

        // The Visual C++ runtimes are machine-wide: read from the registry for any account.
        foreach (var (id, detect) in VcRuntimeDetector.Detectors())
        {
            detectors[id] = detect;
        }

        return detectors;
    }

    /// <summary>The install service for the signed-in <paramref name="user"/>, with Mesura's elevation.</summary>
    public static AppInstallService Installer(UserContext user, bool elevated) =>
        new(new WingetRunner(user, elevated), For(user));
}
