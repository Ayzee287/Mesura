using Mesura.Core.Nvidia;

namespace Mesura.Core.Tests.Nvidia;

/// <summary>
/// The two NVIDIA profiles (0.5): the main profile stays exactly what it was, the gaming profile adds three settings, and the way back resets only those three. The ids, values and
/// defaults are the ones in NVIDIA's public header (read 2026-11-01); nothing here shows the driver accepts them, because no profile was imported (no NVIDIA GPU was available).
/// </summary>
public sealed class GamingProfileTests
{
    [Fact]
    public void The_main_profile_is_byte_for_byte_what_the_owner_approved_and_is_still_the_default()
    {
        // The main profile's hash as committed before the gaming profile existed (computed from commit 6171104); changing the main profile is the owner's decision.
        Assert.Equal("C6D3F93F76A6530B06E0D998ACFE8BD067376C5C3D7A88F77D53F33B45A9DF51", NvidiaProfiles.Designated.Sha256);
        Assert.Equal(NvidiaProfiles.Designated.Bytes(), MesuraNvidiaProfile.Render(MesuraNvidiaProfile.Settings));
        Assert.Equal(NvidiaProfiles.Designated.Id, NvidiaDriverConfig.Default.Profile!.Id);
        Assert.Equal(3, MesuraNvidiaProfile.Settings.Count);
    }

    [Fact]
    public void The_gaming_profile_is_the_main_settings_plus_three_header_settings_with_their_header_values()
    {
        var gaming = GamingNvidiaProfile.Settings;

        Assert.Equal(MesuraNvidiaProfile.Settings, gaming.Take(3)); // the main profile's settings, unchanged and first
        Assert.Equal(
            [(0x00A879CFu, 0x08416747u), (0x007BA09Eu, 1u), (0x00AC8497u, 0xFFFFFFFFu)],
            gaming.Skip(3).Select(s => (s.Id, s.Value)));
        Assert.All(GamingNvidiaProfile.Extras, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Why));
            Assert.False(string.IsNullOrWhiteSpace(s.Consequence));
            Assert.False(s.DesktopOnly);
        });
        Assert.Equal(gaming.Count, gaming.Select(s => s.Id).Distinct().Count());
    }

    [Fact]
    public void The_way_back_resets_only_the_three_extras_to_the_headers_defaults_and_never_the_main_settings()
    {
        var reset = GamingNvidiaProfile.ResetSettings;

        Assert.Equal(GamingNvidiaProfile.Extras.Select(s => s.Id), reset.Select(s => s.Id));
        Assert.Equal([0x60925292u, 0u, 0x4000u], reset.Select(s => s.Value)); // VSYNCMODE_PASSIVE, PRERENDERLIMIT_APP_CONTROLLED, PS_SHADERDISKCACHE_MAX_SIZE_DEFAULT
        Assert.DoesNotContain(reset, s => MesuraNvidiaProfile.Settings.Any(m => m.Id == s.Id));
    }

    [Fact]
    public void Each_profile_renders_as_a_profile_inspector_file_with_a_stable_hash_and_loads_by_its_reference()
    {
        foreach (var profile in new[] { NvidiaProfiles.Designated, NvidiaProfiles.Gaming, NvidiaProfiles.GamingOff })
        {
            Assert.Equal(profile.Bytes(), profile.Bytes());
            Assert.Equal(profile.Sha256, profile.Sha256);
            var (bytes, error) = NvidiaProfiles.Load(new NvidiaProfileRef(profile.Id, profile.Sha256));
            Assert.Null(error);
            var parsed = NipFile.Parse(NipFile.Decode(bytes!));
            Assert.Equal("Base Profile", Assert.Single(parsed).Name); // all in the Base Profile; no application profile is created
        }

        Assert.Equal(6, NipFile.Parse(NipFile.Decode(NvidiaProfiles.Gaming.Bytes())).Single().Settings.Count);
        Assert.Equal(3, NipFile.Parse(NipFile.Decode(NvidiaProfiles.GamingOff.Bytes())).Single().Settings.Count);
        Assert.Equal(3, NvidiaProfiles.All.Select(p => p.Sha256).Distinct().Count());
        Assert.Equal(3, NvidiaProfiles.All.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public void The_gaming_profile_is_never_what_a_new_configuration_picks()
    {
        Assert.NotEqual(NvidiaProfiles.Gaming.Id, NvidiaDriverConfig.Default.Profile!.Id);
        Assert.NotEqual(NvidiaProfiles.Gaming.Sha256, NvidiaDriverConfig.Default.Profile!.Sha256);
    }
}
