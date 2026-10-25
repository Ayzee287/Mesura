using System.Text.Json;
using System.Text.Json.Serialization;
using Mesura.Core.Apps;
using Mesura.Core.Config;
using Mesura.Core.Nvidia;
using Mesura.Core.Support;
using Mesura.Core.Windows;

namespace Mesura.Core.Setup;

/// <summary>
/// A setup profile: a hand-editable JSON file that says what to set up on a machine — which apps to install (by id or
/// bundle), which config bundle to import (optionally only for some apps) and which Windows configuration operations
/// and presets to apply. Running it reuses the app installer, the config importer and the Windows configurator as
/// they are; the profile adds only the selection and the order (install, import, then Windows settings).
/// </summary>
/// <param name="ConfigBundle">
/// Path of a config bundle (from <c>config export</c>), relative to the profile file's folder, so a profile and its
/// bundle travel together on a USB stick; or <c>null</c> for none.
/// </param>
/// <param name="ConfigApps">Config port ids to import from the bundle; empty means every app in it.</param>
/// <param name="Nvidia">
/// The NVIDIA driver configuration the profile carries (components, NVCleanstall tweaks, interrupt settings and the NVIDIA
/// profile to import, D032), or <c>null</c>. Running the profile in the app makes it the PC's NVIDIA configuration; the
/// NVIDIA steps themselves run from Setup.
/// </param>
public sealed record SetupProfile(
    string Name,
    string Description,
    IReadOnlyList<string> Apps,
    IReadOnlyList<string> Bundles,
    string? ConfigBundle,
    IReadOnlyList<string> ConfigApps,
    IReadOnlyList<string> Windows,
    IReadOnlyList<string> WindowsPresets,
    NvidiaDriverConfig? Nvidia = null)
{
    public const string FileKind = "Mesura.SetupProfile";
    public const int CurrentSchema = 1;

    /// <summary>Every package the profile installs, bundles first then single apps, each once, in that order.</summary>
    public IReadOnlyList<AppPackage> ResolveApps(AppCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return [.. Bundles.SelectMany(b => catalog.Resolve(catalog.BundleById(b)!))
            .Concat(Apps.Select(a => catalog.ById(a)!))
            .DistinctBy(p => p.Id, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The config ports the profile imports, in catalog order.</summary>
    public IReadOnlyList<ConfigPortDefinition> ResolveConfigPorts() =>
        ConfigBundle is null
            ? []
            : [.. ConfigCatalog.All.Where(p => ConfigApps.Count == 0 || ConfigApps.Contains(p.Id, StringComparer.OrdinalIgnoreCase))];

    /// <summary>The Windows operations the profile applies: presets first, then single operations, each once.</summary>
    public IReadOnlyList<WindowsOperation> ResolveWindows(WindowsCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return [.. WindowsPresets.SelectMany(p => catalog.Resolve(catalog.PresetById(p)!))
            .Concat(Windows.Select(id => catalog.ById(id)!))
            .DistinctBy(o => o.Id, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// A profile of this machine as it is: the catalog apps that are installed (manual entries and apps that can't be
    /// detected are left out), the Windows operations that are fully applied, and the config bundle saved beside it.
    /// </summary>
    /// <param name="configBundle">The bundle's file name next to the profile, or <c>null</c> when no settings were exported.</param>
    /// <param name="presets">
    /// Presets to record by name (the outcome the user chose); their operations aren't listed again one by one.
    /// </param>
    public static SetupProfile FromMachine(
        string name, string description, IEnumerable<AppPlanItem> apps, IEnumerable<OperationEvaluation> windows, string? configBundle,
        IReadOnlyList<WindowsPreset>? presets = null, NvidiaDriverConfig? nvidia = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(apps);
        ArgumentNullException.ThrowIfNull(windows);
        var covered = (presets ?? []).SelectMany(p => p.OperationIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new SetupProfile(
            name,
            description ?? "",
            [.. apps.Where(a => a.State == InstallState.Installed && a.Package.Source != AppSource.Manual).Select(a => a.Package.Id)],
            [],
            configBundle,
            [],
            [.. windows.Where(w => w.Operation.Applicable && w.State == OperationState.Applied && !covered.Contains(w.Operation.Id) && !AtWindowsDefault(w))
                .Select(w => w.Operation.Id)],
            [.. (presets ?? []).Select(p => p.Id)],
            nvidia is null ? null : NvidiaDriverConfig.Normalize(nvidia));
    }

    /// <summary>
    /// Whether an applied operation is only Windows as it ships rather than a choice: an entry that puts a Windows default
    /// back, or one whose registry values are all absent and count as their default. A saved profile leaves these out,
    /// so it carries what this PC changed (and stays readable by older builds that don't know the newer default entries).
    /// </summary>
    public static bool AtWindowsDefault(OperationEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        var op = evaluation.Operation;
        if (op.Category == WindowsCatalog.DefaultsCategory)
        {
            return true;
        }

        return op.Registry.Count > 0 && op.Services.Count == 0 && op.Features.Count == 0 && op.Tasks.Count == 0
            && op.PowerPlan is null && op.PowerSettings.Count == 0
            && op.Registry.All(r => r.AbsentMatches && evaluation.Items.Any(i => i.Target == r.Target && i.Current == WindowsEvaluator.NotSet));
    }

    /// <summary>
    /// Whether a preset is in place on this PC: each of its operations is applied or doesn't apply here (a desktop power
    /// setting on a laptop), and at least one is applied.
    /// </summary>
    public static bool PresetSatisfied(WindowsPreset preset, IEnumerable<OperationEvaluation> windows)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(windows);
        var states = windows.GroupBy(w => w.Operation.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().State, StringComparer.OrdinalIgnoreCase);
        OperationState? State(string id) => states.TryGetValue(id, out var s) ? s : null;
        return preset.OperationIds.Count > 0
            && preset.OperationIds.All(id => State(id) is OperationState.Applied or OperationState.Inapplicable)
            && preset.OperationIds.Any(id => State(id) is OperationState.Applied);
    }

    /// <summary>The profile as a hand-editable file, which <see cref="Parse"/> reads back.</summary>
    public string ToJson() => JsonSerializer.Serialize(
        new ProfileFile(FileKind, CurrentSchema, Name, Description.Length > 0 ? Description : null,
            Apps.Count > 0 ? [.. Apps] : null, Bundles.Count > 0 ? [.. Bundles] : null, ConfigBundle,
            ConfigApps.Count > 0 ? [.. ConfigApps] : null, Windows.Count > 0 ? [.. Windows] : null,
            WindowsPresets.Count > 0 ? [.. WindowsPresets] : null,
            Nvidia is { } nvidia
                ? new NvidiaEntry([.. nvidia.Components], [.. nvidia.Tweaks], nvidia.InterruptPolicy, nvidia.InterruptPriority,
                    nvidia.Profile is { } profile ? new ProfileRefEntry(profile.Id, profile.Sha256) : null)
                : null),
        ProfileJsonContext.Default.ProfileFile);

    /// <summary>Whether the profile selects anything at all (a profile that doesn't can't be read back).</summary>
    public bool SelectsAnything =>
        Apps.Count > 0 || Bundles.Count > 0 || ConfigBundle is not null || Windows.Count > 0 || WindowsPresets.Count > 0 || Nvidia is not null;

    /// <summary>Reads and validates a profile against the app catalog, the config ports and the Windows catalog.</summary>
    /// <exception cref="FormatException">Not a profile, a newer schema, or it names an unknown app, bundle or port.</exception>
    public static SetupProfile Parse(string json, AppCatalog catalog, WindowsCatalog? windowsCatalog = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ArgumentNullException.ThrowIfNull(catalog);
        windowsCatalog ??= WindowsCatalog.Default;

        ProfileFile? file;
        try
        {
            file = JsonSerializer.Deserialize(json, ProfileJsonContext.Default.ProfileFile);
        }
        catch (JsonException e)
        {
            throw new FormatException($"The profile is not valid JSON: {e.Message}", e);
        }

        if (file is null || !LegacyIdentity.IsKind(file.Kind, FileKind))
        {
            throw new FormatException($"This file is not a setup profile (expected kind '{FileKind}').");
        }

        if (file.SchemaVersion > CurrentSchema)
        {
            throw new FormatException($"The profile uses schema {file.SchemaVersion}; this build reads up to {CurrentSchema}.");
        }

        if (string.IsNullOrWhiteSpace(file.Name))
        {
            throw new FormatException("The profile needs a 'name'.");
        }

        var apps = file.Apps ?? [];
        var bundles = file.Bundles ?? [];
        var configApps = file.ConfigApps ?? [];
        var windows = file.Windows ?? [];
        var windowsPresets = file.WindowsPresets ?? [];
        if (windows.FirstOrDefault(w => windowsCatalog.ById(w) is null) is { } badOp)
        {
            throw new FormatException($"The profile names Windows operation '{badOp}', which is not in the Windows catalog.");
        }

        // A guided or manual operation is allowed: a profile saved before the catalog turned an operation into one
        // (taskbar.hide-widgets, 2026-10-22) still loads, and its run shows that operation as guided, changing nothing.
        if (windowsPresets.FirstOrDefault(w => windowsCatalog.PresetById(w) is null) is { } badPreset)
        {
            throw new FormatException($"The profile names Windows preset '{badPreset}', which is not in the Windows catalog.");
        }
        if (apps.FirstOrDefault(a => catalog.ById(a) is null) is { } badApp)
        {
            throw new FormatException($"The profile names app '{badApp}', which is not in the app catalog.");
        }

        if (bundles.FirstOrDefault(b => catalog.BundleById(b) is null) is { } badBundle)
        {
            throw new FormatException($"The profile names bundle '{badBundle}', which is not in the app catalog.");
        }

        if (configApps.FirstOrDefault(c => ConfigCatalog.ById(c) is null) is { } badPort)
        {
            throw new FormatException($"The profile names config app '{badPort}', which has no config support.");
        }

        var bundlePath = string.IsNullOrWhiteSpace(file.ConfigBundle) ? null : file.ConfigBundle;
        if (bundlePath is not null && (Path.IsPathRooted(bundlePath) || bundlePath.Contains(':', StringComparison.Ordinal)))
        {
            throw new FormatException("'configBundle' must be a path relative to the profile's folder, so the profile stays portable.");
        }

        var nvidia = ReadNvidia(file.Nvidia);
        if (apps.Count == 0 && bundles.Count == 0 && bundlePath is null && windows.Count == 0 && windowsPresets.Count == 0 && nvidia is null)
        {
            throw new FormatException("The profile selects nothing: give it 'apps', 'bundles', a 'configBundle', 'windows', 'windowsPresets' or 'nvidia'.");
        }

        return new SetupProfile(file.Name, file.Description ?? "", apps, bundles, bundlePath, configApps, windows, windowsPresets, nvidia);
    }

    /// <summary>
    /// A profile's NVIDIA block, checked: known components and tweaks only, and an NVIDIA profile this build carries with
    /// the same SHA-256 (a profile never switches silently to another file).
    /// </summary>
    private static NvidiaDriverConfig? ReadNvidia(NvidiaEntry? entry)
    {
        if (entry is null)
        {
            return null;
        }

        NvidiaProfileRef? reference = null;
        if (entry.Profile is { } named)
        {
            if (string.IsNullOrWhiteSpace(named.Id) || NvidiaProfiles.ById(named.Id) is not { } builtIn)
            {
                throw new FormatException($"The profile names NVIDIA profile '{named.Id}', which this build doesn't carry.");
            }

            if (!string.Equals(named.Sha256, builtIn.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException($"The profile's NVIDIA profile '{named.Id}' isn't the file this build carries (its SHA-256 differs).");
            }

            reference = new NvidiaProfileRef(builtIn.Id, builtIn.Sha256);
        }

        var config = new NvidiaDriverConfig(entry.Components ?? [], entry.Tweaks ?? [], entry.InterruptPolicy ?? "", entry.InterruptPriority ?? "", reference);
        if (config.UnknownIds() is { Count: > 0 } unknown)
        {
            throw new FormatException($"The profile's NVIDIA configuration names '{unknown[0]}', which isn't an NVCleanstall component or tweak Mesura knows.");
        }

        return NvidiaDriverConfig.Normalize(config);
    }

    internal sealed record ProfileFile(
        string? Kind, int SchemaVersion, string? Name, string? Description,
        List<string>? Apps, List<string>? Bundles, string? ConfigBundle, List<string>? ConfigApps,
        List<string>? Windows, List<string>? WindowsPresets, NvidiaEntry? Nvidia = null);

    internal sealed record NvidiaEntry(List<string>? Components, List<string>? Tweaks, string? InterruptPolicy, string? InterruptPriority, ProfileRefEntry? Profile);

    internal sealed record ProfileRefEntry(string? Id, string? Sha256);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
    WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SetupProfile.ProfileFile))]
internal sealed partial class ProfileJsonContext : JsonSerializerContext;
