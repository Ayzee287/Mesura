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
    private readonly SystemTools _tools;

    /// <param name="user">The signed-in user whose per-user settings are written (from the state reader).</param>
    /// <param name="tools">The tool-backed settings (boot entry, hibernation, memory agent); the same instance the state reader reads through.</param>
    public WindowsWriter(UserContext user, SystemTools? tools = null)
    {
        _user = user ?? throw new ArgumentNullException(nameof(user));
        _tools = tools ?? new SystemTools();
    }

    public void SetSystemSetting(string kind, string name, string value) => _tools.Write(kind, name, value);

    public void RemoveAppx(string packageFullName)
    {
        var name = packageFullName.Split('_')[0];
        if (!FullPackageName().IsMatch(packageFullName) || AppxSetting.IsProtected(name))
        {
            throw new ArgumentException($"'{packageFullName}' isn't an app Mesura removes.", nameof(packageFullName));
        }

        // The name was checked against a plain pattern, so it is safe inside the quotes.
        var code = Run("WindowsPowerShell\\v1.0\\powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", $"Remove-AppxPackage -Package '{packageFullName}' -ErrorAction Stop"]);
        if (code != 0)
        {
            throw new InvalidOperationException($"Windows couldn't remove {name} (exit code {code}); it may be a system app.");
        }
    }

    public void RestoreAppx(string packageFamilyName)
    {
        if (!FamilyPackageName().IsMatch(packageFamilyName))
        {
            throw new ArgumentException($"'{packageFamilyName}' isn't a package family name.", nameof(packageFamilyName));
        }

        var code = Run("WindowsPowerShell\\v1.0\\powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", $"Add-AppxPackage -RegisterByFamilyName -MainPackage '{packageFamilyName}' -ErrorAction Stop"]);
        if (code != 0)
        {
            throw new InvalidOperationException($"Windows has no copy of {packageFamilyName} left to register; install it again from the Microsoft Store (exit code {code}).");
        }
    }

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
        else if (kind == RegistryKind.Binary)
        {
            key.SetValue(name, Convert.FromHexString(WindowsEvaluator.NormalizeHex(value).Replace(",", "", StringComparison.Ordinal)), RegistryValueKind.Binary);
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

    public void DuplicatePowerScheme(string sourceGuid, string newGuid, string name, string description)
    {
        var source = Guid.Parse(sourceGuid).ToString("D");
        var destination = Guid.Parse(newGuid).ToString("D");
        if (!PlanText().IsMatch(name) || !PlanText().IsMatch(description.Length == 0 ? "-" : description))
        {
            throw new ArgumentException("A power scheme's name and description are plain text.", nameof(name));
        }

        var code = Run("powercfg.exe", ["/duplicatescheme", source, destination]);
        if (code != 0)
        {
            throw new InvalidOperationException($"powercfg couldn't copy the power scheme {source} (exit code {code}).");
        }

        string[] rename = description.Length == 0 ? ["/changename", destination, name] : ["/changename", destination, name, description];
        code = Run("powercfg.exe", rename);
        if (code != 0)
        {
            throw new InvalidOperationException($"powercfg couldn't name the power scheme {destination} (exit code {code}).");
        }
    }

    /// <summary>
    /// Sets an advanced property with Windows' own cmdlet, on the adapter named by its interface GUID (never by position or a class-key index): the
    /// cmdlet validates the value against the driver's own list and restarts the adapter; the script then waits (up to 40 seconds) for the adapter to be
    /// up again. The GUID, keyword and value are checked against plain patterns first, so nothing of a catalog edit is part of the command line.
    /// </summary>
    public void SetAdapterProperty(string interfaceGuid, string keyword, string registryValue)
    {
        if (!AdapterGuid().IsMatch(interfaceGuid) || !AdapterKeyword().IsMatch(keyword) || !AdapterRegistryValue().IsMatch(registryValue))
        {
            throw new ArgumentException("An adapter property, its value and the adapter are plain words and a GUID.", nameof(keyword));
        }

        RunAdapterCommand($"Set-NetAdapterAdvancedProperty -RegistryKeyword '{keyword}' -RegistryValue '{registryValue}' -ErrorAction Stop", interfaceGuid, waitForLink: true);
    }

    public void SetAdapterBinding(string interfaceGuid, string componentId, bool enabled)
    {
        if (!AdapterGuid().IsMatch(interfaceGuid) || !AdapterComponent().IsMatch(componentId))
        {
            throw new ArgumentException("An adapter binding is a Microsoft component id.", nameof(componentId));
        }

        RunAdapterCommand($"{(enabled ? "Enable" : "Disable")}-NetAdapterBinding -ComponentID '{componentId}' -ErrorAction Stop", interfaceGuid, waitForLink: false);
    }

    public void SetAdapterPower(string interfaceGuid, bool allowTurnOff)
    {
        if (!AdapterGuid().IsMatch(interfaceGuid))
        {
            throw new ArgumentException("An adapter is named by its GUID.", nameof(interfaceGuid));
        }

        RunAdapterCommand($"Set-NetAdapterPowerManagement -AllowComputerToTurnOffDevice {(allowTurnOff ? "Enabled" : "Disabled")} -ErrorAction Stop", interfaceGuid, waitForLink: false);
    }

    private static void RunAdapterCommand(string cmdlet, string interfaceGuid, bool waitForLink)
    {
        var wait = waitForLink
            ? "; $end = (Get-Date).AddSeconds(40); do { Start-Sleep -Milliseconds 500; $up = (Get-NetAdapter | Where-Object { $_.InterfaceGuid -eq $guid }).Status -eq 'Up' } until ($up -or (Get-Date) -gt $end); if (-not $up) { exit 3 }"
            : "";
        var script = $"$ErrorActionPreference = 'Stop'; $guid = '{interfaceGuid}'; $wasUp = (Get-NetAdapter | Where-Object {{ $_.InterfaceGuid -eq $guid }}).Status -eq 'Up'; "
            + $"Get-NetAdapter | Where-Object {{ $_.InterfaceGuid -eq $guid }} | {cmdlet}{(waitForLink ? wait.Replace("if (-not $up)", "if ($wasUp -and -not $up)", StringComparison.Ordinal) : "")}";
        var code = Run("WindowsPowerShell\\v1.0\\powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script]);
        if (code != 0)
        {
            throw new InvalidOperationException(code == 3
                ? "The adapter didn't come back up within 40 seconds of the change."
                : $"Windows couldn't change the adapter (exit code {code}); the driver may not accept the value.");
        }
    }

    public void SetSchemeValue(string scheme, string subgroup, string setting, uint value, bool dc)
    {
        var s = Guid.Parse(scheme).ToString("D");
        var g = Guid.Parse(subgroup).ToString("D");
        var p = Guid.Parse(setting).ToString("D");
        var code = Run("powercfg.exe", [dc ? "/setdcvalueindex" : "/setacvalueindex", s, g, p, value.ToString(CultureInfo.InvariantCulture)]);
        if (code != 0)
        {
            throw new InvalidOperationException($"powercfg couldn't set {p} in the power scheme {s} (exit code {code}).");
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

        string[] arguments = name.Equals("heuristics", StringComparison.OrdinalIgnoreCase)
            ? ["interface", "tcp", "set", "heuristics", value.ToLowerInvariant()]
            : ["interface", "tcp", "set", "global", $"{name.ToLowerInvariant()}={value.ToLowerInvariant()}"];
        var code = Run("netsh.exe", arguments);
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

    [GeneratedRegex(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$", RegexOptions.CultureInvariant)]
    private static partial Regex AdapterGuid();

    [GeneratedRegex(@"^\*?[A-Za-z0-9_]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex AdapterKeyword();

    [GeneratedRegex(@"^[0-9]{1,10}$", RegexOptions.CultureInvariant)]
    private static partial Regex AdapterRegistryValue();

    [GeneratedRegex(@"^ms_[a-z0-9]{2,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex AdapterComponent();

    // The catalog's rule for a Mesura power plan's name and description (letters, digits, spaces and . , - ( )).
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 .,()-]{0,120}$", RegexOptions.CultureInvariant)]
    private static partial Regex PlanText();

    // Name_Version_Architecture_ResourceId_PublisherId, as Windows names an installed package.
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{2,99}_[0-9.]{1,40}_[A-Za-z0-9]{1,10}_[A-Za-z0-9-]{0,40}_[a-z0-9]{13}$", RegexOptions.CultureInvariant)]
    private static partial Regex FullPackageName();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{2,99}_[a-z0-9]{13}$", RegexOptions.CultureInvariant)]
    private static partial Regex FamilyPackageName();

    // The same rule the catalog enforces: only Windows' own tasks, plain names.
    [GeneratedRegex(@"^\\Microsoft\\Windows(\\[A-Za-z0-9 ._()-]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex TaskPath();
}
