using FpsUtility.Core.Facts;
using FpsUtility.Core.Normalize;
using FpsUtility.Core.Raw;

namespace FpsUtility.Core.Tests.Normalize;

/// <summary>Steam's four states stay apart: registered with libraries, registered with an unreadable list, not registered, and failed.</summary>
public sealed class SteamNormalizationTests
{
    [Fact]
    public void Not_registered_is_a_known_fact_and_its_libraries_are_absent_not_failed()
    {
        var steam = Normalize(RawBuilder.Unavailable<SteamData>(MissingReason.NotPresent, @"Steam is not installed for this user: HKCU\Software\Valve\Steam\SteamPath is not set"));

        Assert.Equal(SteamRegistration.NotRegistered, steam.Registration.Value);
        Assert.Equal(Rules.SteamRegistration, steam.Registration.Rule);
        Assert.Equal(FactGap.NotPresent, steam.Libraries.Missing);
        Assert.Equal(FactGap.NotPresent, steam.InstallPath.Missing);
    }

    [Theory]
    [InlineData(MissingReason.TimedOut, FactGap.TimedOut)]
    [InlineData(MissingReason.AccessDenied, FactGap.AccessDenied)]
    [InlineData(MissingReason.SourceFailed, FactGap.SourceFailed)]
    public void A_failed_source_is_never_read_as_not_registered(MissingReason reason, FactGap gap)
    {
        var steam = Normalize(RawBuilder.Unavailable<SteamData>(reason));

        Assert.False(steam.Registration.IsKnown);
        Assert.Equal(gap, steam.Registration.Missing);
        Assert.Equal(gap, steam.Libraries.Missing);
    }

    [Fact]
    public void Registered_with_an_unreadable_library_list_keeps_the_registration_and_says_why_the_list_is_missing()
    {
        var data = new SteamData(
            @"C:\Steam",
            Reading.Missing<SteamFile>(MissingReason.AccessDenied, "UnauthorizedAccessException reading libraryfolders.vdf"),
            Reading.Missing<IReadOnlyList<SteamLibrary>>(MissingReason.AccessDenied, "The library list could not be read"));

        var steam = Normalize(RawBuilder.Ok(data));

        Assert.Equal(SteamRegistration.Registered, steam.Registration.Value);
        Assert.Equal(@"C:\Steam", steam.InstallPath.Value);
        Assert.Equal(FactGap.AccessDenied, steam.Libraries.Missing);
    }

    [Fact]
    public void Libraries_say_why_they_were_read_and_apps_come_from_their_manifests()
    {
        const string manifest = "\"AppState\"\n{\n\t\"appid\"\t\t\"730\"\n\t\"name\"\t\t\"Counter-Strike 2\"\n\t\"installdir\"\t\t\"Counter-Strike Global Offensive\"\n\t\"LastOwner\"\t\t\"redacted\"\n\t\"SizeOnDisk\"\t\t\"34359738368\"\n}\n";
        var data = new SteamData(
            @"C:\Steam",
            Reading.Known(new SteamFile(@"C:\Steam\steamapps\libraryfolders.vdf", "\"libraryfolders\"\n{\n}\n")),
            Reading.Known<IReadOnlyList<SteamLibrary>>(
            [
                new SteamLibrary(@"C:\Steam", listedByLibraryFolders: false, isInstallFolder: true, Reading.Known<IReadOnlyList<SteamFile>>([new SteamFile(@"C:\Steam\steamapps\appmanifest_730.acf", manifest)])),
                new SteamLibrary(@"D:\SteamLibrary", listedByLibraryFolders: true, isInstallFolder: false, Reading.Known<IReadOnlyList<SteamFile>>([])),
            ]));

        var steam = Normalize(RawBuilder.Ok(data));
        var install = steam.Libraries.Value![0];
        var app = install.Apps.Value!.Single();

        Assert.True(install.IsInstallFolder.Value);
        Assert.False(install.ListedBySteam.Value);
        Assert.True(steam.Libraries.Value![1].ListedBySteam.Value);
        Assert.Empty(steam.Libraries.Value![1].Apps.Value!); // a readable library with no apps: known empty
        Assert.Equal(730, app.AppId.Value);
        Assert.Equal("Counter-Strike 2", app.Name.Value);
        Assert.Equal("Counter-Strike Global Offensive", app.InstallDirectory.Value);
        Assert.Equal(34_359_738_368UL, app.SizeOnDiskBytes.Value);
        Assert.Equal(Basis.Declared, app.Name.Basis);
        Assert.Equal("/sources/steam/data/libraries/value/0/manifests/value/0/text", app.Name.From.Single());
    }

    [Theory]
    [InlineData("\"AppState\"\n{\n\t\"appid\" \"730\"")] // not closed
    [InlineData("\"UserConfig\"\n{\n}\n")] // not an app manifest
    public void An_unreadable_manifest_leaves_every_field_missing_not_guessed(string text)
    {
        var data = new SteamData(
            @"C:\Steam",
            Reading.Known(new SteamFile(@"C:\Steam\steamapps\libraryfolders.vdf", "\"libraryfolders\"\n{\n}\n")),
            Reading.Known<IReadOnlyList<SteamLibrary>>([new SteamLibrary(@"C:\Steam", false, true, Reading.Known<IReadOnlyList<SteamFile>>([new SteamFile(@"C:\Steam\steamapps\appmanifest_1.acf", text)]))]));

        var app = Normalize(RawBuilder.Ok(data)).Libraries.Value![0].Apps.Value!.Single();

        Assert.Equal(FactGap.ParseFailed, app.AppId.Missing);
        Assert.Equal(FactGap.ParseFailed, app.Name.Missing);
        Assert.Equal(FactGap.ParseFailed, app.SizeOnDiskBytes.Missing);
    }

    [Fact]
    public void A_manifest_value_of_the_wrong_kind_is_not_made_into_a_number()
    {
        const string manifest = "\"AppState\"\n{\n\t\"appid\"\t\t\"-5\"\n\t\"SizeOnDisk\"\t\t\"12 GB\"\n}\n";
        var data = new SteamData(
            @"C:\Steam",
            Reading.Known(new SteamFile(@"C:\Steam\steamapps\libraryfolders.vdf", "\"libraryfolders\"\n{\n}\n")),
            Reading.Known<IReadOnlyList<SteamLibrary>>([new SteamLibrary(@"C:\Steam", false, true, Reading.Known<IReadOnlyList<SteamFile>>([new SteamFile(@"C:\Steam\steamapps\appmanifest_1.acf", manifest)]))]));

        var app = Normalize(RawBuilder.Ok(data)).Libraries.Value![0].Apps.Value!.Single();

        Assert.Equal(FactGap.ParseFailed, app.AppId.Missing);
        Assert.Equal(FactGap.ParseFailed, app.SizeOnDiskBytes.Missing);
        Assert.Equal(FactGap.NotPresent, app.Name.Missing);
    }

    [Fact]
    public void A_manifest_nested_deeper_than_any_steam_file_is_a_parse_failure_not_a_crash()
    {
        // Unbounded nesting would overflow the stack and end the process, taking every other fact with it.
        var deep = "\"AppState\"\n" + string.Concat(Enumerable.Repeat("{ \"k\" ", 10_000)) + "{ }" + new string('}', 10_000);
        var data = new SteamData(
            @"C:\Steam",
            Reading.Known(new SteamFile(@"C:\Steam\steamapps\libraryfolders.vdf", "\"libraryfolders\"\n{\n}\n")),
            Reading.Known<IReadOnlyList<SteamLibrary>>([new SteamLibrary(@"C:\Steam", false, true, Reading.Known<IReadOnlyList<SteamFile>>([new SteamFile(@"C:\Steam\steamapps\appmanifest_1.acf", deep)]))]));

        var app = Normalize(RawBuilder.Ok(data)).Libraries.Value![0].Apps.Value!.Single();

        Assert.Equal(FactGap.ParseFailed, app.AppId.Missing);
        Assert.Throws<FormatException>(() => SteamLibraryFolders.ParsePaths("\"libraryfolders\"\n" + string.Concat(Enumerable.Repeat("{ \"0\" ", 10_000)) + "{ }" + new string('}', 10_000)));
    }

    [Fact]
    public void No_library_identifier_or_label_reaches_the_facts()
    {
        Assert.DoesNotContain(typeof(SteamLibraryFacts).GetProperties(), property => property.Name is "ContentId" or "Label" or "ContentStatsId");
        Assert.DoesNotContain(typeof(SteamAppFacts).GetProperties(), property => property.Name is "LastOwner" or "LastPlayed");
    }

    private static SteamFacts Normalize(SourceEntry<SteamData> steam) =>
        Normalizer.Normalize(RawBuilder.With(sources => sources with { Steam = steam })).Steam;
}
