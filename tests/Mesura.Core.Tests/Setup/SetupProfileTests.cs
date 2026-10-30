using Mesura.Core.Apps;
using Mesura.Core.Setup;

namespace Mesura.Core.Tests.Setup;

public sealed class SetupProfileTests
{
    private static string Profile(string body) =>
        "{ \"kind\": \"Mesura.SetupProfile\", \"schemaVersion\": 1, \"name\": \"P\", " + body + " }";

    [Fact]
    public void Resolves_bundles_then_apps_once_each_and_filters_config_ports()
    {
        var profile = SetupProfile.Parse(Profile("""
            "bundles": ["everyday"], "apps": ["brave", "steam"],
            "configBundle": "my-config.json", "configApps": ["brave", "STEAM"]
            """), AppCatalog.Default);

        var apps = profile.ResolveApps(AppCatalog.Default).Select(p => p.Id).ToList();
        Assert.Equal(["brave", "discord", "vencord", "spotify", "sevenzip", "steam"], apps);
        Assert.Equal(["steam", "brave"], profile.ResolveConfigPorts().Select(p => p.Id));
    }

    [Fact]
    public void Comments_and_trailing_commas_are_accepted_in_a_hand_edited_profile()
    {
        var profile = SetupProfile.Parse("""
            {
              // my PC
              "kind": "Mesura.SetupProfile", "schemaVersion": 1, "name": "P",
              "apps": [ "brave", ],
            }
            """, AppCatalog.Default);

        Assert.Equal(["brave"], profile.Apps);
        Assert.Empty(profile.ResolveConfigPorts()); // no bundle, no config step
    }

    [Fact]
    public void A_profile_naming_an_operation_the_catalog_has_since_made_guided_still_loads()
    {
        // Saved by the 0.1.0 build, when the Widgets button was still written (it is guided since the 2026-10-22 audit).
        var profile = SetupProfile.Parse(Profile("\"windows\": [\"explorer.show-extensions\", \"security.firewall-status\"]"), AppCatalog.Default);

        Assert.Equal(["explorer.show-extensions", "security.firewall-status"], profile.Windows);
    }

    [Theory]
    [InlineData("\"apps\": [\"nope\"]", "'nope'")]
    [InlineData("\"bundles\": [\"nope\"]", "'nope'")]
    [InlineData("\"configBundle\": \"x.json\", \"configApps\": [\"chrome\"]", "'chrome'")]
    [InlineData("\"configBundle\": \"C:\\\\Users\\\\me\\\\x.json\"", "relative")]
    [InlineData("\"description\": \"empty\"", "selects nothing")]
    public void Invalid_profiles_are_rejected_with_a_pointed_message(string body, string expected)
    {
        var e = Assert.Throws<FormatException>(() => SetupProfile.Parse(Profile(body), AppCatalog.Default));
        Assert.Contains(expected, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_profile_saved_from_a_machine_keeps_installed_apps_and_applied_operations_and_reads_back()
    {
        var catalog = AppCatalog.Default;
        var windows = Mesura.Core.Windows.WindowsCatalog.Default;
        var apps = new[]
        {
            new AppPlanItem(catalog.ById("brave")!, InstallState.Installed),
            new AppPlanItem(catalog.ById("steam")!, InstallState.NotInstalled),
            new AppPlanItem(catalog.ById("vencord")!, InstallState.Unknown),
        };
        var applied = windows.ById("explorer.show-extensions")!;
        var pending = windows.ById("start.no-recommendations")!;
        var partial = windows.ById("notifications.no-tips-and-suggestions")!;
        var guided = windows.Operations.First(o => !o.Applicable);
        Mesura.Core.Windows.OperationEvaluation Eval(Mesura.Core.Windows.WindowsOperation op, Mesura.Core.Windows.OperationState state) => new(op, state, "", []);

        var profile = SetupProfile.FromMachine("my-setup", "Saved.", apps,
            [Eval(applied, Mesura.Core.Windows.OperationState.Applied), Eval(pending, Mesura.Core.Windows.OperationState.NotApplied),
             Eval(partial, Mesura.Core.Windows.OperationState.Partial), Eval(guided, Mesura.Core.Windows.OperationState.Applied)],
            "my-setup.settings.json");

        Assert.Equal(["brave"], profile.Apps); // not installed and undetectable apps are left out
        Assert.Equal(["explorer.show-extensions"], profile.Windows); // only fully applied, applicable operations
        var back = SetupProfile.Parse(profile.ToJson(), catalog, windows);
        Assert.Equal(profile.Name, back.Name);
        Assert.Equal(profile.Description, back.Description);
        Assert.Equal(profile.Apps, back.Apps);
        Assert.Equal(profile.Windows, back.Windows);
        Assert.Equal("my-setup.settings.json", back.ConfigBundle);
        Assert.DoesNotContain("bundles", profile.ToJson(), StringComparison.Ordinal); // empty lists aren't written
    }

    [Fact]
    public void A_machine_with_nothing_to_carry_gives_a_profile_that_selects_nothing()
    {
        var profile = SetupProfile.FromMachine("empty", "", [], [], configBundle: null);
        Assert.False(profile.SelectsAnything);
    }

    [Fact]
    public void A_run_is_ok_only_when_every_step_ended_well()
    {
        var ok = SetupRun.Create("id", "apps-install", null, "a", "b",
            [new InstallOutcome("x", "X.X", InstallStatus.AlreadyInstalled, ""), new InstallOutcome("y", "", InstallStatus.Manual, "")], []);
        var failed = SetupRun.Create("id", "apps-install", null, "a", "b",
            [new InstallOutcome("x", "X.X", InstallStatus.Failed, "")], []);
        var skipped = SetupRun.Create("id", "config-import", null, "a", "b", [],
            [new ConfigImportOutcome("brave", "Brave", ConfigImportStatus.SkippedAppRunning, [], [], [], [])]);

        Assert.True(ok.Ok);
        Assert.False(failed.Ok);
        Assert.False(skipped.Ok);
        Assert.Equal("20261018T090502Z-profile", SetupRun.NewId(new DateTimeOffset(2026, 10, 18, 11, 5, 2, TimeSpan.FromHours(2)), "profile"));
    }
}
