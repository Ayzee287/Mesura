using System.IO;

namespace Mesura.App;

/// <summary>
/// Mesura's shortcut in the Start menu (architecture §2.3, §4.11): <c>Mesura.lnk</c> in the user's Start menu Programs
/// folder, pointing at the running <c>Mesura.exe</c>. Created through Windows' own shortcut object (<c>WScript.Shell</c>)
/// and removed again, both only when the user ticks or unticks it. It is the app's one file write, so it is its own
/// owner class.
/// </summary>
public static class StartMenuEntry
{
    public static string LinkPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Mesura.lnk");

    /// <summary>Whether the Start menu has Mesura, pointing at this executable.</summary>
    public static bool IsOn(string executable) =>
        Target() is { } target && string.Equals(target, executable, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a Mesura shortcut exists that starts another copy (Mesura was moved).</summary>
    public static bool PointsElsewhere(string executable) => Target() is { } target && !string.Equals(target, executable, StringComparison.OrdinalIgnoreCase);

    /// <summary>Adds (or corrects) the shortcut, or removes it.</summary>
    /// <returns>What went wrong, or <c>null</c>.</returns>
    public static string? Set(string executable, bool on)
    {
        try
        {
            if (!on)
            {
                if (File.Exists(LinkPath))
                {
#pragma warning disable RS0030 // The app's one file write: Mesura's own Start menu shortcut, on the user's untick (class remarks).
                    File.Delete(LinkPath);
#pragma warning restore RS0030
                }

                return null;
            }

            var shell = Shell() ?? throw new InvalidOperationException("Windows' shortcut object isn't available.");
            dynamic link = shell.CreateShortcut(LinkPath);
            link.TargetPath = executable;
            link.WorkingDirectory = Path.GetDirectoryName(executable);
            link.Description = "Mesura: set up Windows and restore your environment";
            link.IconLocation = executable + ",0";
            link.Save();
            return IsOn(executable) ? null : "Windows didn't keep the shortcut.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return $"The Start menu entry wasn't changed: {e.Message}";
        }
    }

    private static dynamic? Shell() =>
        Type.GetTypeFromProgID("WScript.Shell") is { } type ? Activator.CreateInstance(type) : null;

    /// <summary>What the existing shortcut starts, or <c>null</c> when there is none (or it can't be read).</summary>
    private static string? Target()
    {
        if (!File.Exists(LinkPath))
        {
            return null;
        }

        try
        {
            var shell = Shell();
            return shell is null ? null : (string)shell.CreateShortcut(LinkPath).TargetPath;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
            or InvalidCastException)
        {
            return null;
        }
    }
}
