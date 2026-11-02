using System.Globalization;
using System.Security;
using System.Text;

namespace Mesura.Core.Nvidia;

/// <summary>
/// Mesura's second NVIDIA profile, "gaming" (0.5): the main profile's three settings (<see cref="MesuraNvidiaProfile.Settings"/>, unchanged) plus the driver settings the owner asked
/// to be evaluated: Vertical Sync, the pre-rendered frame limit and the shader cache size. Every id, every value and every default below is in NVIDIA's public header
/// (<c>NvApiDriverSettings.h</c>, read 2026-11-01); a setting the header doesn't list, or whose value would be a guess, is not here. The main profile is not touched: its bytes and its
/// SHA-256 are as they were.
/// <para>
/// What was left out and why: <b>Low Latency Mode</b> (the Ultra setting) is not in the public header, so its id is unverified; <b>a shader cache size in megabytes</b> would need the
/// unit, which the header does not state, so the value is the header's own maximum, which Profile Inspector shows as "Unlimited" (to be confirmed in its window on an NVIDIA PC).
/// </para>
/// <para>
/// None of this was imported into a driver: no NVIDIA GPU was available. The effect on frame time or input lag is not measured and is not claimed.
/// </para>
/// </summary>
public static class GamingNvidiaProfile
{
    public const string ProfileId = "mesura-gaming";
    public const string ResetProfileId = "mesura-gaming-off";

    /// <summary><c>VSYNCMODE_ID</c>, "Vertical Sync".</summary>
    public const uint VerticalSync = 0x00A879CF;

    /// <summary><c>PRERENDERLIMIT_ID</c>, "Maximum pre-rendered frames".</summary>
    public const uint PreRenderLimit = 0x007BA09E;

    /// <summary><c>PS_SHADERDISKCACHE_MAX_SIZE_ID</c>, "Shader disk cache maximum size".</summary>
    public const uint ShaderCacheMaxSize = 0x00AC8497;

    // Values, from the header's enumerations.
    private const uint VsyncForceOff = 0x08416747; // VSYNCMODE_FORCEOFF
    private const uint VsyncPassive = 0x60925292; // VSYNCMODE_PASSIVE, the default ("use the 3D application setting")
    private const uint PreRenderOne = 1; // PRERENDERLIMIT: 0 application controlled (the default), 1 to 255 a number of frames
    private const uint PreRenderApp = 0; // PRERENDERLIMIT_APP_CONTROLLED
    private const uint ShaderCacheMax = 0xFFFFFFFF; // PS_SHADERDISKCACHE_MAX_SIZE_MAX
    private const uint ShaderCacheDefault = 0x4000; // PS_SHADERDISKCACHE_MAX_SIZE_DEFAULT

    /// <summary>What the gaming profile adds to the main profile.</summary>
    public static IReadOnlyList<NvidiaProfileSetting> Extras { get; } =
    [
        new(VerticalSync, "Vertical Sync", VsyncForceOff, "Force off (VSYNCMODE_FORCEOFF)",
            "Turns the driver's vertical sync off for every game, so a frame is shown as soon as it is ready instead of waiting for the next refresh: the lowest delay between input and picture the driver can give.",
            "Tearing (a visible seam) whenever the frame rate is not matched to the refresh rate; on a variable refresh display the usual advice differs (sync on with a frame limit), so this is a choice of lowest delay over a clean picture. A game's own V-Sync setting still applies where the game forces it."),
        new(PreRenderLimit, "Maximum pre-rendered frames", PreRenderOne, "1 frame (PRERENDERLIMIT = 1)",
            "Lets the CPU run only one frame ahead of the GPU, so what is on screen reflects input from less far back (NVIDIA's help calls this the frame queue).",
            "Less buffering: frame rate can drop or stutter more when the CPU is the limit or its frame time varies. Some games ignore the driver's limit (many DirectX 12 and Vulkan games manage their own queue; not verified here), so the effect varies and is not measured."),
        new(ShaderCacheMaxSize, "Shader disk cache maximum size", ShaderCacheMax, "The header's maximum (PS_SHADERDISKCACHE_MAX_SIZE_MAX), shown by Profile Inspector as Unlimited",
            "Stops the driver from throwing compiled shaders away to stay under a size limit, so a game does not recompile them (a cause of stutter) after the cache fills.",
            "The cache can grow without a limit on the system drive; it can be cleared in Windows' Disk Cleanup. Whether it removes stutter depends on the game."),
    ];

    /// <summary>The gaming profile's settings: the main profile's, then the extras.</summary>
    public static IReadOnlyList<NvidiaProfileSetting> Settings { get; } = [.. MesuraNvidiaProfile.Settings, .. Extras];

    /// <summary>
    /// The three extras at the driver's own defaults (from the header): importing it puts them back after the gaming profile, without touching the main profile's settings.
    /// It sets values explicitly; the driver stores them as set, which behave as its defaults.
    /// </summary>
    public static IReadOnlyList<NvidiaProfileSetting> ResetSettings { get; } =
    [
        new(VerticalSync, "Vertical Sync", VsyncPassive, "Use the 3D application setting (VSYNCMODE_PASSIVE, the driver default)",
            "Puts Vertical Sync back to the driver's default.", "None."),
        new(PreRenderLimit, "Maximum pre-rendered frames", PreRenderApp, "Application controlled (PRERENDERLIMIT_APP_CONTROLLED, the driver default)",
            "Puts the frame queue back to the application's own choice.", "None."),
        new(ShaderCacheMaxSize, "Shader disk cache maximum size", ShaderCacheDefault, "The driver default (PS_SHADERDISKCACHE_MAX_SIZE_DEFAULT)",
            "Puts the shader cache limit back to the driver's default.", "None."),
    ];

    public static byte[] Render() => MesuraNvidiaProfile.Render(Settings);

    public static byte[] RenderReset() => MesuraNvidiaProfile.Render(ResetSettings);
}
