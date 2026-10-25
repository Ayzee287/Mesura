namespace Mesura.Core.Windows;

/// <summary>
/// How much an operation can affect the system, after the reassessment's risk model (research, 2026-09-29, §8). The
/// class decides presentation: what the preview warns about and what a preset may contain.
/// </summary>
public enum RiskClass
{
    /// <summary>R0: observation only; nothing to apply (e.g. security posture shown with a link to its settings).</summary>
    Observe,

    /// <summary>R1: a documented per-user preference, reversible in Windows' own settings.</summary>
    UserPreference,

    /// <summary>R2: documented machine configuration, reversible, no security impact (services, features, power).</summary>
    SystemConfiguration,

    /// <summary>R3: a registry-only behaviour knob whose semantics Microsoft doesn't document.</summary>
    Undocumented,

    /// <summary>R6: firmware, driver tools or anything else the user carries out by hand from the steps given.</summary>
    Manual,
}

/// <summary>What has to happen before a change is visible.</summary>
public enum Activation
{
    Immediate,
    ExplorerRestart,
    SignOut,
    Reboot,
}

/// <summary>
/// A condition on the PC itself, beyond its Windows build, for an operation to apply. An operation whose condition
/// doesn't hold is not applicable here; one whose condition can't be read is unknown, so nothing is changed.
/// </summary>
public enum DeviceRequirement
{
    /// <summary>No system battery (a desktop): for changes that would cost a laptop its battery life.</summary>
    NoBattery,

    /// <summary>Not a Modern Standby (S0) PC, where Windows offers only the Balanced plan and its Power mode.</summary>
    NoModernStandby,

    /// <summary>
    /// An edition that honours machine policies: Pro, Enterprise, Education and their variants, not Home. For a policy
    /// value Microsoft lists for those editions only, which Home would accept in the registry and ignore.
    /// </summary>
    ProEdition,
}

/// <summary>
/// What an operation is about, for browsing and filtering: never how much it helps (no operation claims a measured
/// gain unless its description says so) and never how risky it is (<see cref="RiskClass"/> says that).
/// </summary>
public enum OperationImpact
{
    Performance,

    /// <summary>Network behaviour: the TCP/IP stack's defaults, connectivity checks, update sharing.</summary>
    Network,

    Privacy,

    /// <summary>Convenience and interface: the taskbar, Start, notifications, Explorer, input and appearance.</summary>
    Interface,

    /// <summary>System behaviour and functionality: services, features, updates, restored defaults.</summary>
    System,

    /// <summary>Power and thermals: power plans and power settings.</summary>
    Power,
}

/// <summary>
/// What is known about an operation's benefit, kept apart from its recommendation and its risk (2026-10-24 quality
/// pass): a personal preference makes no performance claim; documented means Microsoft documents what it changes and
/// why; unverified means a plausible but unmeasured effect; measured means a measurement Mesura can cite.
/// </summary>
public enum OperationBenefit
{
    Preference,
    Documented,
    Unverified,
    Measured,
}

/// <summary>
/// How the stored value an operation writes is known to be right (not whether the change helps: that is
/// <see cref="OperationBenefit"/>). The settings Mesura applies in its two main profiles must be
/// <see cref="Documented"/> or <see cref="Verified"/>.
/// </summary>
public enum ValueBasis
{
    /// <summary>Microsoft documents the exact value or policy, its meaning and the places it applies.</summary>
    Documented,

    /// <summary>
    /// Checked read-only on a Windows build, not documented by Microsoft: the value name is present in the Windows
    /// components that implement the setting and the matching Settings toggle reads the state the value gives. Writing
    /// the other state back was not exercised on a test PC.
    /// </summary>
    Verified,

    /// <summary>Known from community sources only; never part of a profile's automatic selection.</summary>
    Community,
}

/// <summary>
/// One global TCP/IP setting set with <c>netsh interface tcp set global</c>, by its netsh name: <c>autotuninglevel</c>
/// (disabled, highlyrestricted, restricted, normal, experimental), <c>rss</c> or <c>rsc</c> (enabled, disabled). The
/// catalog refuses any other name or value, so an edit can't reach another netsh command.
/// </summary>
public sealed record TcpGlobalSetting(string Name, string Value)
{
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Allowed = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
    {
        ["autotuninglevel"] = ["disabled", "highlyrestricted", "restricted", "normal", "experimental"],
        ["rss"] = ["enabled", "disabled"],
        ["rsc"] = ["enabled", "disabled"],
    };

    public string Target => $"TCP global {Name}";

    /// <summary>Whether a name and value are among the ones Mesura writes.</summary>
    public static bool IsAllowed(string name, string value) =>
        Allowed.TryGetValue(name, out var values) && values.Contains(value, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Where the app offers a preset (the CLI and profiles use every preset alike).</summary>
public enum PresetTier
{
    /// <summary>A quick selection in the full settings list.</summary>
    Collection,

    /// <summary>One of the outcomes Optimize offers (Recommended, Maximum performance).</summary>
    Primary,

    /// <summary>Offered where it applies, such as the Ghost Spectre follow-up on a Ghost Spectre image.</summary>
    Specialized,
}

/// <summary>A registry value type the configuration layer writes.</summary>
public enum RegistryKind
{
    DWord,
    String,
}

/// <summary>
/// One registry value an operation sets. <see cref="Hive"/> is <c>HKCU</c> or <c>HKLM</c>. When
/// <see cref="AbsentMatches"/> is set, the target is Windows' own default and a missing value already behaves like it,
/// so a missing value counts as in the target state and is never written (a fresh install isn't "not applied").
/// When <see cref="ClearBits"/> is set, the value is a number used as a bit field (a DWORD, or a decimal string such as
/// the accessibility <c>Flags</c>): the target is only those bits cleared, every other bit is kept as found, and
/// <see cref="Value"/> is what is written when the value doesn't exist yet. <see cref="SetBits"/> is its mirror: the target
/// is only those bits set (a flag that is on), every other bit kept as found, and <see cref="Value"/> again the value
/// written when none exists. A setting uses one of the two, never both.
/// </summary>
public sealed record RegistrySetting(
    string Hive, string Path, string Name, RegistryKind Kind, string Value, bool AbsentMatches = false, uint ClearBits = 0, uint SetBits = 0)
{
    public bool Machine => Hive == "HKLM";

    /// <summary>
    /// Whether writing it needs elevation: machine values, and per-user policy values (<c>HKCU\Software\Policies</c>
    /// is read-only to a standard user's token by design).
    /// </summary>
    public bool NeedsElevation => NeedsElevationFor(Hive, Path);

    public static bool NeedsElevationFor(string hive, string path) =>
        hive == "HKLM" || path.StartsWith(@"Software\Policies", StringComparison.OrdinalIgnoreCase);

    public string Target => Name.Length == 0 ? $@"{Hive}\{Path} (default value)" : $@"{Hive}\{Path}\{Name}";
}

/// <summary>How a service starts. Values match the service key's <c>Start</c> DWORD (2 is automatic).</summary>
public enum ServiceStart
{
    Automatic = 2,
    Manual = 3,
    Disabled = 4,
}

/// <summary>One service whose start type an operation sets. It takes effect at the next start of Windows.</summary>
public sealed record ServiceSetting(string Name, ServiceStart Start)
{
    public string Target => $"service {Name}";
}

/// <summary>One Windows optional feature an operation turns on or off (DISM feature name).</summary>
public sealed record FeatureSetting(string Name, bool Enabled)
{
    public string Target => $"feature {Name}";
}

/// <summary>
/// A power scheme to make active. When <see cref="CreateIfMissing"/> is set and the scheme isn't installed (Ultimate
/// Performance often isn't), it is first duplicated from the built-in template with the same GUID.
/// </summary>
public sealed record PowerPlanSetting(string SchemeGuid, string Name, bool CreateIfMissing)
{
    public string Target => "active power scheme";
}

/// <summary>
/// One power setting's value on AC power for the active power plan, set with <c>powercfg /setacvalueindex</c>. Battery
/// (DC) values are never touched. GUIDs are the setting's subgroup and setting, as <c>powercfg /qh</c> lists them.
/// </summary>
public sealed record PowerSetting(string Subgroup, string Setting, string Name, uint AcValue)
{
    public string Target => $"power: {Name} (on AC)";
}

/// <summary>
/// One of Windows' own scheduled tasks to turn on or off, by its full path (always under <c>\Microsoft\Windows\</c>;
/// the catalog refuses anything else, so an edit can't reach a third-party task). Turning it off keeps the task, so
/// turning it back on restores it exactly.
/// </summary>
public sealed record ScheduledTaskSetting(string Path, bool Enabled)
{
    public string Target => $"task {Path}";
}

/// <summary>Steps the user carries out by hand (firmware, driver tools), with where to read more.</summary>
public sealed record ManualProcedure(IReadOnlyList<string> Steps, IReadOnlyList<string> Links);

/// <summary>
/// A guided operation: Mesura reads and shows the current state, states the concrete consequence of changing it and how
/// to restore it, and opens the Windows page where the user makes the change. Used for security-impacting settings
/// (Defender, memory integrity, SmartScreen, firewall): Mesura doesn't script those (vault D024); it guides the change
/// the user makes in Windows, with the state, the consequence and the restore path in front of them.
/// </summary>
public sealed record ObservationSetting(
    IReadOnlyList<RegistrySetting> Reads, string? Service, string OpenUri, string Consequence = "", string Restore = "");

/// <summary>
/// One Windows configuration operation from the catalog: what it changes and why, where it applies, and the settings
/// that make it up. Exactly one kind of content is present: settings to apply, a manual procedure, or an observation.
/// </summary>
public sealed record WindowsOperation(
    string Id,
    string Name,
    string Category,
    string Description,
    RiskClass Risk,
    Activation Activation,
    int? MinBuild,
    int? MaxBuild,
    string Limitations,
    string Source,
    IReadOnlyList<RegistrySetting> Registry,
    IReadOnlyList<ServiceSetting> Services,
    IReadOnlyList<FeatureSetting> Features,
    PowerPlanSetting? PowerPlan,
    IReadOnlyList<PowerSetting> PowerSettings,
    IReadOnlyList<ScheduledTaskSetting> Tasks,
    ManualProcedure? Manual,
    ObservationSetting? Observation,
    IReadOnlyList<DeviceRequirement>? Requires = null,
    OperationImpact Impact = OperationImpact.System,
    string Summary = "",
    IReadOnlyList<TcpGlobalSetting>? Tcp = null,
    OperationBenefit? Benefit = null,
    ValueBasis? Basis = null)
{
    /// <summary>Conditions on this PC (no battery, not Modern Standby) for the operation to apply.</summary>
    public IReadOnlyList<DeviceRequirement> Requirements => Requires ?? [];

    /// <summary>Global TCP/IP settings it sets.</summary>
    public IReadOnlyList<TcpGlobalSetting> TcpSettings => Tcp ?? [];

    /// <summary>
    /// What is known about its benefit: the catalog's own word where it gives one, otherwise from its risk class (a user
    /// preference claims nothing, documented configuration is documented, an undocumented knob is unverified).
    /// </summary>
    public OperationBenefit Evidence => Benefit ?? Risk switch
    {
        RiskClass.UserPreference => OperationBenefit.Preference,
        RiskClass.Undocumented => OperationBenefit.Unverified,
        _ => OperationBenefit.Documented,
    };

    /// <summary>Whether applying it needs an elevated process (machine registry, services, features).</summary>
    public bool NeedsElevation =>
        Registry.Any(r => r.NeedsElevation) || Services.Count > 0 || Features.Count > 0 || PowerPlan is { CreateIfMissing: true }
        || PowerSettings.Count > 0 || Tasks.Count > 0 || TcpSettings.Count > 0;

    /// <summary>Whether it changes per-user settings, which go to the signed-in user (<see cref="UserContext"/>).</summary>
    public bool PerUser => Registry.Any(r => r.Hive == "HKCU") || (Observation?.Reads.Any(r => r.Hive == "HKCU") ?? false);

    /// <summary>Whether it changes machine-wide state (HKLM, services, features, power).</summary>
    public bool MachineWide => Registry.Any(r => r.Machine) || Services.Count > 0 || Features.Count > 0 || PowerPlan is not null
        || PowerSettings.Count > 0 || Tasks.Count > 0 || TcpSettings.Count > 0;

    /// <summary>Whether the product can apply it at all (not manual, not an observation).</summary>
    public bool Applicable => Manual is null && Observation is null;
}

/// <summary>
/// A named selection of operations a user applies together. <see cref="OptionalIds"/> are offered beside it unticked
/// (the user opts in to each); they are never applied as part of the preset itself.
/// </summary>
public sealed record WindowsPreset(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<string> OperationIds,
    PresetTier Tier = PresetTier.Collection,
    string Summary = "",
    IReadOnlyList<string>? Optional = null)
{
    public IReadOnlyList<string> OptionalIds => Optional ?? [];
}
