using System.IO;
using System.Runtime.InteropServices;

namespace Mesura.App;

/// <summary>
/// Mesura's shortcut in the Start menu (architecture §2.3, §4.11): <c>Mesura.lnk</c> in the user's Start menu Programs
/// folder, pointing at the running <c>Mesura.exe</c>. Created through Windows' own shell link object (<c>IShellLinkW</c>, which is
/// Unicode: a Cyrillic or other non-ANSI folder works on any PC; the scripting object it replaced failed there) and removed again,
/// both only when the user ticks or unticks it, or when the installed copy points an existing shortcut at itself. It is the app's
/// one file write, so it is its own owner class.
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

            var link = (IShellLinkW)new ShellLink();
            link.SetPath(executable);
            link.SetWorkingDirectory(Path.GetDirectoryName(executable) ?? "");
            link.SetDescription("Mesura: set up Windows and restore your environment");
            link.SetIconLocation(executable, 0);
            ((IPersistFile)link).Save(LinkPath, true);
            return IsOn(executable) ? null : "Windows didn't keep the shortcut.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or COMException or ArgumentException or InvalidCastException)
        {
            return $"The Start menu entry wasn't changed: {e.Message}";
        }
    }

    /// <summary>What the existing shortcut starts, or <c>null</c> when there is none (or it can't be read).</summary>
    private static string? Target()
    {
        if (!File.Exists(LinkPath))
        {
            return null;
        }

        try
        {
            var link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(LinkPath, 0);
            var path = new System.Text.StringBuilder(1024);
            link.GetPath(path, path.Capacity, IntPtr.Zero, 0);
            return path.Length == 0 ? null : path.ToString();
        }
        catch (Exception e) when (e is COMException or InvalidCastException or ArgumentException or IOException)
        {
            return null;
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int maxPath, IntPtr findData, uint flags);

        void GetIDList(out IntPtr pidl);

        void SetIDList(IntPtr pidl);

        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int maxName);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder dir, int maxPath);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);

        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder args, int maxPath);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);

        void GetHotkey(out short hotkey);

        void SetHotkey(short hotkey);

        void GetShowCmd(out int showCmd);

        void SetShowCmd(int showCmd);

        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder iconPath, int iconPathSize, out int iconIndex);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);

        void Resolve(IntPtr hwnd, uint flags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);

        [PreserveSig]
        int IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);

        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);

        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);

        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }
}
