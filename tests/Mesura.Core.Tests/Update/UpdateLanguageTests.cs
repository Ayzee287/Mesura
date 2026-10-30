using Mesura.Core.Update;

namespace Mesura.Core.Tests.Update;

/// <summary>
/// An update keeps the language of the installed package (0.3.0-rc2): a Russian install is updated with the Russian installer, an English one with the
/// English, and an older manifest that has one package still works. Whatever is chosen is still checked against the signed hash.
/// </summary>
public sealed class UpdateLanguageTests
{
    private static readonly UpdatePackage En = new("msi", "x64", "https://example.org/en.msi", new string('a', 64), 10, "en");
    private static readonly UpdatePackage Ru = new("msi", "x64", "https://example.org/ru.msi", new string('b', 64), 11, "ru");
    private static readonly UpdatePackage Old = new("msi", "x64", "https://example.org/old.msi", new string('c', 64), 12);

    private static UpdateManifest Manifest(params UpdatePackage[] packages) =>
        new(1, "stable", "0.3.1", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(9), new Dictionary<string, string> { ["en"] = "n" }, packages);

    [Fact]
    public void The_installed_language_picks_its_own_package()
    {
        var manifest = Manifest(En, Ru);

        Assert.Equal(Ru, manifest.PackageFor("x64", "ru"));
        Assert.Equal(En, manifest.PackageFor("x64", "en"));
    }

    [Fact]
    public void Without_a_language_or_without_a_matching_package_the_English_one_is_used()
    {
        Assert.Equal(En, Manifest(En, Ru).PackageFor("x64"));
        Assert.Equal(En, Manifest(En).PackageFor("x64", "ru")); // nothing Russian published yet: English, not nothing
    }

    [Fact]
    public void A_manifest_from_before_the_language_field_still_yields_its_one_package()
    {
        Assert.Equal(Old, Manifest(Old).PackageFor("x64", "ru"));
        Assert.Equal(Old, Manifest(Old).PackageFor("x64"));
    }

    [Fact]
    public void A_language_is_read_from_and_written_to_the_manifest_json_and_an_old_one_without_it_parses()
    {
        var round = UpdateManifest.TryParse(Manifest(En, Ru).ToJson())!;
        Assert.Equal(["en", "ru"], round.Packages.Select(p => p.Language));

        var sha = new string('d', 64);
        var legacy = $$"""{ "schemaVersion": 1, "channel": "stable", "version": "0.3.1", "sequence": 1, "publishedUtc": "2026-10-01T00:00:00Z", "expiresUtc": "2099-01-01T00:00:00Z", "notes": { "en": "n" }, "packages": [ { "kind": "msi", "architecture": "x64", "url": "https://example.org/a.msi", "sha256": "{{sha}}", "size": 5 } ] }""";
        Assert.Null(UpdateManifest.TryParse(System.Text.Encoding.UTF8.GetBytes(legacy))!.Packages.Single().Language);
    }

    [Fact]
    public void The_architecture_still_has_to_match()
    {
        var arm = Ru with { Architecture = "arm64" };

        Assert.Null(Manifest(arm).PackageFor("x64", "ru"));
    }

    [Fact]
    public void The_policy_offers_the_package_in_the_installed_language_and_hashes_come_from_that_package()
    {
        var keys = Mesura.Core.Update.UpdateSignature.NewKeyPair();
        var bytes = Manifest(En, Ru).ToJson();
        var signature = UpdateSignature.Sign(bytes, keys.Private);

        var check = UpdatePolicy.Evaluate(bytes, signature, [Convert.ToBase64String(keys.Public)], "0.3.0", "stable", "x64", DateTimeOffset.UtcNow, new UpdateState(), "ru");

        Assert.Equal(UpdateOutcome.Available, check.Outcome);
        Assert.Equal(Ru, check.Package);
    }
}
