using Mesura.Core.Nvidia;

namespace Mesura.Core.Tests.Nvidia;

/// <summary>
/// The NVIDIA pieces that read data without any I/O (D031): driver version numbers, NVIDIA's lookup answer, the component
/// defaults and the profile files a backup is checked with.
/// </summary>
public sealed class NvidiaTests
{
    [Theory]
    [InlineData("32.0.15.6094", "560.94")]
    [InlineData("31.0.15.5222", "552.22")]
    [InlineData("32.0.16.2101", "621.01")]
    [InlineData("32.0.15.94", "500.94")] // a short fourth field is zero-padded
    public void Windows_driver_versions_read_as_nvidia_numbers_them(string windows, string release) =>
        Assert.Equal(release, NvidiaDriver.ToRelease(windows));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("32.0.101.7088x")]
    [InlineData("32.0.15")]
    [InlineData("32.0.15.60940")]
    public void Anything_else_isnt_a_driver_number(string? windows) => Assert.Null(NvidiaDriver.ToRelease(windows));

    [Fact]
    public void Newer_compares_release_numbers()
    {
        Assert.True(NvidiaDriver.IsNewer("621.01", "560.94"));
        Assert.False(NvidiaDriver.IsNewer("560.94", "560.94"));
        Assert.False(NvidiaDriver.IsNewer("552.22", "560.94"));
        Assert.True(NvidiaDriver.IsNewer("621.01", null)); // nothing read: any release is news
        Assert.False(NvidiaDriver.IsNewer(null, "560.94"));
    }

    [Fact]
    public void The_lookup_asks_by_device_id_for_the_windows_version()
    {
        var gpu = new NvidiaGpu("NVIDIA GeForce RTX 4070", 0x2786, "32.0.15.6094");
        Assert.Equal("2786", gpu.DeviceHex);
        Assert.Equal("560.94", gpu.Release);
        var eleven = NvidiaDriver.LookupUri(gpu, 26200).ToString();
        Assert.Contains("deviceID=2786", eleven, StringComparison.Ordinal);
        Assert.Contains("osID=135", eleven, StringComparison.Ordinal);
        Assert.Contains("isWHQL=1", eleven, StringComparison.Ordinal);
        Assert.Contains("dch=1", eleven, StringComparison.Ordinal);
        Assert.Contains("osID=57", NvidiaDriver.LookupUri(gpu, 19045).ToString(), StringComparison.Ordinal);
    }

    /// <summary>The shape NVIDIA's service answered with on 2026-10-23 (shortened).</summary>
    private const string Answer = """
        { "Success" : "1", "IDS" : [{ "downloadInfo": { "Success" : "1", "ID" : "282412",
          "Name" : "GeForce%20Game%20Ready%20Driver", "Version" : "621.01", "IsWHQL" : "1",
          "ReleaseDateTime" : "Wed Oct 21, 2026", "DetailsURL" : "https://www.nvidia.com/en-us/drivers/details/282412/",
          "DownloadURL" : "https://us.download.nvidia.com/Windows/621.01/621.01-desktop-win10-win11-64bit-international-dch-whql.exe",
          "DownloadURLFileSize" : "1.04 GB" } }] }
        """;

    [Fact]
    public void A_lookup_answer_gives_the_release()
    {
        var release = NvidiaDriver.ParseLookup(Answer);
        Assert.NotNull(release);
        Assert.Equal("621.01", release.Version);
        Assert.Equal("GeForce Game Ready Driver", release.Name);
        Assert.Equal("Wed Oct 21, 2026", release.ReleaseDate);
        Assert.Equal("https://www.nvidia.com/en-us/drivers/details/282412/", release.DetailsUrl);
        Assert.Equal("1.04 GB", release.Size);
    }

    [Fact]
    public void No_driver_a_strange_version_or_a_foreign_link_is_not_taken()
    {
        Assert.Null(NvidiaDriver.ParseLookup("""
            { "Success" : "0", "IDS" : [{ "downloadInfo": { "Success" : "0", "Messaging" : [{"MessageCode" : "DriverDownloadIDNotFound"}] } }] }
            """));
        Assert.Null(NvidiaDriver.ParseLookup("not json"));
        Assert.Null(NvidiaDriver.ParseLookup(Answer.Replace("621.01\"", "latest\"", StringComparison.Ordinal)));

        // A link that doesn't go to nvidia.com over https is dropped rather than opened.
        var release = NvidiaDriver.ParseLookup(Answer.Replace("https://www.nvidia.com/en-us/drivers/details/282412/", "http://example.com/x", StringComparison.Ordinal));
        Assert.Equal("", release!.DetailsUrl);
    }

    [Fact]
    public void The_default_configuration_is_the_owners_nvcleanstall_selection_exactly()
    {
        var config = NvidiaDriverConfig.Default;
        Assert.Equal(["Display Driver", "HD Audio via HDMI"], config.Components.Select(id => NvidiaComponents.ById(id)!.Label));
        Assert.Equal(
            ["Legacy Control Panel", "Microsoft Visual C++ 2017 Runtimes", "PhysX", "USB-C Driver", "FrameView SDK", "Quadro View", "Virtual Audio",
             "Telemetry", "NV Platform Controllers", "NVIDIA DLISR", "NVIDIA App", "NV Container", "ShadowPlay", "NV Backend", "NVIDIA App MessageBus"],
            NvidiaComponents.All.Where(c => !config.Components.Contains(c.Id)).Select(c => c.Label));
        Assert.Equal(
            ["Disable Installer Telemetry & Advertising", "Perform a Clean Installation", "Disable Multiplane Overlay (MPO)", "Show Expert Tweaks",
             "Disable Driver Telemetry (Experimental)", "Disable NVIDIA HD Audio device sleep timer", "Enable Message Signed Interrupts (MSI)",
             "Disable HDCP", "Rebuild digital signature", "Use method compatible with Easy-Anti-Cheat",
             "Automatically accept the \"driver unsigned\" warning"],
            config.Tweaks.Select(id => NvidiaTweaks.ById(id)!.Label));
        Assert.Equal(
            ["Unattended Express Installation", "Add Hardware Support", "Enable DLSS Indicator", "Disable Ansel", "Disable NVIDIA Container",
             "Apply NVENC Video Encoding Session Limit Patch", "Start external application"],
            NvidiaTweaks.All.Where(t => !config.Tweaks.Contains(t.Id)).Select(t => t.Label));
        Assert.Equal("Default", config.InterruptPolicy);
        Assert.Equal("Default", config.InterruptPriority);
        Assert.True(config.CleanInstall);
        Assert.Equal(NvidiaProfiles.Designated.Id, config.Profile!.Id);
        Assert.True(config.IsDefault);
    }

    [Fact]
    public void A_saved_configuration_keeps_known_ids_in_order_and_always_the_driver()
    {
        var normal = NvidiaDriverConfig.Normalize(new NvidiaDriverConfig(["physx", "HD-AUDIO", "made-up"], ["msi", "nope", "disable-hdcp"], "", " ", null));
        Assert.Equal(["display-driver", "hd-audio", "physx"], normal.Components);
        Assert.Equal(["msi", "disable-hdcp"], normal.Tweaks);
        Assert.Equal("Default", normal.InterruptPolicy);
        Assert.Equal("Default", normal.InterruptPriority);
        Assert.False(normal.IsDefault);
        Assert.Same(NvidiaDriverConfig.Default, NvidiaDriverConfig.Normalize(null));
        Assert.Equal(["made-up", "nope"], new NvidiaDriverConfig(["made-up"], ["nope"], "", "", null).UnknownIds());
    }

    [Fact]
    public void Mesuras_profile_holds_exactly_its_documented_settings_and_a_wrong_hash_is_refused()
    {
        var reference = NvidiaDriverConfig.Default.Profile!;
        var (bytes, error) = NvidiaProfiles.Load(reference);
        Assert.Null(error);
        var profiles = NipFile.Parse(NipFile.Decode(bytes!));
        var global = Assert.Single(profiles);
        Assert.True(global.IsGlobal);
        Assert.Empty(global.Executables); // Base Profile only: every application, no game profile
        Assert.Equal(
            [(0x1057EB71u, "1"), (0x1094F1F7u, "2"), (0x1194F158u, "2")],
            global.Settings.Select(s => (s.Id, s.Value)));
        Assert.All(global.Settings, s => Assert.Equal("Dword", s.ValueType));

        // Deterministic, so a configuration can name it by hash; it names no card and no setting NVIDIA's header lacks.
        Assert.Equal(NvidiaProfiles.Designated.Bytes(), NvidiaProfiles.Designated.Bytes());
        Assert.Equal(reference.Sha256, NvidiaProfiles.Designated.Sha256);
        Assert.DoesNotContain("id,2.0:", NipFile.Decode(bytes!), StringComparison.Ordinal);
        Assert.Equal(new byte[] { 0xFF, 0xFE }, bytes![..2]);

        var (none, refused) = NvidiaProfiles.Load(reference with { Sha256 = new string('0', 64) });
        Assert.Null(none);
        Assert.Contains("isn't the file", refused, StringComparison.Ordinal);
        Assert.Null(NvidiaProfiles.Load(new NvidiaProfileRef("someone-else", reference.Sha256)).Bytes);
    }

    [Fact]
    public void Every_setting_says_why_it_is_set_and_what_it_costs_and_only_the_power_mode_is_desktop_only()
    {
        Assert.All(MesuraNvidiaProfile.Settings, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Why), s.Name);
            Assert.False(string.IsNullOrWhiteSpace(s.Consequence), s.Name);
            Assert.False(string.IsNullOrWhiteSpace(s.ValueMeaning), s.Name);
        });
        Assert.Equal([MesuraNvidiaProfile.PowerManagementMode], MesuraNvidiaProfile.Settings.Where(s => s.DesktopOnly).Select(s => s.Id));

        // What is deliberately not in it: Vertical Sync, texture filtering, anti-aliasing, anisotropic filtering, shader cache
        // size, Whisper Mode and the undocumented states, and the OpenGL GPU choice.
        uint[] left = [0x007BA09E, 0x0064B541, 0x00A879CF, 0x005A375C, 0x00638E8F, 0x00AC8497, 0x20D0F3E6, 0x10835000, 0x10115C8A, 0x0005F543, 0x00ABAC31];
        var ids = MesuraNvidiaProfile.Settings.Select(s => s.Id).ToList();
        Assert.All(left, id => Assert.DoesNotContain(id, ids));
    }

    [Fact]
    public void On_a_desktop_the_profile_is_imported_byte_for_byte()
    {
        var original = NvidiaProfiles.Designated.Bytes();
        var desktop = NvidiaProfilePortability.Adapt(original, hasBattery: false);

        Assert.False(desktop.Adjusted);
        Assert.Same(original, desktop.Content);
        Assert.Equal(3, NipFile.SettingCount(desktop.Profiles));
    }

    [Theory]
    [InlineData(true, "has a battery")]
    [InlineData(null, "Couldn't tell")]
    public void On_a_laptop_or_with_the_battery_unread_only_the_power_mode_is_left_out(bool? battery, string why)
    {
        var original = NvidiaProfiles.Designated.Bytes();
        var fitted = NvidiaProfilePortability.Adapt(original, battery);

        var adjustment = Assert.Single(fitted.Adjustments);
        Assert.Equal((MesuraNvidiaProfile.PowerManagementMode, "Power management mode", "1", (string?)null),
            (adjustment.SettingId, adjustment.Setting, adjustment.OriginalShort, adjustment.Imported));
        Assert.Contains(why, adjustment.Reason, StringComparison.Ordinal);

        // Every other setting is imported exactly as the profile has it, and the original is untouched.
        var before = NipFile.Parse(NipFile.Decode(original)).Single().Settings;
        var after = fitted.Profiles.Single().Settings;
        Assert.Equal(before.Where(s => s.Id != MesuraNvidiaProfile.PowerManagementMode), after);
        Assert.Equal(2, after.Count);
        Assert.Equal(new byte[] { 0xFF, 0xFE }, fitted.Content[..2]);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-16\"?>", NipFile.Decode(fitted.Content), StringComparison.Ordinal);
        Assert.Equal(3, NipFile.SettingCount(NipFile.Parse(NipFile.Decode(original)))); // not edited in place
    }

    [Fact]
    public void A_file_that_isnt_a_profile_is_refused_when_fitting()
    {
        Assert.Throws<FormatException>(() => NvidiaProfilePortability.Adapt(System.Text.Encoding.UTF8.GetBytes("not xml"), hasBattery: false));
    }

    /// <summary>A profile file as NVIDIA Profile Inspector exports it: a global profile and a game's.</summary>
    private const string Profiles = """
        <?xml version="1.0" encoding="utf-16"?>
        <ArrayOfProfile>
          <Profile>
            <ProfileName>Base Profile</ProfileName>
            <Executeables />
            <Settings>
              <ProfileSetting><SettingNameInfo>Maximum pre-rendered frames</SettingNameInfo><SettingID>8102046</SettingID><SettingValue>1</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
            </Settings>
          </Profile>
          <Profile>
            <ProfileName>Some Game</ProfileName>
            <Executeables><string>game.exe</string></Executeables>
            <Settings>
              <ProfileSetting><SettingNameInfo /><SettingID>274197361</SettingID><SettingValue>1</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
              <ProfileSetting><SettingNameInfo /><SettingID>11306135</SettingID><SettingValue>10240</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
            </Settings>
          </Profile>
        </ArrayOfProfile>
        """;

    [Fact]
    public void A_profile_file_reads_with_its_profiles_and_settings()
    {
        var profiles = NipFile.Parse(Profiles);
        Assert.Equal(2, profiles.Count);
        Assert.True(profiles[0].IsGlobal);
        Assert.Equal(["game.exe"], profiles[1].Executables);
        Assert.Equal(3, NipFile.SettingCount(profiles));
        Assert.Equal(new NipSetting(8102046, "1", "Dword"), profiles[0].Settings[0]);
    }

    [Fact]
    public void A_backup_is_in_place_only_when_every_setting_reads_back_with_its_value()
    {
        var backup = NipFile.Parse(Profiles);
        Assert.Empty(NipFile.Missing(backup, backup));

        // The readback may hold more (other profiles, other settings); what matters is that nothing backed up is gone.
        var changed = NipFile.Parse(Profiles.Replace("<SettingValue>10240</SettingValue>", "<SettingValue>4096</SettingValue>", StringComparison.Ordinal));
        Assert.Equal(["Some Game: 0x00AC8497"], NipFile.Missing(backup, changed));
        var gone = NipFile.Parse(Profiles.Replace("Some Game", "Other Game", StringComparison.Ordinal));
        Assert.Equal(2, NipFile.Missing(backup, gone).Count);
    }

    [Fact]
    public void Profile_files_decode_from_utf16_with_a_mark_and_from_a_resaved_utf8_copy()
    {
        var utf16 = new byte[] { 0xFF, 0xFE }.Concat(System.Text.Encoding.Unicode.GetBytes(Profiles)).ToArray();
        Assert.Equal(2, NipFile.Parse(NipFile.Decode(utf16)).Count);

        // A copy an editor re-saved as UTF-8 with a mark, still declaring UTF-16 (a profile exported by another tool).
        var utf8 = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(System.Text.Encoding.UTF8.GetBytes(Profiles)).ToArray();
        Assert.Equal(2, NipFile.Parse(NipFile.Decode(utf8)).Count);
    }

    [Fact]
    public void Something_else_is_refused()
    {
        Assert.Throws<FormatException>(() => NipFile.Parse("<Profiles />"));
        Assert.Throws<FormatException>(() => NipFile.Parse("not xml"));
        Assert.Throws<FormatException>(() => NipFile.Parse("""<!DOCTYPE x [<!ENTITY e "boom">]><ArrayOfProfile>&e;</ArrayOfProfile>"""));
    }
}
