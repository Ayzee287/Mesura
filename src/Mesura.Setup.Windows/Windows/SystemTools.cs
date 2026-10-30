using System.ComponentModel;
using System.Diagnostics;
using Mesura.Core.Windows;
using Microsoft.Win32;

namespace Mesura.Setup.Windows.Windows;

/// <summary>
/// Reads and changes the <see cref="SystemSetting"/> kinds that need a Windows tool, as the second owner (beside
/// <see cref="WindowsWriter"/>) of fixed System32 launches: <c>bcdedit.exe</c> for the current boot entry's elements,
/// <c>powercfg.exe /hibernate</c> for hibernation, and <c>powershell.exe</c> running only the memory agent's own cmdlets
/// (<c>Get-MMAgent</c>, <c>Enable-MMAgent</c>, <c>Disable-MMAgent</c>). The arguments come from fixed words: the kind, name and
/// value are checked against <see cref="SystemSetting.Allowed"/> first, so nothing from a catalog edit is ever part of a
/// command line. A failed or refused read is <c>null</c> (unknown), never a guess.
/// </summary>
public sealed class SystemTools : ISystemSettingSource
{
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(60);

    private static readonly Dictionary<string, string> MemoryAgentParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["memorycompression"] = "MemoryCompression",
        ["pagecombining"] = "PageCombining",
        ["applicationlaunchprefetching"] = "ApplicationLaunchPrefetching",
        ["applicationprelaunch"] = "ApplicationPreLaunch",
    };

    private readonly object _gate = new();
    private Dictionary<string, string>? _bcd;
    private bool _bcdRead;
    private Dictionary<string, string>? _memoryAgent;
    private bool _memoryAgentRead;

    public string? Read(string kind, string name)
    {
        if (!SystemSetting.Allowed.ContainsKey($"{kind}:{name}"))
        {
            return null;
        }

        return kind.ToLowerInvariant() switch
        {
            "bcd" => ReadBcd(name),
            "hibernate" => ReadHibernate(),
            "mmagent" => ReadMemoryAgent(name),
            _ => null,
        };
    }

    public void Refresh()
    {
        lock (_gate)
        {
            _bcd = null;
            _bcdRead = false;
            _memoryAgent = null;
            _memoryAgentRead = false;
        }
    }

    /// <summary>Changes one setting. The kind, name and value must be among <see cref="SystemSetting.Allowed"/>.</summary>
    public void Write(string kind, string name, string value)
    {
        if (!SystemSetting.IsAllowed(kind, name, value))
        {
            throw new ArgumentException($"'{kind}:{name}={value}' isn't a setting Mesura writes.", nameof(name));
        }

        var key = name.ToLowerInvariant();
        var word = value.ToLowerInvariant();
        switch (kind.ToLowerInvariant())
        {
            case "bcd":
            {
                string[] arguments = word == "absent" ? ["/deletevalue", "{current}", key] : ["/set", "{current}", key, word];
                var (code, _) = Run("bcdedit.exe", arguments);
                if (code != 0 && word != "absent")
                {
                    throw new InvalidOperationException($"bcdedit couldn't set {key} to {word} (exit code {code}).");
                }

                // Deleting an element that isn't there fails harmlessly: the goal (absent) holds.
                break;
            }

            case "hibernate":
            {
                var (code, _) = Run("powercfg.exe", ["/hibernate", word]);
                if (code != 0)
                {
                    throw new InvalidOperationException($"powercfg couldn't turn hibernation {word} (exit code {code}).");
                }

                break;
            }

            case "mmagent":
            {
                var parameter = MemoryAgentParameters[key];
                var cmdlet = word == "enabled" ? "Enable-MMAgent" : "Disable-MMAgent";
                var (code, _) = Run("WindowsPowerShell\\v1.0\\powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", $"{cmdlet} -{parameter}"]);
                if (code != 0)
                {
                    throw new InvalidOperationException($"{cmdlet} -{parameter} failed (exit code {code}).");
                }

                break;
            }

            default:
                throw new ArgumentException($"Unknown system setting kind '{kind}'.", nameof(kind));
        }

        Refresh();
    }

    private string? ReadBcd(string name)
    {
        Dictionary<string, string>? elements;
        lock (_gate)
        {
            if (!_bcdRead)
            {
                _bcd = ParseBcd();
                _bcdRead = true;
            }

            elements = _bcd;
        }

        if (elements is null)
        {
            return null; // bcdedit needs administrator rights to read the store
        }

        if (!elements.TryGetValue(name.ToLowerInvariant(), out var raw))
        {
            return "absent";
        }

        // bcdedit prints booleans in the display language; only the English words are recognised, anything else is
        // reported as it is (so the change is refused rather than guessed).
        return raw.ToLowerInvariant();
    }

    private static Dictionary<string, string>? ParseBcd()
    {
        var (code, output) = Run("bcdedit.exe", ["/enum", "{current}"]);
        if (code != 0)
        {
            return null;
        }

        var elements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n'))
        {
            var text = line.TrimEnd('\r');
            var gap = text.IndexOf("  ", StringComparison.Ordinal);
            if (gap <= 0 || text.StartsWith('-') || text.StartsWith(' '))
            {
                continue;
            }

            elements[text[..gap].Trim().ToLowerInvariant()] = text[gap..].Trim();
        }

        return elements;
    }

    private static string? ReadHibernate()
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power");
            return key?.GetValue("HibernateEnabled") is int value ? (value != 0 ? "on" : "off") : null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private string? ReadMemoryAgent(string name)
    {
        Dictionary<string, string>? flags;
        lock (_gate)
        {
            if (!_memoryAgentRead)
            {
                _memoryAgent = ParseMemoryAgent();
                _memoryAgentRead = true;
            }

            flags = _memoryAgent;
        }

        return flags is not null && flags.TryGetValue(name.ToLowerInvariant(), out var state) ? state : null;
    }

    private static Dictionary<string, string>? ParseMemoryAgent()
    {
        const string script = "$a = Get-MMAgent -ErrorAction Stop; 'memorycompression=' + $a.MemoryCompression; 'pagecombining=' + $a.PageCombining; "
            + "'applicationlaunchprefetching=' + $a.ApplicationLaunchPrefetching; 'applicationprelaunch=' + $a.ApplicationPreLaunch";
        var (code, output) = Run("WindowsPowerShell\\v1.0\\powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script]);
        if (code != 0)
        {
            return null; // the memory agent needs administrator rights to read
        }

        var flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Trim().Split('=', 2);
            if (parts.Length == 2 && parts[1].Trim().ToLowerInvariant() is "true" or "false")
            {
                flags[parts[0]] = parts[1].Trim().Equals("true", StringComparison.OrdinalIgnoreCase) ? "enabled" : "disabled";
            }
        }

        return flags.Count == 0 ? null : flags;
    }

    /// <summary>Runs a System32 tool with fixed arguments; returns its exit code and standard output.</summary>
    private static (int Code, string Output) Run(string tool, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, tool))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = info };
        try
        {
#pragma warning disable RS0030 // The setup layer's fixed bcdedit/powercfg/PowerShell launches (class remarks).
            if (!process.Start())
#pragma warning restore RS0030
            {
                return (-1, "");
            }
        }
        catch (Win32Exception)
        {
            return (-1, "");
        }

        var output = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(ToolTimeout))
        {
#pragma warning disable RS0030 // Stopping the tool this class started, after its timeout (class remarks).
            process.Kill(entireProcessTree: true);
#pragma warning restore RS0030
            return (-1, "");
        }

        process.WaitForExit(); // let the redirected output finish
        return (process.ExitCode, output.GetAwaiter().GetResult());
    }
}
