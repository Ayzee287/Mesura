using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Mesura.Core.Support;

namespace Mesura.Core.Windows;

/// <summary>
/// The Windows configuration operations and presets, read from JSON (<c>windows.catalog.json</c>). As with the app
/// catalog, the built-in copy is embedded and an edited copy in <c>%LOCALAPPDATA%\Mesura</c> replaces it. Validation refuses an
/// inconsistent edit with a message naming the entry, so a hand edit can't produce an operation the engine
/// misreads.
/// </summary>
public sealed partial class WindowsCatalog
{
    public const string FileKind = "Mesura.WindowsCatalog";

    /// <summary>
    /// 2 since the catalog audit and redesign of 2026-10 (bit-field writes, device requirements, Windows-default matches,
    /// impact, tiers, optional preset entries). A build before them refuses a schema 2 copy instead of misreading it,
    /// and this build sets aside an edited copy of an older schema (<c>CatalogFiles</c>), so neither runs on the other's
    /// definitions. 3 since the quality pass of 2026-10-24 (global TCP settings, the network impact, an operation's
    /// benefit): a schema 2 build refuses a schema 3 copy, and this build sets a schema 2 edited copy aside the same way.
    /// 4 since 0.2.0 (binary values, per-adapter registry settings, tool-backed system settings, a stated level, sources):
    /// the same rule holds between 3 and 4.
    /// </summary>
    public const int CurrentSchema = 5;
    public const string FileName = "windows.catalog.json";

    /// <summary>The categories of the Debloat page (bundled apps, AI features) and its generated startup entries; Tweaks leaves them out.</summary>
    public const string BundledAppsCategory = "Bundled apps";

    public const string AiCategory = "AI features";

    public const string StartupCategory = "Startup apps";

    /// <summary>The category of entries that put a Windows default back after a tweak tool changed it.</summary>
    public const string DefaultsCategory = "Restore Windows defaults";

    public const string RetiredFileName = "windows.retired.json";

    private static readonly Lazy<WindowsCatalog> BuiltIn = new(() => Parse(BuiltInJson()));

    private static readonly Lazy<WindowsCatalog> BuiltInRetired = new(() => Parse(ReadResource(RetiredFileName)));

    private WindowsCatalog(IReadOnlyList<WindowsOperation> operations, IReadOnlyList<WindowsPreset> presets)
    {
        Operations = operations;
        Presets = presets;
    }

    public static WindowsCatalog Default => BuiltIn.Value;

    /// <summary>
    /// The operations earlier versions had and this one dropped (0.4 removed 103), reduced to the settings they changed. They are never listed
    /// or applied; an undo of a history record an earlier version wrote still needs to know what those operations declared (<see cref="RevertTrust"/>).
    /// </summary>
    public static WindowsCatalog Retired => BuiltInRetired.Value;

    public IReadOnlyList<WindowsOperation> Operations { get; }

    public IReadOnlyList<WindowsPreset> Presets { get; }

    public WindowsOperation? ById(string id) =>
        Operations.FirstOrDefault(o => o.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public WindowsPreset? PresetById(string id) =>
        Presets.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<WindowsOperation> Resolve(WindowsPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return [.. preset.OperationIds.Select(ById).OfType<WindowsOperation>()];
    }

    public static string BuiltInJson() => ReadResource(FileName);

    private static string ReadResource(string name)
    {
        using var stream = typeof(WindowsCatalog).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The built-in {name} resource is missing from this build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <exception cref="FormatException">The text is not a valid Windows catalog; the message names the entry.</exception>
    public static WindowsCatalog Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        CatalogFile? file;
        try
        {
            file = JsonSerializer.Deserialize(json, WindowsCatalogJsonContext.Default.CatalogFile);
        }
        catch (JsonException e)
        {
            throw new FormatException($"The Windows catalog is not valid JSON: {e.Message}", e);
        }

        if (file is null || !LegacyIdentity.IsKind(file.Kind, FileKind))
        {
            throw new FormatException($"This file is not a Windows catalog (expected kind '{FileKind}').");
        }

        if (file.SchemaVersion > CurrentSchema)
        {
            throw new FormatException($"The Windows catalog uses schema {file.SchemaVersion}; this build reads up to {CurrentSchema}.");
        }

        var operations = new List<WindowsOperation>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in file.Operations ?? [])
        {
            var op = ToOperation(entry);
            if (!ids.Add(op.Id))
            {
                throw new FormatException($"Operation id '{op.Id}' appears more than once.");
            }

            operations.Add(op);
        }

        var presets = new List<WindowsPreset>();
        foreach (var p in file.Presets ?? [])
        {
            if (string.IsNullOrWhiteSpace(p.Id) || presets.Any(x => x.Id.Equals(p.Id, StringComparison.OrdinalIgnoreCase)))
            {
                throw new FormatException($"Preset id '{p.Id}' is missing or appears more than once.");
            }

            var members = p.Operations ?? [];
            var optional = p.Optional ?? [];
            if (optional.FirstOrDefault(id => members.Contains(id, StringComparer.OrdinalIgnoreCase)) is { } both)
            {
                throw new FormatException($"Preset '{p.Id}' names '{both}' both as an operation and as optional.");
            }

            foreach (var id in members.Concat(optional))
            {
                var op = operations.FirstOrDefault(o => o.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                    ?? throw new FormatException($"Preset '{p.Id}' names operation '{id}', which is not in the catalog.");
                if (!op.Applicable)
                {
                    throw new FormatException($"Preset '{p.Id}' names '{id}', which is manual or for information only.");
                }

                if (op.Risk == RiskClass.Undocumented)
                {
                    throw new FormatException($"Preset '{p.Id}' names '{id}', an undocumented setting; those are chosen one at a time.");
                }
            }

            var tier = p.Tier?.ToLowerInvariant() switch
            {
                null or "collection" => PresetTier.Collection,
                "primary" => PresetTier.Primary,
                "specialized" => PresetTier.Specialized,
                _ => throw new FormatException($"Preset '{p.Id}' has unknown tier '{p.Tier}' (primary, specialized or collection)."),
            };

            presets.Add(new WindowsPreset(p.Id, p.Name ?? p.Id, p.Description ?? "", members, tier, p.Summary ?? "", optional));
        }

        return new WindowsCatalog(operations, presets);
    }

    private static WindowsOperation ToOperation(OperationEntry e)
    {
        if (string.IsNullOrWhiteSpace(e.Id) || string.IsNullOrWhiteSpace(e.Name) || string.IsNullOrWhiteSpace(e.Description))
        {
            throw new FormatException($"Operation '{e.Id}' needs an 'id', a 'name' and a 'description'.");
        }

        string Where(string what) => $"Operation '{e.Id}': {what}";

        var risk = e.Risk?.ToLowerInvariant() switch
        {
            "observe" => RiskClass.Observe,
            "userpreference" => RiskClass.UserPreference,
            "systemconfiguration" => RiskClass.SystemConfiguration,
            "undocumented" => RiskClass.Undocumented,
            "manual" => RiskClass.Manual,
            _ => throw new FormatException(Where($"unknown risk '{e.Risk}'.")),
        };

        var activation = e.Activation?.ToLowerInvariant() switch
        {
            null or "immediate" => Activation.Immediate,
            "explorerrestart" => Activation.ExplorerRestart,
            "signout" => Activation.SignOut,
            "reboot" => Activation.Reboot,
            _ => throw new FormatException(Where($"unknown activation '{e.Activation}'.")),
        };

        var registry = (e.Registry ?? []).Select(r => ToRegistry(r, Where)).ToList();
        var services = (e.Services ?? []).Select(s => new ServiceSetting(
            string.IsNullOrWhiteSpace(s.Name) ? throw new FormatException(Where("a service needs a 'name'.")) : s.Name,
            s.Start?.ToLowerInvariant() switch
            {
                "automatic" => ServiceStart.Automatic,
                "manual" => ServiceStart.Manual,
                "disabled" => ServiceStart.Disabled,
                _ => throw new FormatException(Where($"service '{s.Name}' has unknown start '{s.Start}'.")),
            })).ToList();
        var features = (e.Features ?? []).Select(f => new FeatureSetting(
            string.IsNullOrWhiteSpace(f.Name) ? throw new FormatException(Where("a feature needs a 'name'.")) : f.Name,
            f.Enabled ?? throw new FormatException(Where($"feature '{f.Name}' needs 'enabled'.")))).ToList();

        PowerPlanSetting? power = null;
        if (e.PowerPlan is { } p)
        {
            if (!Guid.TryParse(p.Scheme, out _))
            {
                throw new FormatException(Where($"power scheme '{p.Scheme}' is not a GUID."));
            }

            power = new PowerPlanSetting(p.Scheme!.ToLowerInvariant(), p.Name ?? p.Scheme, p.CreateIfMissing ?? false);
        }

        var powerSettings = (e.PowerSettings ?? []).Select(p =>
            !Guid.TryParse(p.Subgroup, out _) || !Guid.TryParse(p.Setting, out _)
                ? throw new FormatException(Where($"power setting '{p.Name}' needs GUIDs for 'subgroup' and 'setting'."))
                : new PowerSetting(p.Subgroup!.ToLowerInvariant(), p.Setting!.ToLowerInvariant(),
                    string.IsNullOrWhiteSpace(p.Name) ? p.Setting! : p.Name,
                    p.Ac ?? throw new FormatException(Where($"power setting '{p.Name}' needs an 'ac' value.")))).ToList();

        var tasks = (e.Tasks ?? []).Select(t =>
            t.Path is null || !TaskPath().IsMatch(t.Path)
                ? throw new FormatException(Where($"task '{t.Path}' must be a full path under \\Microsoft\\Windows\\ (only Windows' own tasks)."))
                : new ScheduledTaskSetting(t.Path, t.Enabled ?? throw new FormatException(Where($"task '{t.Path}' needs 'enabled'.")))).ToList();

        var registryEach = (e.RegistryEach ?? []).Select(r => ToRegistryEach(r, Where)).ToList();

        var system = (e.System ?? []).Select(t =>
            t.Kind is null || t.Name is null || t.Value is null || !SystemSetting.IsAllowed(t.Kind, t.Name, t.Value)
                ? throw new FormatException(Where($"system setting '{t.Kind}:{t.Name}' = '{t.Value}' isn't one Mesura writes."))
                : new SystemSetting(t.Kind.ToLowerInvariant(), t.Name.ToLowerInvariant(), t.Value.ToLowerInvariant())).ToList();

        var appx = (e.Appx ?? []).Select(a =>
            string.IsNullOrWhiteSpace(a.Name) || !AppxSetting.IsPlain(a.Name)
                ? throw new FormatException(Where($"app '{a.Name}' needs a plain package name such as Microsoft.BingNews."))
                : AppxSetting.IsProtected(a.Name)
                    ? throw new FormatException(Where($"app '{a.Name}' is a package Windows depends on; Mesura never removes it."))
                    : new AppxSetting(a.Name)).ToList();

        var tcp = (e.Tcp ?? []).Select(t =>
            t.Name is null || t.Value is null || !TcpGlobalSetting.IsAllowed(t.Name, t.Value)
                ? throw new FormatException(Where($"TCP setting '{t.Name}' = '{t.Value}' isn't one Mesura writes (autotuninglevel, rss or rsc, with one of netsh's values)."))
                : new TcpGlobalSetting(t.Name.ToLowerInvariant(), t.Value.ToLowerInvariant())).ToList();

        ManualProcedure? manual = e.Manual is { } m
            ? new ManualProcedure(m.Steps is { Count: > 0 } ? m.Steps : throw new FormatException(Where("a manual procedure needs steps.")), m.Links ?? [])
            : null;

        ObservationSetting? observe = e.Observe is { } o
            ? new ObservationSetting((o.Reads ?? []).Select(r => ToRegistry(r, Where)).ToList(), o.Service,
                string.IsNullOrWhiteSpace(o.OpenUri) ? throw new FormatException(Where("an observation needs 'openUri'.")) : o.OpenUri,
                o.Consequence ?? "", o.Restore ?? "")
            : null;

        var applyParts = (registry.Count > 0 || registryEach.Count > 0 ? 1 : 0) + (system.Count > 0 ? 1 : 0) + (appx.Count > 0 ? 1 : 0) + (services.Count > 0 ? 1 : 0) + (features.Count > 0 ? 1 : 0) + (power is null ? 0 : 1)
            + (powerSettings.Count > 0 ? 1 : 0) + (tasks.Count > 0 ? 1 : 0) + (tcp.Count > 0 ? 1 : 0);
        var kinds = (applyParts > 0 ? 1 : 0) + (manual is null ? 0 : 1) + (observe is null ? 0 : 1);
        if (kinds != 1)
        {
            throw new FormatException(Where("give exactly one of: settings to apply, 'manual', or 'observe'."));
        }

        if ((risk == RiskClass.Manual) != (manual is not null) || (risk == RiskClass.Observe) != (observe is not null))
        {
            throw new FormatException(Where("risk 'manual' goes with 'manual' and risk 'observe' with 'observe'."));
        }

        if (power is not null && powerSettings.Count > 0)
        {
            // Settings are written into the active plan: one operation switching plans and changing settings would write
            // them into the plan it is leaving. A preset can hold both as two operations (the plan switch runs first).
            throw new FormatException(Where("give either a 'powerPlan' or 'powerSettings', not both; make them two operations."));
        }

        var requires = (e.Requires ?? []).Select(r => r?.ToLowerInvariant() switch
        {
            "nobattery" => DeviceRequirement.NoBattery,
            "nomodernstandby" => DeviceRequirement.NoModernStandby,
            "proedition" => DeviceRequirement.ProEdition,
            "ram16gb" => DeviceRequirement.Ram16Gb,
            "hybridcpu" => DeviceRequirement.HybridCpu,
            _ => throw new FormatException(Where($"unknown requirement '{r}' (noBattery, noModernStandby, proEdition, ram16Gb or hybridCpu).")),
        }).Distinct().ToList();

        var impact = e.Impact?.ToLowerInvariant() switch
        {
            null or "maintenance" => OperationImpact.Maintenance,
            "performance" => OperationImpact.Performance,
            "gaming" => OperationImpact.Gaming,
            "power" => OperationImpact.Power,
            "privacy" => OperationImpact.Privacy,
            "components" => OperationImpact.Components,
            "convenience" => OperationImpact.Convenience,
            "experimental" => OperationImpact.Experimental,
            _ => throw new FormatException(Where($"unknown impact '{e.Impact}' (performance, gaming, power, privacy, components, maintenance, convenience or experimental).")),
        };

        OperationBenefit? benefit = e.Benefit?.ToLowerInvariant() switch
        {
            null => null,
            "preference" => OperationBenefit.Preference,
            "documented" => OperationBenefit.Documented,
            "unverified" => OperationBenefit.Unverified,
            "measured" => OperationBenefit.Measured,
            _ => throw new FormatException(Where($"unknown benefit '{e.Benefit}' (preference, documented, unverified or measured).")),
        };

        ValueBasis? basis = e.ValueBasis?.ToLowerInvariant() switch
        {
            null => null,
            "documented" => ValueBasis.Documented,
            "verified" => ValueBasis.Verified,
            "community" => ValueBasis.Community,
            _ => throw new FormatException(Where($"unknown valueBasis '{e.ValueBasis}' (documented, verified or community).")),
        };

        OperationLevel? level = e.Level?.ToLowerInvariant() switch
        {
            null => null,
            "safe" => OperationLevel.Safe,
            "moderate" => OperationLevel.Moderate,
            "high" => OperationLevel.High,
            _ => throw new FormatException(Where($"unknown level '{e.Level}' (safe, moderate or high).")),
        };

        var sources = e.Sources ?? [];
        if (sources.FirstOrDefault(u => !Uri.TryCreate(u, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) is { } badSource)
        {
            throw new FormatException(Where($"source '{badSource}' isn't a web address."));
        }

        return new WindowsOperation(e.Id, e.Name, e.Category ?? "Other", e.Description, risk, activation, e.MinBuild, e.MaxBuild,
            e.Limitations ?? "", e.Source ?? "", registry, services, features, power, powerSettings, tasks, manual, observe, requires,
            impact, e.Summary ?? "", tcp, benefit, basis, level, registryEach, system, sources, appx);
    }

    private static RegistrySetting ToRegistry(RegistryEntry r, Func<string, string> where)
    {
        if (r.Hive is not ("HKCU" or "HKLM"))
        {
            throw new FormatException(where($"registry hive '{r.Hive}' must be HKCU or HKLM."));
        }

        if (string.IsNullOrWhiteSpace(r.Path) || r.Path.StartsWith('\\') || r.Path.EndsWith('\\') || r.Path.Contains(@"\\", StringComparison.Ordinal)
            || r.Name is null)
        {
            throw new FormatException(where($"registry path '{r.Path}' or name '{r.Name}' is malformed."));
        }

        var kind = ToKind(r.Type, where);

        // An empty name is the key's default value, which is valid; a default value must be a string.
        if (r.Name.Length == 0 && kind != RegistryKind.String)
        {
            throw new FormatException(where("the key's default value (empty name) must be a string."));
        }

        var value = r.Value ?? "";
        CheckValue(kind, r.Name, value, where);

        if (kind == RegistryKind.Binary && (r.ClearBits is not null || r.SetBits is not null))
        {
            throw new FormatException(where($"'{r.Name}' is binary: clearBits and setBits work on numbers only."));
        }

        var clear = r.ClearBits ?? 0;
        if (r.ClearBits is not null
            && (clear == 0 || r.Name.Length == 0 || !uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var initial) || (initial & clear) != 0))
        {
            throw new FormatException(where($"'{r.Name}' clears bits {clear}: it needs a non-zero mask, a named value and a decimal 'value' (written when the value is missing) with those bits already clear."));
        }

        var set = r.SetBits ?? 0;
        if (r.SetBits is not null
            && (set == 0 || r.ClearBits is not null || r.Name.Length == 0 || !uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var start) || (start & set) != set))
        {
            throw new FormatException(where($"'{r.Name}' sets bits {set}: it needs a non-zero mask, no clearBits, a named value and a decimal 'value' (written when the value is missing) with those bits already set."));
        }

        return new RegistrySetting(r.Hive, r.Path, r.Name, kind, value, r.AbsentMatches ?? false, clear, set);
    }

    private static RegistryKind ToKind(string? type, Func<string, string> where) => type?.ToLowerInvariant() switch
    {
        "dword" => RegistryKind.DWord,
        "string" => RegistryKind.String,
        "binary" => RegistryKind.Binary,
        _ => throw new FormatException(where($"registry type '{type}' must be dword, string or binary.")),
    };

    private static void CheckValue(RegistryKind kind, string name, string value, Func<string, string> where)
    {
        if (kind == RegistryKind.DWord && !uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            throw new FormatException(where($"'{name}' is a dword but '{value}' is not a number."));
        }

        if (kind == RegistryKind.Binary && !HexBytes().IsMatch(value))
        {
            throw new FormatException(where($"'{name}' is binary: '{value}' must be comma-separated hex bytes such as \"00,0a,ff\"."));
        }
    }

    private static RegistryEachSetting ToRegistryEach(RegistryEachEntry r, Func<string, string> where)
    {
        if (r.Hive is not ("HKCU" or "HKLM"))
        {
            throw new FormatException(where($"registryEach hive '{r.Hive}' must be HKCU or HKLM."));
        }

        if (string.IsNullOrWhiteSpace(r.PathPattern) || r.PathPattern.StartsWith('\\') || r.PathPattern.EndsWith('\\') || r.PathPattern.Contains(@"\\", StringComparison.Ordinal)
            || r.PathPattern.Split('\\').Count(segment => segment == "*") != 1 || r.PathPattern.Count(c => c == '*') != 1 || r.PathPattern.StartsWith("*\\", StringComparison.Ordinal)
            || r.Name is null || r.Name.Length == 0)
        {
            throw new FormatException(where($"registryEach path '{r.PathPattern}' needs exactly one '*' segment after a fixed parent key, and a named value."));
        }

        var kind = ToKind(r.Type, where);
        var value = r.Value ?? "";
        CheckValue(kind, r.Name, value, where);
        var skip = r.Where?.ValueNotEquals;
        if (skip is not null && (string.IsNullOrEmpty(skip.Name) || skip.Value is null))
        {
            throw new FormatException(where("registryEach 'where.valueNotEquals' needs a 'name' and a 'value'."));
        }

        return new RegistryEachSetting(r.Hive, r.PathPattern, r.Name, kind, value, r.AbsentMatches ?? false, r.Where?.SubkeyExists, skip?.Name, skip?.Value);
    }

    [GeneratedRegex(@"^[0-9a-fA-F]{2}(,[0-9a-fA-F]{2})*$", RegexOptions.CultureInvariant)]
    private static partial Regex HexBytes();

    /// <summary>A Windows scheduled task path: under \\Microsoft\\Windows\\, plain folder and task names.</summary>
    [GeneratedRegex(@"^\\Microsoft\\Windows(\\[A-Za-z0-9 ._()-]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex TaskPath();

    internal sealed record CatalogFile(string? Kind, int SchemaVersion, List<OperationEntry>? Operations, List<PresetEntry>? Presets);

    internal sealed record OperationEntry(
        string? Id, string? Name, string? Category, string? Description, string? Risk, string? Activation,
        int? MinBuild, int? MaxBuild, string? Limitations, string? Source,
        List<RegistryEntry>? Registry, List<ServiceEntry>? Services, List<FeatureEntry>? Features,
        PowerPlanEntry? PowerPlan, List<PowerSettingEntry>? PowerSettings, List<TaskEntry>? Tasks, ManualEntry? Manual, ObserveEntry? Observe,
        List<string?>? Requires = null, string? Impact = null, string? Summary = null, List<TcpEntry>? Tcp = null, string? Benefit = null, string? ValueBasis = null,
        string? Level = null, List<RegistryEachEntry>? RegistryEach = null, List<SystemEntry>? System = null, List<string>? Sources = null,
        List<AppxEntry>? Appx = null);

    internal sealed record AppxEntry(string? Name);

    internal sealed record RegistryEachEntry(string? Hive, string? PathPattern, string? Name, string? Type, string? Value, bool? AbsentMatches = null, EachWhere? Where = null);

    internal sealed record EachWhere(string? SubkeyExists = null, EachValue? ValueNotEquals = null);

    internal sealed record EachValue(string? Name, string? Value);

    internal sealed record SystemEntry(string? Kind, string? Name, string? Value);

    internal sealed record TaskEntry(string? Path, bool? Enabled);

    internal sealed record TcpEntry(string? Name, string? Value);

    internal sealed record PowerSettingEntry(string? Subgroup, string? Setting, string? Name, uint? Ac);

    internal sealed record RegistryEntry(string? Hive, string? Path, string? Name, string? Type, string? Value, bool? AbsentMatches = null,
        uint? ClearBits = null, uint? SetBits = null);

    internal sealed record ServiceEntry(string? Name, string? Start);

    internal sealed record FeatureEntry(string? Name, bool? Enabled);

    internal sealed record PowerPlanEntry(string? Scheme, string? Name, bool? CreateIfMissing);

    internal sealed record ManualEntry(List<string>? Steps, List<string>? Links);

    internal sealed record ObserveEntry(List<RegistryEntry>? Reads, string? Service, string? OpenUri, string? Consequence, string? Restore);

    internal sealed record PresetEntry(string? Id, string? Name, string? Description, List<string>? Operations,
        string? Tier = null, string? Summary = null, List<string>? Optional = null);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(WindowsCatalog.CatalogFile))]
internal sealed partial class WindowsCatalogJsonContext : JsonSerializerContext;
