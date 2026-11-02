using Mesura.Core.Nvidia;

namespace Mesura.Core.Tests.Nvidia;

/// <summary>
/// The official NVIDIA installer route's planning (0.5). The package names below are the ones the 621.01 GeForce package declares (read statically from its
/// .nvi files and setup.cfg on 2026-11-01); the plan is pure, so these pin how a configuration becomes a command line. They do not show that NVIDIA's installer
/// accepts that command line on a GeForce package: nothing was installed (no NVIDIA GPU was available).
/// </summary>
public sealed class OfficialInstallTests
{
    private static readonly string[] Package621 =
    [
        "Display.Driver", "Display.NvApp", "Display.NvApp.MessageBus", "Display.NvApp.NvBackend", "Display.NvApp.NvCPL", "Display.NVWMI", "Display.PhysX", "FrameViewSdk", "HDAudio.Driver",
        "MSVCRuntime2017", "MSVCRuntime2019", "NvContainer", "NvContainer.AIUser", "NvContainer.LocalSystem", "NvContainer.Session", "NvContainer.User", "NvDLISR", "NVPCF", "NvPlugin.Watchdog",
        "NvTelemetry", "ShadowPlay", "USBC", "VirtualAudio.Driver",
    ];

    [Fact]
    public void The_owners_configuration_installs_the_display_driver_and_hd_audio_only_with_a_clean_install()
    {
        var plan = OfficialInstall.Plan(NvidiaDriverConfig.Default, Package621, @"C:\logs\nvidia.log");

        Assert.Equal(["Display.Driver", "HDAudio.Driver"], plan.Install);
        Assert.Equal(["-s", "-noreboot", "-noeula", "-nofinish", "-nosplash", "-clean", @"-log:C:\logs\nvidia.log", "Display.Driver", "HDAudio.Driver"], plan.Arguments);
        Assert.True(plan.HasDisplayDriver);
        Assert.Contains("Display.NvApp", plan.LeftOut);
        Assert.Contains("ShadowPlay", plan.LeftOut);
        Assert.DoesNotContain("Display.Driver", plan.LeftOut);
        Assert.Empty(plan.ComponentsNotInThisPackage);
    }

    [Fact]
    public void A_clean_install_is_passed_only_when_the_configuration_asks_for_it()
    {
        var noClean = NvidiaDriverConfig.Default with { Tweaks = [.. NvidiaDriverConfig.Default.Tweaks.Where(t => t != NvidiaTweaks.CleanInstall)] };

        Assert.DoesNotContain("-clean", OfficialInstall.Plan(noClean, Package621, "").Arguments);
        Assert.DoesNotContain(OfficialInstall.Plan(noClean, Package621, "").Arguments, a => a.StartsWith("-log", StringComparison.Ordinal)); // no log path, no log switch
    }

    [Fact]
    public void A_component_picks_up_every_package_that_makes_it_up_and_only_those_in_this_package()
    {
        var config = NvidiaDriverConfig.Default with { Components = [NvidiaComponents.DisplayDriver, "nvidia-app", "nv-container", "vcredist-2017", "legacy-control-panel", "quadro-view"] };

        var plan = OfficialInstall.Plan(config, Package621, "");

        Assert.Contains("Display.NvApp", plan.Install);
        Assert.Contains("Display.NvApp.NvCPL", plan.Install);
        Assert.Contains("NvPlugin.Watchdog", plan.Install);
        Assert.Contains("MSVCRuntime2019", plan.Install);
        Assert.Contains("MSVCRuntime2017", plan.Install);
        Assert.Equal(["legacy-control-panel", "quadro-view"], plan.ComponentsNotInThisPackage); // not in 621.01: said so, never invented
        Assert.Equal(plan.Install.Count, plan.Install.Distinct().Count());
    }

    [Fact]
    public void A_package_without_the_display_driver_is_not_a_plan_that_can_run()
    {
        var plan = OfficialInstall.Plan(NvidiaDriverConfig.Default, ["HDAudio.Driver", "PhysX"], "");

        Assert.False(plan.HasDisplayDriver);
        Assert.Contains(NvidiaComponents.DisplayDriver, plan.ComponentsNotInThisPackage);
    }

    [Fact]
    public void Every_component_the_configuration_can_name_has_a_mapping_and_every_mapped_package_looks_like_a_package_name()
    {
        Assert.All(NvidiaComponents.All, c => Assert.True(OfficialInstall.Packages.ContainsKey(c.Id), c.Id));
        Assert.All(OfficialInstall.Packages.Values.SelectMany(v => v), name => Assert.Matches(@"^[A-Za-z0-9_.]+$", name));
    }

    [Theory]
    [InlineData("<?xml version=\"1.0\"?>\r\n<nvi\r\n  name      = \"Display.PhysX\"\r\n  title = \"${{title}}\">", "Display.PhysX")]
    [InlineData("<nvi name=\"HDAudio.Driver\" title=\"x\">", "HDAudio.Driver")]
    [InlineData("<nvi name=\"${{PackageId}}\" title=\"x\">", null)] // a template: named by the cfg instead
    [InlineData("<setup name=\"x\">", null)]
    [InlineData("", null)]
    public void A_package_name_is_read_from_the_nvi_file_and_templates_are_skipped(string text, string? expected) =>
        Assert.Equal(expected, OfficialInstall.PackageName(text));

    [Theory]
    [InlineData(0, OfficialInstallOutcome.Installed)]
    [InlineData(1, OfficialInstallOutcome.InstalledRestartNeeded)]
    [InlineData(2, OfficialInstallOutcome.Failed)]
    [InlineData(-1, OfficialInstallOutcome.Failed)]
    [InlineData(3010, OfficialInstallOutcome.Failed)] // not an NVIDIA code: a failure is never inferred to be a success
    public void The_exit_code_is_read_as_nvidia_documents_it_and_anything_else_is_a_failure(int code, OfficialInstallOutcome expected) =>
        Assert.Equal(expected, OfficialInstall.Outcome(code));
}
