using Mesura.Core.Windows;
using Microsoft.Win32;

namespace Mesura.Platform.Windows.Windows;

/// <summary>
/// Lists what starts with Windows: the <c>Run</c> keys (user, machine, 32-bit machine) and the two Startup folders, each
/// with the on/off flag Windows keeps in <c>StartupApproved</c>. Read only. A key or folder that can't be read contributes
/// nothing, never an error.
/// </summary>
public sealed class StartupScanner(UserContext user) : IStartupSource
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string MachineRunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string MachineRun32Key = @"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Run";

    public IReadOnlyList<StartupItem> Scan()
    {
        var items = new List<StartupItem>();
        if (user.Resolved)
        {
            items.AddRange(ReadRun(RegistryHive.Users, user.Sid + @"\" + RunKey, StartupSource.UserRun));
        }

        items.AddRange(ReadRun(RegistryHive.LocalMachine, MachineRunKey, StartupSource.MachineRun));
        items.AddRange(ReadRun(RegistryHive.LocalMachine, MachineRun32Key, StartupSource.MachineRun32));
        items.AddRange(ReadFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupSource.UserFolder));
        items.AddRange(ReadFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupSource.CommonFolder));
        return [.. items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private IEnumerable<StartupItem> ReadRun(RegistryHive hive, string path, StartupSource source)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(path);
            if (key is null)
            {
                return [];
            }

            var probe = new StartupItem(source, "", "", true);
            var flags = ReadFlags(probe);
            return [.. key.GetValueNames().Where(n => n.Length > 0).Select(n =>
                new StartupItem(source, n, key.GetValue(n, "", RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? "", !flags.Contains(n)))];
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    private IEnumerable<StartupItem> ReadFolder(string folder, StartupSource source)
    {
        try
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                return [];
            }

            var flags = ReadFlags(new StartupItem(source, "", "", true));
            return [.. Directory.EnumerateFiles(folder).Where(f => !string.Equals(Path.GetFileName(f), "desktop.ini", StringComparison.OrdinalIgnoreCase))
                .Select(f => new StartupItem(source, Path.GetFileName(f), f, !flags.Contains(Path.GetFileName(f))))];
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return [];
        }
    }

    /// <summary>The names whose <c>StartupApproved</c> flag turns them off.</summary>
    private HashSet<string> ReadFlags(StartupItem probe)
    {
        var off = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = RegistryKey.OpenBaseKey(probe.Machine ? RegistryHive.LocalMachine : RegistryHive.Users, RegistryView.Registry64);
            using var key = root.OpenSubKey(probe.Machine ? probe.ApprovedPath : user.Sid + @"\" + probe.ApprovedPath);
            if (key is null)
            {
                return off;
            }

            foreach (var name in key.GetValueNames())
            {
                if (key.GetValue(name) is byte[] bytes && StartupItem.IsDisabledFlag(string.Join(',', bytes.Take(1).Select(b => b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)))))
                {
                    off.Add(name);
                }
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // an unreadable flag key: every entry counts as running
        }

        return off;
    }
}
