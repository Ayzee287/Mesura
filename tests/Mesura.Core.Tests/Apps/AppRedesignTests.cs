using Mesura.Core.Apps;
using Mesura.Core.Config;

namespace Mesura.Core.Tests.Apps;

/// <summary>
/// The redesign's app data and the rules behind it: which apps the app lists, which installers run as the signed-in
/// user, Vencord's own detector, whose context winget runs in, and Mesura's default configurations.
/// </summary>
public sealed class AppRedesignTests
{
    private static readonly AppCatalog Catalog = AppCatalog.Default;

    [Fact]
    public void Removed_apps_stay_in_the_catalog_for_profiles_but_are_not_listed()
    {
        string[] unlisted = ["zoom", "firefox", "epic", "vlc", "git", "vscode", "nvpi", "nvcleanstall", "vencord"];
        Assert.All(unlisted, id => Assert.False(Catalog.ById(id)!.Listed, id));
        Assert.All(["brave", "discord", "spotify", "steam", "sevenzip", "obs", "chrome", "notepadpp"],
            id => Assert.True(Catalog.ById(id)!.Listed, id));
    }

    [Fact]
    public void Vencord_is_a_customisation_of_discord_and_a_customisation_must_name_a_catalog_app()
    {
        Assert.Equal("discord", Catalog.ById("vencord")!.Customizes);
        Assert.All(Catalog.Packages.Where(p => p.Id != "vencord"), p => Assert.Equal("", p.Customizes));
        var e = Assert.Throws<FormatException>(() => AppCatalog.Parse("""
            { "kind": "Mesura.AppCatalog", "schemaVersion": 2,
              "apps": [ { "id": "mod", "wingetId": "A.Mod", "name": "Mod", "customizes": "missing" } ] }
            """));
        Assert.Contains("customises 'missing'", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Per_user_installers_run_as_the_signed_in_user()
    {
        Assert.All(["discord", "spotify", "vencord"], id => Assert.Equal(AppRunContext.SignedInUser, Catalog.ById(id)!.Context));
        Assert.All(["brave", "steam", "sevenzip"], id => Assert.Equal(AppRunContext.Any, Catalog.ById(id)!.Context));
    }

    [Fact]
    public void Vencord_has_its_own_detector_and_an_unknown_context_is_refused()
    {
        Assert.Equal("vencord", Catalog.ById("vencord")!.Detector);
        var e = Assert.Throws<FormatException>(() => AppCatalog.Parse("""
            { "kind": "Mesura.AppCatalog", "schemaVersion": 1,
              "apps": [ { "id": "a", "wingetId": "A.A", "name": "A", "context": "admin" } ] }
            """));
        Assert.Contains("unknown context 'admin'", e.Message, StringComparison.Ordinal);
    }

    private sealed class NoWinget : IWingetRunner
    {
        public int Lists { get; private set; }

        public bool IsAvailable() => true;

        public WingetRunResult List(AppPackage package)
        {
            Lists++;
            return new WingetRunResult(true, WingetOutput.NoApplicationsFound, "No installed package found matching input criteria.");
        }

        public WingetRunResult Install(AppPackage package, Action<string>? onOutput, CancellationToken cancellationToken) => new(true, 0);
    }

    [Fact]
    public void A_detector_decides_the_state_of_an_app_winget_cannot_see()
    {
        var winget = new NoWinget();
        var vencord = Catalog.ById("vencord")!;
        var service = new AppInstallService(winget, new Dictionary<string, Func<InstallState>> { ["vencord"] = () => InstallState.Installed });

        Assert.True(service.CanDetect(vencord));
        Assert.Equal(InstallState.Installed, service.BuildPlan([vencord]).Single().State);
        Assert.Equal(0, winget.Lists);
    }

    [Fact]
    public void A_detector_that_cannot_read_reports_unknown_never_not_installed()
    {
        var service = new AppInstallService(new NoWinget(),
            new Dictionary<string, Func<InstallState>> { ["vencord"] = () => throw new UnauthorizedAccessException("denied") });

        Assert.Equal(InstallState.Unknown, service.BuildPlan([Catalog.ById("vencord")!]).Single().State);
    }

    [Fact]
    public void Without_its_detector_vencord_stays_undetectable()
    {
        var service = new AppInstallService(new NoWinget());

        Assert.False(service.CanDetect(Catalog.ById("vencord")!));
        Assert.Equal(InstallState.Unknown, service.BuildPlan([Catalog.ById("vencord")!]).Single().State);
    }

    [Fact]
    public void A_refused_launch_is_reported_with_its_reason()
    {
        var refusing = new RefusingWinget("Mesura runs as another account.");
        var outcome = new AppInstallService(refusing).Install([new AppPlanItem(Catalog.ById("spotify")!, InstallState.NotInstalled)],
            skipInstalled: true, null, CancellationToken.None).Single();

        Assert.Equal(InstallStatus.Failed, outcome.Status);
        Assert.Equal("Mesura runs as another account.", outcome.Detail);
    }

    private sealed class RefusingWinget(string reason) : IWingetRunner
    {
        public bool IsAvailable() => true;

        public WingetRunResult List(AppPackage package) => WingetRunResult.Refused(reason);

        public WingetRunResult Install(AppPackage package, Action<string>? onOutput, CancellationToken cancellationToken) => WingetRunResult.Refused(reason);
    }

    [Theory]
    // Not elevated, as the signed-in user: everything runs directly, as before.
    [InlineData(true, true, false, AppRunContext.Any, false, WingetLaunch.Direct)]
    [InlineData(true, true, false, AppRunContext.SignedInUser, false, WingetLaunch.Direct)]
    // Elevated as the signed-in user: machine installers run elevated (no second prompt); per-user ones as the user.
    [InlineData(true, true, true, AppRunContext.Any, true, WingetLaunch.Direct)]
    [InlineData(true, true, true, AppRunContext.SignedInUser, true, WingetLaunch.AsSignedInUser)]
    [InlineData(true, true, true, AppRunContext.SignedInUser, false, WingetLaunch.Refused)]
    // Elevated as another administrator: everything runs as the signed-in user, or not at all.
    [InlineData(true, false, true, AppRunContext.Any, true, WingetLaunch.AsSignedInUser)]
    [InlineData(true, false, true, AppRunContext.Any, false, WingetLaunch.Refused)]
    [InlineData(true, false, false, AppRunContext.Any, false, WingetLaunch.Refused)]
    // No signed-in user known: fine unelevated (the user running Mesura is the only one), refused elevated.
    [InlineData(false, false, false, AppRunContext.SignedInUser, false, WingetLaunch.Direct)]
    [InlineData(false, false, true, AppRunContext.Any, true, WingetLaunch.Refused)]
    public void Winget_runs_as_the_signed_in_user_or_not_at_all(
        bool userKnown, bool sameUser, bool elevated, AppRunContext context, bool reachable, WingetLaunch expected)
    {
        var (launch, reason) = WingetContext.Decide(userKnown, sameUser, elevated, context, reachable);

        Assert.Equal(expected, launch);
        Assert.Equal(expected == WingetLaunch.Refused, reason.Length > 0);
    }

    [Fact]
    public void Mesura_defaults_cover_brave_and_spotify_only_and_every_value_is_allowlisted_and_valid()
    {
        var bundle = ConfigDefaults.Bundle("2026-10-23T00:00:00Z");

        Assert.Equal(["brave", "spotify"], bundle.Apps.Keys.Order(StringComparer.Ordinal));
        Assert.False(ConfigDefaults.Has("steam"));
        Assert.False(ConfigDefaults.Has("vencord"));
        foreach (var (portId, entry) in bundle.Apps)
        {
            var port = ConfigCatalog.ById(portId)!;
            Assert.All(entry.Values.Keys, id => Assert.Contains(port.Settings, s => s.Id == id));
        }

        // Brave's values are JSON booleans written into its Preferences; Spotify's a quoted word in prefs.
        var brave = new JsonConfigDocument("""{"profile":{"name":"keep"}}""");
        var result = ConfigEngine.Import(ConfigCatalog.ById("brave")!, brave, bundle.ForApp("brave")!, _ => true);
        Assert.Equal(bundle.Apps["brave"].Values.Count, result.Applied.Count);
        Assert.Equal("false", brave.TryGet("/brave/brave_ads/sponsored/enabled"));
        Assert.Equal("\"keep\"", brave.TryGet("/profile/name"));

        var spotify = new PrefsConfigDocument("language=\"en\"\n");
        ConfigEngine.Import(ConfigCatalog.ById("spotify")!, spotify, bundle.ForApp("spotify")!, _ => true);
        Assert.Equal("\"off\"", spotify.TryGet("app.autostart-mode"));
        Assert.Equal("\"en\"", spotify.TryGet("language"));
    }
}
