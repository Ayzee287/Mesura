namespace Mesura.Core.Nvidia;

/// <summary>One component of an NVIDIA driver package, by the label NVCleanstall shows for it.</summary>
/// <param name="Id">Stable id, kept in the saved configuration and in setup profiles.</param>
/// <param name="Label">NVCleanstall's label, word for word (the user ticks it in NVCleanstall's own window).</param>
/// <param name="Required">The display driver: always installed, never unticked.</param>
public sealed record NvidiaComponent(string Id, string Label, bool Required = false);

/// <summary>One of NVCleanstall's installation tweaks, by its label there.</summary>
public sealed record NvidiaTweak(string Id, string Label);

/// <summary>
/// The NVIDIA driver components NVCleanstall offers, as the owner's configuration names them (2026-10-24, D032).
/// </summary>
public static class NvidiaComponents
{
    public const string DisplayDriver = "display-driver";

    public static IReadOnlyList<NvidiaComponent> All { get; } =
    [
        new(DisplayDriver, "Display Driver", Required: true),
        new("hd-audio", "HD Audio via HDMI"),
        new("legacy-control-panel", "Legacy Control Panel"),
        new("vcredist-2017", "Microsoft Visual C++ 2017 Runtimes"),
        new("physx", "PhysX"),
        new("usb-c", "USB-C Driver"),
        new("frameview", "FrameView SDK"),
        new("quadro-view", "Quadro View"),
        new("virtual-audio", "Virtual Audio"),
        new("telemetry", "Telemetry"),
        new("platform-controllers", "NV Platform Controllers"),
        new("dlisr", "NVIDIA DLISR"),
        new("nvidia-app", "NVIDIA App"),
        new("nv-container", "NV Container"),
        new("shadowplay", "ShadowPlay"),
        new("nv-backend", "NV Backend"),
        new("app-messagebus", "NVIDIA App MessageBus"),
    ];

    public static NvidiaComponent? ById(string id) => All.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>NVCleanstall's installation tweaks (its Tweaks page, with Expert Tweaks shown), as the owner's configuration names them.</summary>
public static class NvidiaTweaks
{
    public const string CleanInstall = "clean-install";

    public static IReadOnlyList<NvidiaTweak> All { get; } =
    [
        new("installer-telemetry", "Disable Installer Telemetry & Advertising"),
        new("unattended", "Unattended Express Installation"),
        new(CleanInstall, "Perform a Clean Installation"),
        new("disable-mpo", "Disable Multiplane Overlay (MPO)"),
        new("show-expert", "Show Expert Tweaks"),
        new("driver-telemetry", "Disable Driver Telemetry (Experimental)"),
        new("hdaudio-sleep", "Disable NVIDIA HD Audio device sleep timer"),
        new("msi", "Enable Message Signed Interrupts (MSI)"),
        new("disable-hdcp", "Disable HDCP"),
        new("add-hardware", "Add Hardware Support"),
        new("dlss-indicator", "Enable DLSS Indicator"),
        new("disable-ansel", "Disable Ansel"),
        new("disable-container", "Disable NVIDIA Container"),
        new("nvenc-patch", "Apply NVENC Video Encoding Session Limit Patch"),
        new("rebuild-signature", "Rebuild digital signature"),
        new("eac-signing", "Use method compatible with Easy-Anti-Cheat"),
        new("accept-unsigned", "Automatically accept the \"driver unsigned\" warning"),
        new("external-app", "Start external application"),
    ];

    public static NvidiaTweak? ById(string id) => All.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>The NVIDIA profile a configuration imports after the driver install: a built-in profile by id, and its SHA-256.</summary>
public sealed record NvidiaProfileRef(string Id, string Sha256);

/// <summary>
/// The reusable NVIDIA driver configuration (D032): the components and NVCleanstall tweaks to tick, the interrupt
/// settings, and the NVIDIA profile imported after the driver install. It is a selection, never a driver version, so it
/// stays valid for every new driver. Saved in Mesura's settings once changed, and carried by setup profiles.
/// </summary>
/// <param name="Components">The components kept (ids from <see cref="NvidiaComponents"/>); the display driver is always among them.</param>
/// <param name="Tweaks">The tweaks ticked (ids from <see cref="NvidiaTweaks"/>); every other tweak is left unticked.</param>
/// <param name="InterruptPolicy">NVCleanstall's Interrupt Policy, as it names the choice.</param>
/// <param name="InterruptPriority">NVCleanstall's Interrupt Priority, as it names the choice.</param>
public sealed record NvidiaDriverConfig(
    IReadOnlyList<string> Components,
    IReadOnlyList<string> Tweaks,
    string InterruptPolicy,
    string InterruptPriority,
    NvidiaProfileRef? Profile)
{
    public const string DefaultChoice = "Default";

    /// <summary>
    /// The owner's NVCleanstall configuration (given 2026-10-24 with screenshots of NVCleanstall, the source of truth):
    /// the display driver and HD Audio via HDMI kept, every other component left out; the tweaks below ticked and the
    /// rest unticked; both interrupt settings at Default; Mesura's own NVIDIA profile imported after the install (D036).
    /// </summary>
    public static NvidiaDriverConfig Default { get; } = new(
        [NvidiaComponents.DisplayDriver, "hd-audio"],
        ["installer-telemetry", NvidiaTweaks.CleanInstall, "disable-mpo", "show-expert", "driver-telemetry", "hdaudio-sleep", "msi",
         "disable-hdcp", "rebuild-signature", "eac-signing", "accept-unsigned"],
        DefaultChoice,
        DefaultChoice,
        new NvidiaProfileRef(NvidiaProfiles.Designated.Id, NvidiaProfiles.Designated.Sha256));

    public bool CleanInstall => Tweaks.Contains(NvidiaTweaks.CleanInstall, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The configuration made valid: known ids only, in NVCleanstall's order, each once, the display driver always kept;
    /// empty interrupt settings read as Default. <c>null</c> (none saved yet) gives <see cref="Default"/>.
    /// </summary>
    public static NvidiaDriverConfig Normalize(NvidiaDriverConfig? saved)
    {
        if (saved is null)
        {
            return Default;
        }

        var components = (saved.Components ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tweaks = (saved.Tweaks ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new NvidiaDriverConfig(
            [.. NvidiaComponents.All.Where(c => c.Required || components.Contains(c.Id)).Select(c => c.Id)],
            [.. NvidiaTweaks.All.Where(t => tweaks.Contains(t.Id)).Select(t => t.Id)],
            string.IsNullOrWhiteSpace(saved.InterruptPolicy) ? DefaultChoice : saved.InterruptPolicy,
            string.IsNullOrWhiteSpace(saved.InterruptPriority) ? DefaultChoice : saved.InterruptPriority,
            saved.Profile);
    }

    /// <summary>Whether this is <see cref="Default"/>, the owner's configuration.</summary>
    public bool IsDefault
    {
        get
        {
            var normal = Normalize(this);
            return normal.Components.SequenceEqual(Default.Components, StringComparer.OrdinalIgnoreCase)
                && normal.Tweaks.SequenceEqual(Default.Tweaks, StringComparer.OrdinalIgnoreCase)
                && normal.InterruptPolicy == Default.InterruptPolicy && normal.InterruptPriority == Default.InterruptPriority
                && normal.Profile == Default.Profile;
        }
    }

    /// <summary>The ids in a list that the catalog doesn't know, for a profile file's validation.</summary>
    public IReadOnlyList<string> UnknownIds() =>
        [.. (Components ?? []).Where(id => NvidiaComponents.ById(id) is null)
            .Concat((Tweaks ?? []).Where(id => NvidiaTweaks.ById(id) is null))];
}
