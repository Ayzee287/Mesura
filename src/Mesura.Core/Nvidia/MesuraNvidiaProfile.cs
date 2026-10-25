using System.Globalization;
using System.Security;
using System.Text;

namespace Mesura.Core.Nvidia;

/// <summary>
/// One driver setting of Mesura's own NVIDIA profile: the setting NVIDIA's public header (<c>NvApiDriverSettings.h</c>,
/// MIT) lists under <see cref="Name"/>, its id and the value Mesura sets, with why it is set and what it costs. Every
/// setting here is named in that header with its value; a setting the header doesn't list is never part of it.
/// </summary>
/// <param name="ValueMeaning">The header's name for the value, or what NVIDIA's Control Panel help calls it.</param>
/// <param name="DesktopOnly">Left out on a PC that has (or may have) a battery, where it would cost battery life.</param>
public sealed record NvidiaProfileSetting(uint Id, string Name, uint Value, string ValueMeaning, string Why, string Consequence, bool DesktopOnly = false)
{
    public string IdHex => $"0x{Id:X8}";
}

/// <summary>
/// Mesura's own NVIDIA profile (D036), written from NVIDIA's documented driver settings and not taken from anyone's
/// profile file: a small set, all in the Base Profile (global), each with its reason and its cost, and nothing the public
/// header doesn't list. It is rendered here as a Profile Inspector <c>.nip</c> file, imported after the driver step with
/// <c>-mergeImport</c>, so the user's other profiles and settings stay as they are.
/// <para>
/// What is left out on purpose: Vertical Sync (forcing it adds latency without a variable refresh display; the driver's
/// "use the application's setting" stays), the pre-rendered frame limit (left to the driver and the games),
/// "Preferred refresh rate" (with variable refresh on, NVIDIA's own help says the control is disabled and the driver uses
/// the highest available rate, so a second setting would only duplicate it), texture filtering, anti-aliasing and
/// anisotropic filtering (image quality is the user's choice), shader cache size (the unit isn't documented), Whisper
/// Mode, NIS and the frame-rate limiter's low-latency state (not in the public header), the OpenGL GPU choice (it names a
/// card) and SLI/AFR flags.
/// </para>
/// </summary>
public static class MesuraNvidiaProfile
{
    public const string ProfileId = "mesura-responsive";

    /// <summary>NVIDIA's <c>PREFERRED_PSTATE_ID</c>, "Power management mode".</summary>
    public const uint PowerManagementMode = 0x1057EB71;

    public static IReadOnlyList<NvidiaProfileSetting> Settings { get; } =
    [
        new(PowerManagementMode, "Power management mode", 1, "Prefer maximum performance (PREFERRED_PSTATE_PREFER_MAX)",
            "Keeps the GPU at its highest performance state while a 3D application runs, so it doesn't spend time raising its clocks when the load changes (NVIDIA: \"Use the GPU only at maximum performance when running most 3D applications\").",
            "Higher idle power, heat and fan noise on a desktop; on a laptop it would cost battery life, so it is left out on a PC that has a battery. The gain varies with the game and isn't measured here.",
            DesktopOnly: true),
        new(0x1094F1F7, "VRR requested state", 2, "G-SYNC for full screen and windowed (VRRREQUESTSTATE_FULLSCREEN_AND_WINDOWED)",
            "Asks the driver to use G-SYNC or G-SYNC Compatible variable refresh for borderless and windowed games as well as exclusive full screen, which is how most current games run (NVIDIA Control Panel, Set up G-SYNC).",
            "Only has an effect on a G-SYNC or G-SYNC Compatible display. In windowed mode only the window in focus is synchronised, so moving content in another window can look uneven. The driver's own default is full screen only."),
        new(0x1194F158, "Enable G-SYNC globally", 2, "Full screen and windowed (VRR_MODE_FULLSCREEN_AND_WINDOWED)",
            "The matching global switch for the setting above: NVIDIA's header lists both (VRRREQUESTSTATE_ID and VRR_MODE_ID) with the same three values, and Profile Inspector exports both, so Mesura sets both to the same value.",
            "The same as above."),
    ];

    /// <summary>The profile as a Profile Inspector <c>.nip</c> file: UTF-16 with a byte-order mark, as the tool writes it.</summary>
    public static byte[] Render()
    {
        var xml = new StringBuilder();
        xml.Append("<?xml version=\"1.0\" encoding=\"utf-16\"?>\r\n<ArrayOfProfile>\r\n  <Profile>\r\n")
            .Append("    <ProfileName>Base Profile</ProfileName>\r\n    <Executeables />\r\n    <Settings>\r\n");
        foreach (var s in Settings)
        {
            xml.Append("      <ProfileSetting>\r\n")
                .Append(CultureInfo.InvariantCulture, $"        <SettingNameInfo>{SecurityElement.Escape(s.Name)}</SettingNameInfo>\r\n")
                .Append(CultureInfo.InvariantCulture, $"        <SettingID>{s.Id}</SettingID>\r\n")
                .Append(CultureInfo.InvariantCulture, $"        <SettingValue>{s.Value}</SettingValue>\r\n")
                .Append("        <ValueType>Dword</ValueType>\r\n      </ProfileSetting>\r\n");
        }

        xml.Append("    </Settings>\r\n  </Profile>\r\n</ArrayOfProfile>");
        return [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(xml.ToString())];
    }
}
