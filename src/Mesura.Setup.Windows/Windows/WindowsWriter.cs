using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Mesura.Core.Windows;
using Microsoft.Win32;

namespace Mesura.Setup.Windows.Windows;

/// <summary>
/// The one owner of the Windows configuration layer's side effects (vault D023, architecture §4.9): registry values
/// under HKCU and HKLM, a service's <c>Start</c> value, and four fixed system tools run from System32 — <c>dism.exe</c>
/// to turn an optional feature on or off, <c>powercfg.exe</c> to install a built-in power scheme, make one active, and
/// set a power setting's AC value (<c>/setacvalueindex</c>, then <c>/setactive SCHEME_CURRENT</c> so it takes effect),
/// <c>schtasks.exe /Change /TN &lt;task&gt; /ENABLE|/DISABLE</c> for one of Windows' own scheduled tasks, and
/// <c>netsh.exe interface tcp set global &lt;name&gt;=&lt;value&gt;</c> for an allowed global TCP/IP setting (quality pass,
/// 2026-10-24: receive-window auto-tuning, receive-side scaling, receive segment coalescing). It is only called by <see cref="WindowsConfigurator"/>, after the user previewed and asked to apply, and by
/// <see cref="StartupEntry"/> for Mesura's own sign-in entry when the user ticks it; it never touches security settings.
/// Every failure is thrown for the caller to undo and report.
/// </summary>
public sealed partial class WindowsWriter : IWindowsWriter
{
    private const int DismRestartRequired = 3010;
    private readonly UserContext _user;

    /// <param name="user">The signed-in user whose per-user settings are written (from the state reader).</param>
    public WindowsWriter(UserContext user) => _user = user ?? throw new ArgumentNullException(nameof(user));
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromMinutes(10);

    public void SetRegistry(string hive, string path, string name, RegistryKind kind, string value)
    {
        using var root = Root(hive);
#pragma warning disable RS0030 // The Windows configuration layer's one registry writer (class remarks).
        using var key = root.CreateSubKey(path, writable: true)
            ?? throw new InvalidOperationException($@"Couldn't open {hive}\{path} for writing.");
        if (kind == RegistryKind.DWord)
        {
            key.SetValue(name, unchecked((int)uint.Parse(value, CultureInfo.InvariantCulture)), RegistryValueKind.DWord);
        }
        else
        {
            key.SetValue(name, value, RegistryValueKind.String);
        }
#pragma warning restore RS0030
    }

    public void DeleteRegistry(string hive, string path, string name)
    {
        using var root = Root(hive);
#pragma warning disable RS0030 // The Windows configuration layer's one registry writer (class remarks).
        using var key = root.OpenSubKey(path, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
#pragma warning restore RS0030
    }

    public void DeleteRegistryKeyIfEmpty(string hive, string path)
    {
        using var root = Root(hive);
        using (var key = root.OpenSubKey(path))
        {
            if (key is null || key.ValueCount > 0 || key.SubKeyCount > 0)
            {
                return;
            }
        }

#pragma warning disable RS0030 // The Windows configuration layer's one registry writer (class remarks).
        root.DeleteSubKey(path, throwOnMissingSubKey: false);
#pragma warning restore RS0030
    }

    public void SetServiceStart(string name, int start)
    {
        if (!PlainName().IsMatch(name))
        {
            throw new ArgumentException($"'{name}' is not a service name.", nameof(name));
        }

        using var root = Root("HKLM");
#pragma warning disable RS0030 // The Windows configuration layer's one registry writer (class remarks).
        using var key = root.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}", writable: true)
            ?? throw new InvalidOperationException($"The service {name} isn't on this system.");
        key.SetValue("Start", start, RegistryValueKind.DWord);
#pragma warning restore RS0030
    }

    public bool SetFeature(string name, bool enabled)
    {
        if (!PlainName().IsMatch(name))
        {
            throw new ArgumentException($"'{name}' is not a feature name.", nameof(name));
        }

        var code = Run("dism.exe", ["/Online", enabled ? "/Enable-Feature" : "/Disable-Feature", $"/FeatureName:{name}", "/NoRestart", "/Quiet"]);
        return code switch
        {
            0 => false,
            DismRestartRequired => true,
            _ => throw new InvalidOperationException($"DISM couldn't {(enabled ? "enable" : "disable")} {name} (exit code {code}).")
        };
    }

    public void CreatePowerScheme(string schemeGuid)
    {
        var guid = Guid.Parse(schemeGuid).ToString("D");
        var code = Run("powercfg.exe", ["/duplicatescheme", guid, guid]);
        if (code != 0)
        {
            throw new InvalidOperationException($"powercfg couldn't install the power scheme {guid} (exit code {code}).");
        }
    }

    public void ActivatePowerScheme(string schemeGuid)
    {
        var guid = Guid.Parse(schemeGuid).ToString("D");
        var code = Run("powercfg.exe", ["/setactive", guid]);
        if (code != 0)
        {
            throw new InvalidOperationException($"powercfg couldn't make {guid} active (exit code {code}).");
        }
    }

    public void SetPowerSetting(string scheme, string subgroup, string setting, uint acValue)
    {
        var s = Guid.Parse(scheme).ToString("D");
        var g = Guid.Parse(subgroup).ToString("D");
        var p = Guid.Parse(setting).ToString("D");
        var code = Run("powercfg.exe", ["/setacvalueindex", s, g, p, acValue.ToString(CultureInfo.InvariantCulture)]);
        if (code != 0)
        {
            throw new InvalidOperationException($"powercfg couldn't set {p} (exit code {code}).");
        }

        // Re-apply whichever plan is active so the new value takes effect, without switching plans.
        code = Run("powercfg.exe", ["/setactive", "SCHEME_CURRENT"]);
        if (code != 0)
        {
            throw new InvalidOperationException($"powercfg couldn't re-apply the active plan (exit code {code}).");
        }
    }

    public void SetTaskEnabled(string path, bool enabled)
    {
        if (!TaskPath().IsMatch(path))
        {
            throw new ArgumentException($"'{path}' is not one of Windows' own scheduled tasks.", nameof(path));
        }

        var code = Run("schtasks.exe", ["/Change", "/TN", path, enabled ? "/ENABLE" : "/DISABLE"]);
        if (code != 0)
        {
            throw new InvalidOperationException($"schtasks couldn't turn {path} {(enabled ? "on" : "off")} (exit code {code}).");
        }
    }

    public void SetTcpGlobal(string name, string value)
    {
        if (!TcpGlobalSetting.IsAllowed(name, value))
        {
            throw new ArgumentException($"'{name}={value}' isn't a TCP setting Mesura writes.", nameof(name));
        }

        var code = Run("netsh.exe", ["interface", "tcp", "set", "global", $"{name.ToLowerInvariant()}={value.ToLowerInvariant()}"]);
        if (code != 0)
        {
            throw new InvalidOperationException($"netsh couldn't set the TCP setting {name} to {value} (exit code {code}).");
        }
    }

    public void DeletePowerScheme(string schemeGuid)
    {
        var guid = Guid.Parse(schemeGuid).ToString("D");
        var code = Run("powercfg.exe", ["/delete", guid]);
        if (code != 0)
        {
            throw new InvalidOperationException($"powercfg couldn't delete the power scheme {guid} (exit code {code}).");
        }
    }

    /// <summary>
    /// The root a catalog hive maps to: HKLM itself, or for HKCU the signed-in user's own hive under HKEY_USERS (never
    /// the hive of whatever account this process runs as). Without a resolved user, per-user writes are refused.
    /// </summary>
    private RegistryKey Root(string hive)
    {
        if (hive == "HKLM")
        {
            return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        }

        if (hive != "HKCU")
        {
            throw new ArgumentException($"Unsupported hive '{hive}'.", nameof(hive));
        }

        if (!_user.Resolved)
        {
            throw new InvalidOperationException($"Per-user settings refused: {_user.Reason}");
        }

        using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
#pragma warning disable RS0030 // The Windows configuration layer's one registry writer (class remarks).
        return users.OpenSubKey(_user.Sid!, writable: true)
            ?? throw new InvalidOperationException($"The registry of {_user.Account} isn't loaded.");
#pragma warning restore RS0030
    }

    /// <summary>Runs a System32 tool with fixed arguments and returns its exit code.</summary>
    private static int Run(string tool, IReadOnlyList<string> arguments)
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
#pragma warning disable RS0030 // The Windows configuration layer's fixed dism/powercfg launches (class remarks).
            if (!process.Start())
#pragma warning restore RS0030
            {
                throw new InvalidOperationException($"{tool} could not be started.");
            }
        }
        catch (Win32Exception e)
        {
            throw new InvalidOperationException($"{tool} could not be started: {e.Message}", e);
        }

        // Drain both pipes so a chatty tool can't block on a full buffer; the text itself isn't needed.
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(ToolTimeout))
        {
#pragma warning disable RS0030 // Stopping the tool this class started, after its timeout (class remarks).
            process.Kill(entireProcessTree: true);
#pragma warning restore RS0030
            throw new InvalidOperationException($"{tool} did not finish within {ToolTimeout.TotalMinutes:0} minutes and was stopped.");
        }

        return process.ExitCode;
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainName();

    // The same rule the catalog enforces: only Windows' own tasks, plain names.
    [GeneratedRegex(@"^\\Microsoft\\Windows(\\[A-Za-z0-9 ._()-]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex TaskPath();
}
