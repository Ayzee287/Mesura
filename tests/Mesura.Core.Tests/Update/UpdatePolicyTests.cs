using System.Text;
using Mesura.Core.Update;

namespace Mesura.Core.Tests.Update;

public sealed class UpdatePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly (byte[] Private, byte[] Public) Keys = UpdateSignature.NewKeyPair();
    private static readonly string PublicKey = Convert.ToBase64String(Keys.Public);

    private static UpdateManifest Manifest(string version = "0.3.1", string channel = "stable", long sequence = 10, DateTimeOffset? expires = null,
        string url = "https://example.org/Mesura-0.3.1-x64.msi", string sha = "")
    {
        return new UpdateManifest(
            UpdateManifest.CurrentSchema, channel, version, sequence, Now.AddDays(-1), expires ?? Now.AddDays(30),
            new Dictionary<string, string> { ["en"] = "Better things.", ["ru"] = "Лучше." },
            [new UpdatePackage("msi", "x64", url, sha.Length > 0 ? sha : new string('a', 64), 1234)]);
    }

    private static UpdateCheck Check(UpdateManifest manifest, string installed = "0.3.0", string channel = "stable", UpdateState? state = null,
        string? signWith = null, byte[]? tamper = null)
    {
        var bytes = manifest.ToJson();
        var signature = UpdateSignature.Sign(bytes, Keys.Private);
        if (signWith is not null)
        {
            signature = signWith;
        }

        return UpdatePolicy.Evaluate(tamper ?? bytes, signature, [PublicKey], installed, channel, "x64", Now, state ?? new UpdateState());
    }

    [Theory]
    [InlineData("0.3.0", "0.3.1", -1)]
    [InlineData("0.3.0-rc2", "0.3.0", -1)]
    [InlineData("0.3.0-rc2", "0.3.0-rc10", -1)]
    [InlineData("0.3.0-beta", "0.3.0-rc1", -1)]
    [InlineData("0.10.0", "0.9.9", 1)]
    [InlineData("1.0", "1.0.0", 0)]
    [InlineData("0.3.0+abc.dirty", "0.3.0", 0)]
    [InlineData("v0.2.0-rc3", "0.2.0-rc3", 0)]
    public void Versions_are_ordered_as_semver_orders_them(string a, string b, int expected) =>
        Assert.Equal(expected, Math.Sign(AppVersion.TryParse(a)!.CompareTo(AppVersion.TryParse(b))));

    [Theory]
    [InlineData("")]
    [InlineData("x.y.z")]
    [InlineData("1")]
    [InlineData("1.2.3-")]
    [InlineData("1.-2.3")]
    public void A_version_that_is_not_one_is_not_parsed(string text) => Assert.Null(AppVersion.TryParse(text));

    [Fact]
    public void A_newer_signed_manifest_is_available_with_its_package()
    {
        var check = Check(Manifest());

        Assert.Equal(UpdateOutcome.Available, check.Outcome);
        Assert.Equal(1234, check.Package!.Size);
        Assert.Equal("Лучше.", check.Manifest!.NotesFor("ru"));
        Assert.Equal("Better things.", check.Manifest.NotesFor("de"));
    }

    [Fact]
    public void The_same_or_an_older_version_is_up_to_date()
    {
        Assert.Equal(UpdateOutcome.UpToDate, Check(Manifest("0.3.0")).Outcome);
        Assert.Equal(UpdateOutcome.UpToDate, Check(Manifest("0.2.9")).Outcome);
    }

    [Fact]
    public void A_manifest_signed_by_another_key_is_rejected()
    {
        var other = UpdateSignature.NewKeyPair();
        var forged = UpdateSignature.Sign(Manifest().ToJson(), other.Private);

        Assert.Equal("signature", Check(Manifest(), signWith: forged).Reason);
    }

    [Fact]
    public void A_manifest_changed_after_signing_is_rejected()
    {
        var tampered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Manifest().ToJson()).Replace("0.3.1", "9.9.9", StringComparison.Ordinal));

        Assert.Equal("signature", Check(Manifest(), tamper: tampered).Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("AAAA")]
    public void A_missing_or_malformed_signature_is_rejected(string signature) =>
        Assert.Equal("signature", Check(Manifest(), signWith: signature).Reason);

    [Fact]
    public void A_manifest_of_another_channel_is_rejected_even_when_signed()
    {
        Assert.Equal("channel", Check(Manifest(channel: "preview"), channel: "stable").Reason);
    }

    [Fact]
    public void An_expired_manifest_is_rejected_so_an_old_one_cannot_be_replayed()
    {
        Assert.Equal("expired", Check(Manifest(expires: Now.AddMinutes(-1))).Reason);
    }

    [Fact]
    public void A_manifest_older_than_one_already_accepted_is_a_replay()
    {
        Assert.Equal("replay", Check(Manifest(sequence: 9), state: new UpdateState(HighestSequence: 10)).Reason);
        Assert.Equal(UpdateOutcome.Available, Check(Manifest(sequence: 10), state: new UpdateState(HighestSequence: 10)).Outcome);
    }

    [Fact]
    public void A_prerelease_is_never_offered_on_the_stable_channel()
    {
        Assert.Equal("prerelease-on-stable", Check(Manifest("0.4.0-rc1")).Reason);
        Assert.Equal(UpdateOutcome.Available, Check(Manifest("0.4.0-rc1", channel: "preview"), channel: "preview").Outcome);
    }

    [Theory]
    [InlineData("http://example.org/a.msi")]
    [InlineData("ftp://example.org/a.msi")]
    [InlineData("file:///c:/a.msi")]
    [InlineData("not a url")]
    public void A_package_must_come_over_https(string url) => Assert.Equal("package-description", Check(Manifest(url: url)).Reason);

    [Fact]
    public void Loopback_http_is_allowed_for_a_local_test_server() =>
        Assert.Equal(UpdateOutcome.Available, Check(Manifest(url: "http://127.0.0.1:8099/a.msi")).Outcome);

    [Fact]
    public void A_hash_that_is_not_sha256_hex_is_rejected() => Assert.Equal("package-description", Check(Manifest(sha: new string('z', 64))).Reason);

    [Fact]
    public void A_release_without_a_package_for_this_architecture_is_rejected()
    {
        var manifest = Manifest() with { Packages = [new UpdatePackage("msi", "arm64", "https://example.org/a.msi", new string('a', 64), 5)] };

        Assert.Equal("no-package", Check(manifest).Reason);
    }

    [Fact]
    public void A_skipped_version_is_not_offered_but_a_newer_one_is()
    {
        var state = new UpdateState(SkippedVersion: "0.3.1");

        Assert.Equal(UpdateOutcome.Skipped, Check(Manifest("0.3.1"), state: state).Outcome);
        Assert.Equal(UpdateOutcome.Available, Check(Manifest("0.3.2"), state: state).Outcome);
    }

    [Fact]
    public void A_postponed_update_waits_until_its_time()
    {
        Assert.Equal(UpdateOutcome.Postponed, Check(Manifest(), state: new UpdateState(RemindAfterUtc: Now.AddHours(2))).Outcome);
        Assert.Equal(UpdateOutcome.Available, Check(Manifest(), state: new UpdateState(RemindAfterUtc: Now.AddHours(-2))).Outcome);
    }

    [Fact]
    public void A_newer_schema_is_refused_rather_than_misread()
    {
        Assert.Equal("schema", Check(Manifest() with { SchemaVersion = 2 }).Reason);
    }

    [Fact]
    public void Garbage_that_is_signed_is_still_unreadable()
    {
        var garbage = Encoding.UTF8.GetBytes("{ not json");
        var signature = UpdateSignature.Sign(garbage, Keys.Private);

        Assert.Equal("unreadable", UpdatePolicy.Evaluate(garbage, signature, [PublicKey], "0.3.0", "stable", "x64", Now, new UpdateState()).Reason);
    }

    [Fact]
    public void A_bad_key_in_the_trusted_list_does_not_break_the_good_one()
    {
        var bytes = Manifest().ToJson();
        var signature = UpdateSignature.Sign(bytes, Keys.Private);

        Assert.True(UpdateSignature.Verify(bytes, signature, ["%%%", "AAAA", PublicKey]));
    }
}
