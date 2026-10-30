using System.Globalization;
using System.Management;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Mesura.Core.Windows;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Power;
using Windows.Win32.System.RemoteDesktop;

namespace Mesura.Platform.Windows.Windows;

/// <summary>
/// Reads the Windows state the configuration operations depend on: registry values, service start types, optional
/// feature states, power schemes and settings, and scheduled tasks. Read-only, like every Platform collector: keys are
/// opened with the read-only <c>OpenSubKey(string)</c>, WMI is only queried, and the power and Task Scheduler interfaces
/// are only read. A failed read is reported as missing or unknown, never thrown.
/// </summary>
public sealed partial class SystemStateReader : ISystemReader
{
    private readonly ISystemSettingSource? _tools;

    /// <summary>The tool-backed reader this instance was given (so a writer can share it), if any.</summary>
    public ISystemSettingSource? Tools => _tools;

    public SystemStateReader()
    {
    }

    /// <param name="tools">
    /// Where tool-backed settings (<see cref="SystemSetting"/>: boot entry, hibernation, memory agent) are read from. Those
    /// need a Windows tool, which only the setup layer may start, so it hands its reader in; without one they read as unknown.
    /// </param>
    public SystemStateReader(ISystemSettingSource tools) => _tools = tools;

    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";
    private const string PowerSchemesKey = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";

    // Every optional feature's state from one WMI query (each query costs most of a second, filtered or not), kept until
    // Refresh. A long-running caller (the app) refreshes before each detection pass and before a revert.
    private volatile IReadOnlyDictionary<string, FeatureState>? _features;

    public int Build { get; } = Environment.OSVersion.Version.Build;

    public bool IsElevated { get; } = ReadElevation();

    /// <summary>
    /// The user signed in to this Windows session. Per-user ("HKCU") settings are read from their own hive under
    /// <c>HKEY_USERS</c>, so an elevated run under another administrator's account still reads the right user.
    /// </summary>
    public UserContext User { get; } = ReadUserContext();

    private static readonly SYSTEM_POWER_CAPABILITIES? Capabilities = ReadCapabilities();

    /// <summary>A system battery that isn't short-term: a UPS on a desktop counts as no battery.</summary>
    public bool? HasBattery { get; } = Capabilities is { } c ? c.SystemBatteriesPresent && !c.BatteriesAreShortTerm : null;

    /// <summary>Whether the platform supports Modern Standby (S0 low power idle; <c>AoAc</c> in the power capabilities).</summary>
    public bool? ModernStandby { get; } = Capabilities is { } c ? c.AoAc : null;

    /// <summary>
    /// Whether the edition honours machine policies: every edition but the Home family (<c>Core</c>, <c>CoreN</c>,
    /// <c>CoreSingleLanguage</c>, <c>CoreCountrySpecific</c>); <c>null</c> when <c>EditionID</c> can't be read.
    /// </summary>
    public bool? PolicyEdition
    {
        get
        {
            var edition = ReadRegistry("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionID");
            return edition is { ValueExists: true, Kind: RegistryKind.String, Value: { Length: > 0 } id }
                ? !id.StartsWith("Core", StringComparison.OrdinalIgnoreCase)
                : null;
        }
    }

    private static SYSTEM_POWER_CAPABILITIES? ReadCapabilities() =>
        PInvoke.GetPwrCapabilities(out var capabilities) ? capabilities : null;

    private static bool ReadElevation()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public RegistryReading ReadRegistry(string hive, string path, string name)
    {
        if (hive == "HKCU" && !User.Resolved)
        {
            return RegistryReading.Missing; // whose settings these would be is unknown: read nothing
        }

        try
        {
            using var root = RegistryKey.OpenBaseKey(hive == "HKLM" ? RegistryHive.LocalMachine : RegistryHive.Users, RegistryView.Registry64);
            using var key = root.OpenSubKey(hive == "HKLM" ? path : User.Sid + @"\" + path);
            if (key is null)
            {
                return RegistryReading.Missing;
            }

            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value is null)
            {
                return new RegistryReading(true, false, null, null);
            }

            return ToReading(key.GetValueKind(name), value);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return RegistryReading.Failed; // not the same as missing: nothing is known about the value
        }
    }

    /// <summary>
    /// A value as the registry API returned it. A DWORD written with more than 4 bytes comes back as a long or a byte
    /// array: it is read as a value of a type Mesura doesn't write (left alone), never cast.
    /// </summary>
    internal static RegistryReading ToReading(RegistryValueKind kind, object value) => (kind, value) switch
    {
        (RegistryValueKind.DWord, int dword) => new RegistryReading(true, true, RegistryKind.DWord,
            unchecked((uint)dword).ToString(CultureInfo.InvariantCulture)),
        (RegistryValueKind.String, string text) => new RegistryReading(true, true, RegistryKind.String, text),
        (RegistryValueKind.Binary, byte[] bytes) => new RegistryReading(true, true, RegistryKind.Binary,
            string.Join(',', bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)))),
        _ => new RegistryReading(true, true, null, Convert.ToString(value, CultureInfo.InvariantCulture)),
    };

    public IReadOnlyList<string> EnumerateSubKeys(string hive, string path)
    {
        if (hive == "HKCU" && !User.Resolved)
        {
            return [];
        }

        try
        {
            using var root = RegistryKey.OpenBaseKey(hive == "HKLM" ? RegistryHive.LocalMachine : RegistryHive.Users, RegistryView.Registry64);
            using var key = root.OpenSubKey(hive == "HKLM" ? path : User.Sid + @"\" + path);
            return key is null ? [] : [.. key.GetSubKeyNames().Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    public string? ReadSystemSetting(string kind, string name) => _tools?.Read(kind, name);

    private const string AppxRepositoryKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    /// <summary>
    /// The packaged apps registered for the signed-in user, from the package repository's keys (named
    /// <c>Name_Version_Arch_ResourceId_PublisherId</c>), which is what <c>Get-AppxPackage</c> lists. A package matches on its
    /// exact name; none installed is an empty list; an unreadable repository is <c>null</c>.
    /// </summary>
    public IReadOnlyList<string>? ReadAppxPackages(string name)
    {
        if (!User.Resolved)
        {
            return null;
        }

        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
            using var key = root.OpenSubKey(User.Sid + @"\" + AppxRepositoryKey);
            if (key is null)
            {
                return null;
            }

            var prefix = name + "_";
            return [.. key.GetSubKeyNames().Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && n.Split('_').Length == 5)];
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public ServiceReading ReadService(string name)
    {
        var start = ReadRegistry("HKLM", $@"{ServicesKey}\{name}", "Start");
        return start.KeyExists && start.Kind == RegistryKind.DWord
            ? new ServiceReading(true, int.Parse(start.Value!, CultureInfo.InvariantCulture))
            : ServiceReading.Absent;
    }

    public FeatureState ReadFeature(string name)
    {
        if (!FeatureName().IsMatch(name))
        {
            return FeatureState.Unknown;
        }

        var features = _features ??= ReadFeatures();
        return features is null ? FeatureState.Unknown
            : features.TryGetValue(name, out var state) ? state
            : FeatureState.Absent; // no row: this image doesn't carry the feature
    }

    /// <summary>
    /// A scheduled task's state, read through the Task Scheduler's own interface (<c>Schedule.Service</c>; read only:
    /// connect, find the task, read <c>Enabled</c>). A task or folder that isn't there is Absent; anything else that
    /// stops the read is Unknown.
    /// </summary>
    public FeatureState ReadTask(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var split = path.LastIndexOf('\\');
        if (split < 0)
        {
            return FeatureState.Unknown;
        }

        var folder = split == 0 ? "\\" : path[..split];
        var name = path[(split + 1)..];
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null)
            {
                return FeatureState.Unknown;
            }

            dynamic service = Activator.CreateInstance(type)!;
            service.Connect();
            dynamic task = service.GetFolder(folder).GetTask(name);
            return (bool)task.Enabled ? FeatureState.Enabled : FeatureState.Disabled;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException
            || (e is System.Runtime.InteropServices.COMException && unchecked((uint)e.HResult) is 0x80070002 or 0x80070003))
        {
            return FeatureState.Absent; // ERROR_FILE_NOT_FOUND / ERROR_PATH_NOT_FOUND (mapped to .NET exceptions): not on this PC
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or UnauthorizedAccessException
            or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException)
        {
            return FeatureState.Unknown;
        }
    }

    /// <summary>
    /// A global TCP/IP setting, read through Windows' network management classes (<c>root\StandardCimv2</c>, read only):
    /// receive-window auto-tuning from every TCP setting template that reports a level (<c>MSFT_NetTCPSetting</c>;
    /// <c>normal</c> only when all of them are, otherwise the first other level found), receive-side scaling and receive
    /// segment coalescing from <c>MSFT_NetOffloadGlobalSetting</c>. In netsh's words; <c>null</c> when unreadable.
    /// </summary>
    public string? ReadTcpGlobal(string name)
    {
        try
        {
            switch (name.ToLowerInvariant())
            {
                case "autotuninglevel":
                {
                    using var searcher = new ManagementObjectSearcher(@"root\StandardCimv2", "SELECT SettingName, AutoTuningLevelLocal FROM MSFT_NetTCPSetting");
                    using var results = searcher.Get();
                    var levels = new List<string>();
                    foreach (var row in results)
                    {
                        using (row)
                        {
                            if (row["AutoTuningLevelLocal"] is { } value)
                            {
                                levels.Add(AutoTuningWord(Convert.ToUInt32(value, CultureInfo.InvariantCulture)));
                            }
                        }
                    }

                    return levels.Count == 0 ? null : levels.FirstOrDefault(l => l != "normal") ?? "normal";
                }

                case "rss" or "rsc":
                {
                    var property = name.Equals("rss", StringComparison.OrdinalIgnoreCase) ? "ReceiveSideScaling" : "ReceiveSegmentCoalescing";
                    using var searcher = new ManagementObjectSearcher(@"root\StandardCimv2", $"SELECT {property} FROM MSFT_NetOffloadGlobalSetting");
                    using var results = searcher.Get();
                    foreach (var row in results)
                    {
                        using (row)
                        {
                            if (row[property] is { } value)
                            {
                                return Convert.ToUInt32(value, CultureInfo.InvariantCulture) switch
                                {
                                    0 => "disabled",
                                    1 => "enabled",
                                    _ => null,
                                };
                            }
                        }
                    }

                    return null;
                }

                default:
                    return null;
            }
        }
        catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException
            or InvalidCastException or FormatException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>netsh's word for a <c>MSFT_NetTCPSetting.AutoTuningLevelLocal</c> value.</summary>
    internal static string AutoTuningWord(uint level) => level switch
    {
        0 => "disabled",
        1 => "highlyrestricted",
        2 => "restricted",
        3 => "normal",
        4 => "experimental",
        _ => $"level {level}",
    };

    /// <summary>Forgets what was read before (optional feature states), so the next reads see the system as it is now.</summary>
    public void Refresh()
    {
        _features = null;
        _tools?.Refresh();
    }

    /// <summary>Every optional feature on this image and its state, or <c>null</c> when WMI couldn't be read.</summary>
    private static Dictionary<string, FeatureState>? ReadFeatures()
    {
        try
        {
            var states = new Dictionary<string, FeatureState>(StringComparer.OrdinalIgnoreCase);
            using var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT Name, InstallState FROM Win32_OptionalFeature");
            using var results = searcher.Get();
            foreach (var row in results)
            {
                using (row)
                {
                    if (row["Name"] is string name)
                    {
                        states[name] = Convert.ToUInt32(row["InstallState"], CultureInfo.InvariantCulture) switch
                        {
                            1 => FeatureState.Enabled,
                            2 => FeatureState.Disabled,
                            3 => FeatureState.Absent,
                            _ => FeatureState.Unknown,
                        };
                    }
                }
            }

            return states;
        }
        catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    /// <summary>
    /// The setting's effective AC value in the scheme, as <c>powercfg /q</c> reports it: the scheme's own value, else a
    /// value the PC maker provisioned, else the Windows default (the power API resolves that order; reading the registry
    /// alone would miss the provisioned and override values). <c>null</c> when the setting isn't on this machine. Readable
    /// as a standard user.
    /// </summary>
    public unsafe uint? ReadPowerSetting(string scheme, string subgroup, string setting)
    {
        if (!Guid.TryParse(scheme, out var schemeId) || !Guid.TryParse(subgroup, out var subgroupId) || !Guid.TryParse(setting, out var settingId))
        {
            return null;
        }

        uint acIndex;
        var error = PInvoke.PowerReadACValueIndex(default, &schemeId, &subgroupId, &settingId, &acIndex);
        if (error == WIN32_ERROR.ERROR_SUCCESS)
        {
            return acIndex;
        }

        if (error == WIN32_ERROR.ERROR_FILE_NOT_FOUND)
        {
            return null; // not on this machine
        }

        // The API couldn't be used: fall back to the scheme's own value, then the Windows default.
        foreach (var path in new[]
        {
            $@"{PowerSchemesKey}\{scheme}\{subgroup}\{setting}",
            $@"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\{subgroup}\{setting}\DefaultPowerSchemeValues\{scheme}",
        })
        {
            var value = ReadRegistry("HKLM", path, "ACSettingIndex");
            if (value.ValueExists && value.Kind == RegistryKind.DWord
                && uint.TryParse(value.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stored))
            {
                return stored;
            }
        }

        return null;
    }

    public PowerReading ReadPower()
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(PowerSchemesKey);
            if (key is null)
            {
                return new PowerReading(null, new HashSet<string>());
            }

            var active = (key.GetValue("ActivePowerScheme") as string)?.Trim().ToLowerInvariant();
            // The schemes Windows offers, as powercfg /l lists them. The registry also keeps hidden ones (on a Modern
            // Standby PC, High performance and Ultimate stay there while Windows offers only Balanced) and the Power mode
            // overlays, so its subkeys are only the fallback.
            var schemes = EnumerateSchemes()
                ?? key.GetSubKeyNames().Select(n => n.Trim('{', '}').ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new PowerReading(active, schemes);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return new PowerReading(null, new HashSet<string>());
        }
    }

    /// <summary>The power schemes Windows enumerates (<c>PowerEnumerate</c>, what powercfg /l uses), or <c>null</c> on failure.</summary>
    private static unsafe HashSet<string>? EnumerateSchemes()
    {
        var schemes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (uint index = 0; ; index++)
        {
            Guid scheme;
            var size = (uint)sizeof(Guid);
            var error = PInvoke.PowerEnumerate(default, null, null, POWER_DATA_ACCESSOR.ACCESS_SCHEME, index, (byte*)&scheme, &size);
            if (error == WIN32_ERROR.ERROR_NO_MORE_ITEMS)
            {
                return schemes;
            }

            if (error != WIN32_ERROR.ERROR_SUCCESS)
            {
                return null;
            }

            schemes.Add(scheme.ToString("D"));
        }
    }

    private static UserContext ReadUserContext()
    {
        string? processSid;
        using (var identity = WindowsIdentity.GetCurrent())
        {
            processSid = identity.User?.Value;
        }

        var account = SessionAccount();
        string? sessionSid = null;
        if (account is not null)
        {
            try
            {
                sessionSid = new NTAccount(account).Translate(typeof(SecurityIdentifier)).Value;
            }
            catch (Exception e) when (e is IdentityNotMappedException or SystemException)
            {
                sessionSid = null;
            }
        }

        var hiveLoaded = false;
        if (sessionSid is not null)
        {
            try
            {
                using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
                using var hive = users.OpenSubKey(sessionSid);
                hiveLoaded = hive is not null;
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                hiveLoaded = false;
            }
        }

        return UserContext.Resolve(processSid, sessionSid, account ?? "", hiveLoaded);
    }

    /// <summary>The account signed in to this process's session as <c>DOMAIN\user</c>, or <c>null</c> (e.g. session 0).</summary>
    private static unsafe string? SessionAccount()
    {
        var session = (uint)System.Diagnostics.Process.GetCurrentProcess().SessionId;

        string? Query(WTS_INFO_CLASS info)
        {
            if (!PInvoke.WTSQuerySessionInformation(HANDLE.Null, session, info, out var buffer, out _))
            {
                return null;
            }

            try
            {
                return buffer.ToString();
            }
            finally
            {
                PInvoke.WTSFreeMemory(buffer.Value);
            }
        }

        var user = Query(WTS_INFO_CLASS.WTSUserName);
        if (string.IsNullOrEmpty(user))
        {
            return null;
        }

        var domain = Query(WTS_INFO_CLASS.WTSDomainName);
        return string.IsNullOrEmpty(domain) ? user : domain + @"\" + user;
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex FeatureName();
}
