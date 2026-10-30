using Mesura.Core.Apps;

namespace Mesura.Core.Tests.Apps;

/// <summary>The 0.4 changes to the app catalog: Telegram and AyuGram, and the install folder only for installers that take one.</summary>
public sealed class AppsFourTests
{
    private sealed class RecordingWinget : IWingetRunner
    {
        public List<(string Id, string? Folder)> Calls { get; } = [];

        public bool IsAvailable() => true;

        public WingetRunResult List(AppPackage package) => new(true, WingetOutput.NoApplicationsFound, "");

        public WingetRunResult Install(AppPackage package, Action<string>? onOutput, CancellationToken cancellationToken)
        {
            Calls.Add((package.WingetId, package.InstallFolder));
            return new WingetRunResult(true, 0);
        }
    }

    [Fact]
    public void Telegram_and_ayugram_come_from_their_own_publishers_and_install_as_the_signed_in_user()
    {
        var telegram = AppCatalog.Default.ById("telegram")!;
        var ayugram = AppCatalog.Default.ById("ayugram")!;
        Assert.Equal("Telegram.TelegramDesktop", telegram.WingetId);
        Assert.Equal("RadolynLabs.AyuGramDesktop", ayugram.WingetId);
        Assert.All(new[] { telegram, ayugram }, p =>
        {
            Assert.Equal(AppRunContext.SignedInUser, p.Context);
            Assert.True(p.Listed);
            Assert.Equal("Chat", p.Category);
            Assert.True(p.InstallLocation);
        });
        Assert.Equal("ayugram", ayugram.Detector); // winget doesn't list a copy installed from AyuGram's own release
        Assert.Contains("not Telegram's own app", ayugram.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void The_install_folder_is_offered_only_where_the_installer_takes_one()
    {
        var supported = AppCatalog.Default.Packages.Where(p => p.InstallLocation).Select(p => p.Id).Order().ToList();
        Assert.Equal(["ayugram", "notepadpp", "obs", "regorganizer", "sevenzip", "steam", "telegram", "vscode"], supported);

        // Squirrel (Discord), the vendors' own exes (Brave, Spotify) and Chrome's MSI have no folder option: choosing one would be ignored.
        Assert.All(new[] { "discord", "brave", "spotify", "chrome", "vencord" }, id => Assert.False(AppCatalog.Default.ById(id)!.InstallLocation, id));
        Assert.False(AppCatalog.Default.ById("medal")!.InstallLocation); // an exe installer with no documented folder switch
    }

    [Fact]
    public void A_chosen_folder_reaches_winget_only_for_a_package_that_takes_one()
    {
        var winget = new RecordingWinget();
        var service = new AppInstallService(winget)
        {
            InstallFolderFor = package => package.Id is "steam" or "discord" ? @"D:\Apps\" + package.Name : null,
        };
        service.Install(
            [new AppPlanItem(AppCatalog.Default.ById("steam")!, InstallState.NotInstalled), new AppPlanItem(AppCatalog.Default.ById("discord")!, InstallState.NotInstalled),
             new AppPlanItem(AppCatalog.Default.ById("obs")!, InstallState.NotInstalled)],
            skipInstalled: true, onProgress: null, CancellationToken.None);

        Assert.Equal([("Valve.Steam", @"D:\Apps\Steam"), ("Discord.Discord", null), ("OBSProject.OBSStudio", null)], winget.Calls);
    }

    [Fact]
    public void The_catalog_still_parses_with_an_entry_that_doesnt_mention_the_folder()
    {
        var catalog = AppCatalog.Parse("""{"kind":"Mesura.AppCatalog","schemaVersion":2,"apps":[{"id":"x","wingetId":"A.B","name":"X"}],"bundles":[]}""");
        Assert.False(catalog.ById("x")!.InstallLocation);
    }
}
