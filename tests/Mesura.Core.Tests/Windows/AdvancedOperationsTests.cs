using Mesura.Core.Profiles;
using Mesura.Core.Windows;

namespace Mesura.Core.Tests.Windows;

/// <summary>
/// The advanced settings of 0.7.0 (D052): the ones that give up protection or data sync and the generic network adapter settings. They are individually selectable choices: a profile
/// holds one only through the owner's allow-list (D054; today SmartScreen off, unticked in Maximum performance), each states its cost, none claims a gain, and each can be put back.
/// Nothing is applied to a real PC here.
/// </summary>
public sealed class AdvancedOperationsTests
{
    private static readonly WindowsCatalog Catalog = WindowsCatalog.Default;

    private static readonly string[] Security =
        ["security.memory-integrity-off", "security.vbs-off", "security.cpu-mitigations-off", "security.hypervisor-off", "security.smartscreen-off", "security.mark-of-the-web-off"];

    private static readonly string[] Background =
    [
        "privacy.no-background-apps", "privacy.no-app-diagnostics", "privacy.location-policy-off", "updates.store-auto-download-off", "updates.driver-search-off",
        "system.onedrive-sync-off", "services.diagnostic-tracking-off", "services.print-spooler-off", "appearance.menu-show-delay-off",
    ];

    private static IEnumerable<string> Advanced => Security.Concat(Background);

    [Fact]
    public void The_two_advanced_collections_hold_exactly_these_settings()
    {
        Assert.Equal(Security, Catalog.PresetById("advanced-security")!.OperationIds);
        Assert.Equal(Background, Catalog.PresetById("advanced-background")!.OperationIds);
        Assert.All(new[] { "advanced-security", "advanced-background" }, id => Assert.Equal(PresetTier.Collection, Catalog.PresetById(id)!.Tier));
    }

    /// <summary>
    /// The owner's allow-list of Advanced settings in profiles (D052 as revised by D054 items 1 and 2): each by id, with its one profile and its role. Nothing
    /// else Advanced is in a profile. 2026-11-26 (owner's review corrections, D059): DiagTrack joins, optional in Maximum; Print Spooler is not added; OneDrive sync
    /// is not used (the owner wants the client removed, which is withheld).
    /// </summary>
    private static readonly (string Operation, string Profile, EntryRole Role)[] AllowList =
        [("services.diagnostic-tracking-off", "maximum-performance", EntryRole.Optional), ("security.smartscreen-off", "maximum-performance", EntryRole.Optional)];

    [Fact]
    public void Advanced_settings_reach_a_profile_only_through_the_owners_allow_list()
    {
        var advanced = Advanced.Concat(Catalog.Operations.Where(o => o.AdapterSettings.Count > 0 && o.Id.StartsWith("network.adapter-", StringComparison.Ordinal)).Select(o => o.Id)).ToHashSet();
        Assert.Equal(Security.Length + Background.Length + 13, advanced.Count); // 6 + 9 + the 13 generic adapter settings (14 until 0.9 retired the IPv6 unbinding, D057)

        var found = OptimizationProfiles.Default.Profiles.SelectMany(p => p.Entries.Where(e => advanced.Contains(e.OperationId)).Select(e => (e.OperationId, p.Id, e.Role))).ToList();
        Assert.Equal(AllowList, found);

        // The primary presets mirror that: the allowed setting is offered unticked in Maximum performance and in no preset's applied list.
        foreach (var preset in Catalog.Presets.Where(p => p.Tier == PresetTier.Primary))
        {
            Assert.All(preset.OperationIds, id => Assert.DoesNotContain(id, advanced));
            Assert.All(preset.OptionalIds.Where(advanced.Contains), id => Assert.Contains(AllowList, a => a.Operation == id && a.Profile == preset.Id));
        }

        // Never in Reference, never as a default: Defender, the firewall, memory integrity, VBS, CPU mitigations, the hypervisor and Mark-of-the-Web in no profile.
        Assert.DoesNotContain(OptimizationProfiles.Default.ById("recommended-gaming")!.Entries, e => e.OperationId.StartsWith("security.", StringComparison.Ordinal));
        Assert.All(new[] { "security.memory-integrity-off", "security.vbs-off", "security.cpu-mitigations-off", "security.hypervisor-off", "security.mark-of-the-web-off" },
            id => Assert.DoesNotContain(OptimizationProfiles.Default.Profiles.SelectMany(p => p.Entries), e => e.OperationId == id));
        Assert.DoesNotContain(OptimizationProfiles.Default.Profiles.SelectMany(p => p.Entries), e => e.OperationId.Contains("defender", StringComparison.OrdinalIgnoreCase)
            || e.OperationId.Contains("firewall", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Every_setting_that_gives_up_protection_or_data_sync_is_high_level_names_its_cost_and_claims_no_gain()
    {
        foreach (var id in Security.Concat(["updates.store-auto-download-off", "system.onedrive-sync-off"]))
        {
            var op = Catalog.ById(id)!;
            Assert.Equal(OperationLevel.High, op.Level);
            Assert.True(op.Applicable);
            Assert.False(string.IsNullOrWhiteSpace(op.Limitations), $"{id} names no cost");
            Assert.NotEqual(OperationBenefit.Measured, op.Benefit); // Mesura measured nothing
            Assert.DoesNotContain("faster", op.Summary, StringComparison.OrdinalIgnoreCase);
        }

        // The ones that cost performance quote third-party tests as such, and say Microsoft publishes no figure.
        Assert.Contains("third-party", Catalog.ById("security.memory-integrity-off")!.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Microsoft publishes no figure", Catalog.ById("security.memory-integrity-off")!.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_setting_that_needs_a_restart_says_so_and_the_security_status_entries_stay_observe_only()
    {
        foreach (var id in new[] { "security.memory-integrity-off", "security.vbs-off", "security.cpu-mitigations-off", "security.hypervisor-off", "services.print-spooler-off", "services.diagnostic-tracking-off" })
        {
            Assert.Equal(Activation.Reboot, Catalog.ById(id)!.Activation);
        }

        Assert.All(Catalog.Operations.Where(o => o.Category == "Security (information)"), o => Assert.Equal(RiskClass.Observe, o.Risk));
    }

    [Fact]
    public void The_hypervisor_is_switched_only_through_the_allowlisted_boot_option_and_only_to_off_or_back_to_the_default()
    {
        var op = Catalog.ById("security.hypervisor-off")!;
        var setting = Assert.Single(op.SystemSettings);
        Assert.Equal(("bcd", "hypervisorlaunchtype", "off"), (setting.Kind, setting.Name, setting.Value));
        Assert.True(SystemSetting.IsAllowed("bcd", "hypervisorlaunchtype", "off"));
        Assert.True(SystemSetting.IsAllowed("bcd", "hypervisorlaunchtype", "absent")); // the undo: delete the element, Windows' default (auto) applies again
        Assert.False(SystemSetting.IsAllowed("bcd", "hypervisorlaunchtype", "disabled"));
        Assert.False(SystemSetting.IsAllowed("bcd", "nx", "alwaysoff")); // DEP is not something Mesura switches
        Assert.False(SystemSetting.IsAllowed("bcd", "bootstatuspolicy", "ignoreallfailures"));
    }

    [Fact]
    public void The_two_print_settings_conflict_so_they_never_run_together()
    {
        var on = Catalog.ById("services.print-on")!;
        var off = Catalog.ById("services.print-spooler-off")!;
        Assert.NotEmpty(WindowsConfigurator.Conflicts([on, off]));
    }
}
