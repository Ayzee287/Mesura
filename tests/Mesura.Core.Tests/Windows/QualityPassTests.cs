using Mesura.Core.Licensing;
using Mesura.Core.Windows;

namespace Mesura.Core.Tests.Windows;

/// <summary>
/// The 2026-10-24 quality pass: the global TCP setting primitive (netsh, read through Windows' network classes), the
/// network repairs that use it, the IPv6 repair that keeps a preference for IPv4, the network impact, an operation's
/// benefit, conflicts as groups for the app to word, and the network-defaults collection.
/// </summary>
public sealed class QualityPassTests
{
    private static readonly WindowsCatalog Catalog = WindowsCatalog.Default;
    private const string Ipv6 = @"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\DisabledComponents";

    private static WindowsOperation Op(string id) => EngineFixtures.Op(id);

    [Fact]
    public void Tcp_repairs_read_apply_read_back_record_and_revert()
    {
        var pc = new FakeWindows();
        pc.Tcp["autotuninglevel"] = "disabled";
        pc.Tcp["rss"] = "disabled";
        pc.Tcp["rsc"] = "enabled";
        var configurator = new WindowsConfigurator(pc, pc);
        var ops = new[] { Op("manual.restore-tcp-autotuning"), Op("repair.tcp-rss"), Op("repair.tcp-rsc") };

        Assert.Equal([OperationState.NotApplied, OperationState.NotApplied, OperationState.Applied],
            ops.Select(o => WindowsEvaluator.Evaluate(o, pc).State));

        var outcomes = configurator.Apply(ops, TestContext.Current.CancellationToken);
        Assert.Equal([WindowsOpStatus.Applied, WindowsOpStatus.Applied, WindowsOpStatus.AlreadyApplied], outcomes.Select(o => o.Status));
        Assert.Equal("normal", pc.Tcp["autotuninglevel"]);
        Assert.Equal("enabled", pc.Tcp["rss"]);
        var change = Assert.Single(outcomes[0].Changes);
        Assert.Equal(ChangeKind.TcpGlobal, change.Kind);
        Assert.Equal(("disabled", "normal"), (change.Before, change.After));
        Assert.True(change.NeedsElevation);
        Assert.Equal(["tcp autotuninglevel=normal", "tcp rss=enabled"], pc.Writes);

        var reverted = configurator.Revert(outcomes);
        Assert.All(reverted, r => Assert.Equal(WindowsOpStatus.Reverted, r.Status));
        Assert.Equal("disabled", pc.Tcp["autotuninglevel"]);
        Assert.Equal("disabled", pc.Tcp["rss"]);
    }

    [Fact]
    public void A_tcp_setting_that_cant_be_read_is_unknown_and_never_written()
    {
        var pc = new FakeWindows(); // nothing in pc.Tcp: every read fails
        var op = Op("repair.tcp-rss");

        Assert.Equal(OperationState.Unknown, WindowsEvaluator.Evaluate(op, pc).State);
        var outcome = new WindowsConfigurator(pc, pc).Apply([op], TestContext.Current.CancellationToken).Single();
        Assert.Equal(WindowsOpStatus.Unknown, outcome.Status);
        Assert.Empty(pc.Writes);
    }

    [Fact]
    public void A_tcp_write_windows_doesnt_keep_fails_and_is_put_back()
    {
        var pc = new FakeWindows { TcpKeeps = false };
        pc.Tcp["autotuninglevel"] = "highlyrestricted";

        var outcome = new WindowsConfigurator(pc, pc).Apply([Op("manual.restore-tcp-autotuning")], TestContext.Current.CancellationToken).Single();

        Assert.Equal(WindowsOpStatus.Failed, outcome.Status);
        Assert.Empty(outcome.Changes); // nothing took effect, so nothing is left to put back
        Assert.Equal("highlyrestricted", pc.Tcp["autotuninglevel"]);
    }

    [Fact]
    public void A_tcp_level_mesura_couldnt_give_back_is_left_alone()
    {
        var pc = new FakeWindows();
        pc.Tcp["autotuninglevel"] = "level 9"; // not a value netsh takes, so it couldn't be restored

        var outcome = new WindowsConfigurator(pc, pc).Apply([Op("manual.restore-tcp-autotuning")], TestContext.Current.CancellationToken).Single();

        Assert.Equal(WindowsOpStatus.Failed, outcome.Status);
        Assert.Empty(pc.Writes);
    }

    [Fact]
    public void The_catalog_refuses_a_tcp_setting_mesura_doesnt_write()
    {
        var json = WindowsCatalog.BuiltInJson().Replace("\"name\": \"rsc\", \"value\": \"enabled\"", "\"name\": \"congestionprovider\", \"value\": \"bbr2\"", StringComparison.Ordinal);
        Assert.NotEqual(WindowsCatalog.BuiltInJson(), json);
        var error = Assert.Throws<FormatException>(() => WindowsCatalog.Parse(json));
        Assert.Contains("congestionprovider", error.Message, StringComparison.Ordinal);
        Assert.False(TcpGlobalSetting.IsAllowed("rss", "maybe"));
        Assert.True(TcpGlobalSetting.IsAllowed("AutoTuningLevel", "Normal"));
    }

    [Fact]
    public void The_ipv6_repair_clears_the_disabling_bits_and_keeps_a_preference_for_ipv4()
    {
        var op = Op("repair.ipv6-components");
        var configurator = (FakeWindows pc) => new WindowsConfigurator(pc, pc);

        var untouched = new FakeWindows();
        Assert.Equal(OperationState.Applied, WindowsEvaluator.Evaluate(op, untouched).State); // absent is Windows' default

        var preferIpv4 = new FakeWindows();
        preferIpv4.Registry[Ipv6] = (RegistryKind.DWord, "32"); // 0x20, what Microsoft recommends instead of turning IPv6 off
        Assert.Equal(OperationState.Applied, WindowsEvaluator.Evaluate(op, preferIpv4).State);

        var disabled = new FakeWindows();
        disabled.Registry[Ipv6] = (RegistryKind.DWord, "255"); // 0xFF: IPv6 off
        Assert.Equal(OperationState.NotApplied, WindowsEvaluator.Evaluate(op, disabled).State);
        var outcome = configurator(disabled).Apply([op], TestContext.Current.CancellationToken).Single();
        Assert.Equal(WindowsOpStatus.Applied, outcome.Status);
        Assert.Equal("32", disabled.Registry[Ipv6].Value); // the disabling bits cleared, the IPv4 preference bit kept
        Assert.Equal(Activation.Reboot, outcome.Activation);

        var wrong = new FakeWindows();
        wrong.Registry[Ipv6] = (RegistryKind.DWord, "4294967295"); // 0xFFFFFFFF: the wrong value that delays start-up
        configurator(wrong).Apply([op], TestContext.Current.CancellationToken);
        Assert.Equal("32", wrong.Registry[Ipv6].Value);

        var tunnelsOnly = new FakeWindows();
        tunnelsOnly.Registry[Ipv6] = (RegistryKind.DWord, "1");
        configurator(tunnelsOnly).Apply([op], TestContext.Current.CancellationToken);
        Assert.Equal("0", tunnelsOnly.Registry[Ipv6].Value);
    }

    [Fact]
    public void Repair_entries_are_maintenance_and_carry_their_benefit()
    {
        foreach (var id in new[] { "manual.restore-tcp-autotuning", "repair.tcp-rss", "repair.tcp-rsc", "repair.ipv6-components", "repair.ncsi-active-probing", "updates.no-p2p-delivery" })
        {
            Assert.Equal(OperationImpact.Maintenance, Op(id).Impact);
        }

        Assert.All(new[] { "manual.restore-tcp-autotuning", "repair.tcp-rss", "repair.tcp-rsc", "repair.ipv6-components" },
            id => Assert.Equal(OperationBenefit.Documented, Op(id).Evidence));
        Assert.Equal(OperationBenefit.Preference, Op("updates.no-p2p-delivery").Evidence); // a choice, not a speed-up

        // Without a benefit in the catalog it follows the risk class.
        Assert.Equal(OperationBenefit.Preference, Op("explorer.show-extensions").Evidence);
        Assert.Equal(OperationBenefit.Unverified, Op("explorer.classic-context-menu").Evidence);
        Assert.Equal(OperationBenefit.Documented, Op("system.fast-startup-off").Evidence);
    }

    [Fact]
    public void The_mmcss_reserve_repair_puts_back_the_documented_default()
    {
        const string key = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\SystemResponsiveness";
        var op = Op("repair.mmcss-responsiveness");
        Assert.Equal(OperationImpact.Maintenance, op.Impact);
        Assert.Equal(OperationBenefit.Documented, op.Evidence);

        var untouched = new FakeWindows(); // no value stored: 20 is Windows' default
        Assert.Equal(OperationState.Applied, WindowsEvaluator.Evaluate(op, untouched).State);

        var tweaked = new FakeWindows();
        tweaked.Registry[key] = (RegistryKind.DWord, "10");
        Assert.Equal(OperationState.NotApplied, WindowsEvaluator.Evaluate(op, tweaked).State);
        var outcome = new WindowsConfigurator(tweaked, tweaked).Apply([op], TestContext.Current.CancellationToken).Single();
        Assert.Equal(WindowsOpStatus.Applied, outcome.Status);
        Assert.Equal("20", tweaked.Registry[key].Value);
        Assert.Equal(("10", "20"), (outcome.Changes[0].Before, outcome.Changes[0].After));
        Assert.All(new WindowsConfigurator(tweaked, tweaked).Revert([outcome]), r => Assert.Equal(WindowsOpStatus.Reverted, r.Status));
        Assert.Equal("10", tweaked.Registry[key].Value);
    }

    private const string LastAccess = @"HKLM\SYSTEM\CurrentControlSet\Control\FileSystem\NtfsDisableLastAccessUpdate";

    [Fact]
    public void Last_access_updates_are_turned_off_by_setting_one_bit_and_keeping_the_rest()
    {
        var op = Op("system.ntfs-last-access-off");
        Assert.Equal(OperationImpact.Performance, op.Impact);
        Assert.Equal(OperationBenefit.Unverified, op.Evidence); // Microsoft says it helps, not by how much

        var pc = new FakeWindows();
        pc.Registry[LastAccess] = (RegistryKind.DWord, "2147483650"); // 0x80000002, as read on PC-000: managed flag set, updates on
        Assert.Equal(OperationState.NotApplied, WindowsEvaluator.Evaluate(op, pc).State);

        var outcome = new WindowsConfigurator(pc, pc).Apply([op], TestContext.Current.CancellationToken).Single();
        Assert.Equal(WindowsOpStatus.Applied, outcome.Status);
        Assert.Equal("2147483651", pc.Registry[LastAccess].Value); // 0x80000003: the managed flag is kept, the updates bit is set
        Assert.Equal(Activation.Reboot, outcome.Activation);
        Assert.Equal(OperationState.Applied, WindowsEvaluator.Evaluate(op, pc).State);

        Assert.All(new WindowsConfigurator(pc, pc).Revert([outcome]), r => Assert.Equal(WindowsOpStatus.Reverted, r.Status));
        Assert.Equal("2147483650", pc.Registry[LastAccess].Value); // the exact previous value
    }

    [Fact]
    public void A_set_bit_is_read_as_met_whatever_the_other_bits_hold_and_a_missing_value_is_written_whole()
    {
        var op = Op("system.ntfs-last-access-off");
        foreach (var met in new[] { "1", "3", "2147483649", "2147483651" })
        {
            var pc = new FakeWindows();
            pc.Registry[LastAccess] = (RegistryKind.DWord, met);
            Assert.Equal(OperationState.Applied, WindowsEvaluator.Evaluate(op, pc).State);
            Assert.Empty(new WindowsConfigurator(pc, pc).Apply([op], TestContext.Current.CancellationToken).Single().Changes); // nothing to write
        }

        var missing = new FakeWindows(); // no value: the catalog value is written
        Assert.Equal(OperationState.NotApplied, WindowsEvaluator.Evaluate(op, missing).State);
        new WindowsConfigurator(missing, missing).Apply([op], TestContext.Current.CancellationToken);
        Assert.Equal("1", missing.Registry[LastAccess].Value);

        var text = new FakeWindows();
        text.Registry[LastAccess] = (RegistryKind.String, "not a number"); // a bit field that isn't a number is left alone
        Assert.NotEqual(WindowsOpStatus.Applied, new WindowsConfigurator(text, text).Apply([op], TestContext.Current.CancellationToken).Single().Status);
        Assert.Equal("not a number", text.Registry[LastAccess].Value);
    }

    [Fact]
    public void The_catalog_refuses_a_set_bits_entry_that_would_contradict_itself()
    {
        const string good = "\"value\": \"1\", \"setBits\": 1";
        var json = WindowsCatalog.BuiltInJson();
        Assert.Contains(good, json, StringComparison.Ordinal);
        foreach (var bad in new[] { "\"value\": \"1\", \"setBits\": 0", "\"value\": \"0\", \"setBits\": 1", "\"value\": \"1\", \"setBits\": 1, \"clearBits\": 2" })
        {
            Assert.Throws<FormatException>(() => WindowsCatalog.Parse(json.Replace(good, bad, StringComparison.Ordinal)));
        }
    }

    [Fact]
    public void Error_reporting_and_cross_device_experiences_are_documented_policies_read_as_set_only_when_they_are()
    {
        const string wer = @"HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\Disabled";
        const string cdp = @"HKLM\SOFTWARE\Policies\Microsoft\Windows\System\EnableCdp";
        var reporting = Op("privacy.no-error-reporting");
        var devices = Op("privacy.no-cross-device-experiences");
        Assert.All(new[] { reporting, devices }, o =>
        {
            Assert.Equal(OperationImpact.Privacy, o.Impact);
            Assert.Equal(OperationBenefit.Documented, o.Evidence);
            Assert.Equal(RiskClass.SystemConfiguration, o.Risk);
        });
        Assert.Equal(19041, devices.MinBuild);

        var pc = new FakeWindows(); // neither is set: Windows' own default, which for EnableCdp depends on the edition
        Assert.Equal(OperationState.NotApplied, WindowsEvaluator.Evaluate(reporting, pc).State);
        Assert.Equal(OperationState.NotApplied, WindowsEvaluator.Evaluate(devices, pc).State);

        pc.Registry[wer] = (RegistryKind.DWord, "1"); // as on PC-000
        pc.Registry[cdp] = (RegistryKind.DWord, "1"); // the policy says "enabled": not the target
        Assert.Equal(OperationState.Applied, WindowsEvaluator.Evaluate(reporting, pc).State);
        Assert.Equal(OperationState.NotApplied, WindowsEvaluator.Evaluate(devices, pc).State);

        var outcome = new WindowsConfigurator(pc, pc).Apply([reporting, devices], TestContext.Current.CancellationToken);
        Assert.Equal([WindowsOpStatus.AlreadyApplied, WindowsOpStatus.Applied], outcome.Select(o => o.Status));
        Assert.Equal("0", pc.Registry[cdp].Value);
        Assert.All(new WindowsConfigurator(pc, pc).Revert([outcome[1]]), r => Assert.Equal(WindowsOpStatus.Reverted, r.Status));
        Assert.Equal("1", pc.Registry[cdp].Value);
        Assert.Equal("1", pc.Registry[wer].Value); // untouched: it was already set, so there was nothing to put back
    }

    [Fact]
    public void New_collections_hold_exactly_what_they_say_and_no_new_entry_reaches_a_primary_preset()
    {
        Assert.Equal(["repair.threaded-dpc", "repair.mmcss-responsiveness", "repair.large-system-cache", "repair.prefetcher",
            // 0.2.0: the boot-timer overrides and the scheduler value put back to Windows' own (all no-ops on an untouched PC)
            "cpu.platform-clock-default", "cpu.dynamic-tick-default", "cpu.win32-priority-separation-default",
            // 0.4.1: the two service defaults that stripped images change (the old brand-named follow-up collection is gone)
            "services.sens-on", "services.print-on"], Catalog.PresetById("system-defaults")!.OperationIds);

        // Mesura Reference is exactly its three parts: performance, privacy and fewer promotions, no overlap and nothing left over.
        var parts = new[] { "reference-performance", "reference-privacy", "reference-convenience" }.SelectMany(id => Catalog.PresetById(id)!.OperationIds).ToList();
        Assert.Equal(parts.Count, parts.Distinct().Count());
        Assert.Equal(Catalog.PresetById("recommended-gaming")!.OperationIds.Order(), parts.Order());
        Assert.Equal(["privacy.diagnostic-data-required", "privacy.no-advertising-id", "privacy.no-tailored-experiences", "privacy.no-language-list", "privacy.no-app-launch-tracking",
            "privacy.no-typing-personalization", "privacy.no-online-speech", "privacy.no-activity-history", "privacy.no-error-reporting", "privacy.no-cross-device-experiences",
            // 0.4: the documented policies of the old privacy-policies collection moved in, and its duplicates and harmful switches were dropped by the audit.
            "privacy.no-inking-typing-data-upload", "privacy.no-cross-device-clipboard", "privacy.no-settings-sync", "telemetry.ceip-policy-off", "telemetry.application-telemetry-off",
            "telemetry.limit-diagnostic-logs", "telemetry.limit-dump-collection", "telemetry.inventory-collector-off", "telemetry.onesettings-downloads-off", "search.no-box-suggestions",
            "search.no-cloud-content", "search.no-location", "edge.diagnostic-data-required", "ai.click-to-do-off"],
            Catalog.PresetById("less-data-sharing")!.OperationIds);
        Assert.All(new[] { "system-defaults", "less-data-sharing", "network-defaults" }, id =>
        {
            var preset = Catalog.PresetById(id)!;
            Assert.Equal(PresetTier.Collection, preset.Tier);
            Assert.Empty(WindowsConfigurator.Conflicts(Catalog.Resolve(preset)));
        });

        // Every default repair has a collection to live in, so none is selectable only one by one.
        var homes = Catalog.Presets.Where(p => p.Tier == PresetTier.Collection).SelectMany(p => p.OperationIds).ToHashSet();
        Assert.All(Catalog.Operations.Where(o => o.Category == WindowsCatalog.DefaultsCategory && o.Applicable), o => Assert.Contains(o.Id, homes));

        // Preferences and unmeasured settings stay Optional: outside Recommended and Maximum performance, by decision.
        var primary = Catalog.Presets.Where(p => p.Tier == PresetTier.Primary).SelectMany(p => p.OperationIds.Concat(p.OptionalIds)).ToHashSet();
        Assert.All(new[] { "explorer.show-extensions", "repair.mmcss-responsiveness" }, id => Assert.DoesNotContain(id, primary));

        // The 0.4 audit took the privacy extras out of Maximum performance (it is about performance and desktop power now) and left them in
        // the Less data sharing collection; the unmeasured ones are its unticked extras: never in the preset itself.
        var maximum = Catalog.PresetById("maximum-performance")!;
        Assert.All(new[] { "privacy.no-error-reporting", "privacy.no-cross-device-experiences" }, id =>
        {
            Assert.DoesNotContain(id, maximum.OperationIds);
            Assert.Contains(id, Catalog.PresetById("less-data-sharing")!.OperationIds);
        });
        Assert.All(new[] { "system.ntfs-last-access-off", "gpu.hardware-scheduling-on" }, id =>
        {
            Assert.Contains(id, maximum.OptionalIds);
            Assert.DoesNotContain(id, maximum.OperationIds);
        });
    }

    [Fact]
    public void Network_defaults_is_a_collection_of_documented_default_repairs_and_recommended_is_the_reviewed_list()
    {
        var preset = Catalog.PresetById("network-defaults")!;
        Assert.Equal(PresetTier.Collection, preset.Tier);
        Assert.All(Catalog.Resolve(preset), op =>
        {
            Assert.Equal(WindowsCatalog.DefaultsCategory, op.Category);
            Assert.Equal(OperationImpact.Maintenance, op.Impact);
            Assert.Equal(RiskClass.SystemConfiguration, op.Risk);
        });

        // The network repairs stay opt-in (the one in Recommended is the connectivity check, shared with this collection).
        // Recommended is the list of the 2026-10-25 profile review (docs/profile-review-2026-10.md): pinned exactly, so a
        // change to it is a decision, not a side effect.
        Assert.Equal(["gaming.game-mode-on", "input.accessibility-shortcuts-off", "privacy.no-advertising-id", "privacy.no-tailored-experiences", "privacy.no-language-list",
                      "privacy.diagnostic-data-required", "telemetry.ceip-policy-off", "telemetry.application-telemetry-off", "notifications.no-feedback-prompts",
                      "notifications.no-tips-and-suggestions", "notifications.no-setup-nags", "lockscreen.no-spotlight-ads", "start.no-recommendations",
                      "start.no-account-notifications", "suggestions.no-third-party-in-spotlight", "search.no-highlights-policy", "repair.ncsi-active-probing", "repair.threaded-dpc"],
            Catalog.PresetById("recommended-gaming")!.OperationIds);
    }

    [Fact]
    public void Conflicts_come_as_groups_once_for_the_app_to_word()
    {
        var groups = WindowsConfigurator.ConflictGroups([Op("taskbar.search-icon-only"), Op("taskbar.hide-search"), Op("appearance.dark-mode")]);

        var group = Assert.Single(groups);
        Assert.Equal(["taskbar.search-icon-only", "taskbar.hide-search"], group.Select(o => o.Id));
        Assert.Single(WindowsConfigurator.Conflicts([Op("taskbar.search-icon-only"), Op("taskbar.hide-search")])); // the CLI's sentence
        Assert.Empty(WindowsConfigurator.ConflictGroups([Op("repair.tcp-rss"), Op("repair.tcp-rsc")]));
    }

    [Fact]
    public void Winget_refusals_keep_the_phrases_the_app_translates()
    {
        // The app words each refusal in the user's language by these phrases (AppsViewModel.RefusalReason).
        Assert.Contains("couldn't tell which user", Mesura.Core.Apps.WingetContext.Decide(false, false, true, Mesura.Core.Apps.AppRunContext.Any, false).Reason, StringComparison.Ordinal);
        Assert.Contains("shell isn't reachable", Mesura.Core.Apps.WingetContext.Decide(true, true, true, Mesura.Core.Apps.AppRunContext.SignedInUser, false).Reason, StringComparison.Ordinal);
        Assert.Contains("another account", Mesura.Core.Apps.WingetContext.Decide(true, false, true, Mesura.Core.Apps.AppRunContext.Any, false).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Key_problems_are_told_apart_from_other_activation_failures()
    {
        Assert.True(ActivationErrors.IsKeyProblem(0xC004C003));  // blocked key
        Assert.True(ActivationErrors.IsKeyProblem(0xC004C060));  // key no longer valid
        Assert.False(ActivationErrors.IsKeyProblem(0x80072EE7)); // no connection: trying again can work
        Assert.False(ActivationErrors.IsKeyProblem(0xC004F074)); // no KMS host reachable
    }
}
