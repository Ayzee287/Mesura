using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Mesura.Core.Update;
using Mesura.Updating;

namespace Mesura.Updating.Tests;

/// <summary>
/// The elevated half of a machine-wide update: what it accepts from the user's folders and what it leaves in the protected one
/// (docs/security.md). The protected folder is a stand-in here (a test cannot be administrator); the access list it would be given is checked
/// separately, and a real one is made in the tester checklist.
/// </summary>
public sealed class UpdateStagerTests : IDisposable
{
    private static readonly (byte[] Private, byte[] Public) Keys = UpdateSignature.NewKeyPair();
    private static readonly (byte[] Private, byte[] Public) OtherKeys = UpdateSignature.NewKeyPair();
    private static readonly string[] Trusted = [Convert.ToBase64String(Keys.Public)];

    private sealed class TestFolders(string root) : IProtectedFolders
    {
        public List<string> Made { get; } = [];

        public string Create(string name)
        {
            var folder = Path.Combine(root, name);
            Directory.CreateDirectory(folder);
            Made.Add(folder);
            return folder;
        }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "mesura-stage-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly byte[] _package = RandomNumberGenerator.GetBytes(200_000);
    private readonly TestFolders _folders;
    private readonly InstallInfo _install;
    private readonly string _user;

    public UpdateStagerTests()
    {
        _user = Path.Combine(_root, "user");
        var program = Path.Combine(_root, "Program Files", "Mesura");
        Directory.CreateDirectory(_user);
        Directory.CreateDirectory(Path.Combine(program, "cli"));
        File.WriteAllText(Path.Combine(program, "cli", "mesura.exe"), "the installed command line");
        _install = new InstallInfo(InstallScope.Machine, program, "0.3.0");
        _folders = new TestFolders(Path.Combine(_root, "protected"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private StageRequest Offer(string version = "0.3.1", string? sha = null, long? size = null, bool expired = false, byte[]? signWith = null, bool tamper = false, byte[]? package = null)
    {
        package ??= _package;
        var manifest = new UpdateManifest(1, "stable", version, 1, DateTimeOffset.UtcNow, expired ? DateTimeOffset.UtcNow.AddDays(-1) : DateTimeOffset.UtcNow.AddDays(9),
            new Dictionary<string, string> { ["en"] = "n" }, [new UpdatePackage("msi", "x64", "https://example.org/a.msi", sha ?? Convert.ToHexStringLower(SHA256.HashData(_package)), size ?? _package.Length)]);
        var bytes = manifest.ToJson();
        var signature = UpdateSignature.Sign(bytes, signWith ?? Keys.Private);
        if (tamper)
        {
            bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("example.org", "evil.example", StringComparison.Ordinal));
        }

        File.WriteAllBytes(Path.Combine(_user, "m.json"), bytes);
        File.WriteAllText(Path.Combine(_user, "m.sig"), signature);
        File.WriteAllBytes(Path.Combine(_user, "p.msi"), package);
        return new StageRequest(Path.Combine(_user, "p.msi"), Path.Combine(_user, "m.json"), Path.Combine(_user, "m.sig"));
    }

    private StageOutcome Stage(StageRequest request, InstallInfo? install = null, string running = "0.3.0", Func<string, string?>? trust = null) =>
        UpdateStager.Stage(request, install ?? _install, running, Trusted, DateTimeOffset.UtcNow, _folders, trust ?? (_ => null));

    [Fact]
    public void A_good_update_is_copied_into_the_protected_folder_with_the_updater_and_the_verified_files()
    {
        var outcome = Stage(Offer());

        var staged = Assert.IsType<StagedUpdate>(outcome.Staged);
        Assert.Equal(_package, File.ReadAllBytes(staged.Package));
        Assert.Equal("the installed command line", File.ReadAllText(staged.Executable));
        Assert.True(File.Exists(staged.Manifest));
        Assert.True(File.Exists(staged.Signature));
        Assert.Equal("0.3.1", staged.ToVersion);
        Assert.StartsWith(Path.Combine(_root, "protected"), staged.Folder, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_staged_manifest_is_the_bytes_that_were_verified_not_a_second_read_of_the_users_file()
    {
        var request = Offer();
        var verified = File.ReadAllBytes(request.ManifestPath);

        var staged = Stage(request).Staged!;

        Assert.Equal(verified, File.ReadAllBytes(staged.Manifest));
    }

    [Fact]
    public void A_package_that_does_not_match_the_signed_hash_is_refused_and_leaves_nothing_behind()
    {
        var outcome = Stage(Offer(package: RandomNumberGenerator.GetBytes(_package.Length)));

        Assert.Equal("package-hash", outcome.Failure);
        Assert.Null(outcome.Staged);
        Assert.All(_folders.Made, folder => Assert.False(Directory.Exists(folder)));
    }

    [Fact]
    public void A_package_longer_than_the_manifest_says_is_refused()
    {
        var outcome = Stage(Offer(package: [.. _package, .. new byte[10]]));

        Assert.Equal("package-hash", outcome.Failure);
    }

    [Theory]
    [InlineData("wrong-key")]
    [InlineData("tampered-manifest")]
    public void An_unsigned_or_swapped_update_never_reaches_the_protected_folder(string how)
    {
        var request = how == "wrong-key" ? Offer(signWith: OtherKeys.Private) : Offer(tamper: true);

        var outcome = Stage(request);

        Assert.Equal("signature", outcome.Failure);
        Assert.Empty(_folders.Made); // nothing was even created
    }

    [Fact]
    public void An_expired_manifest_is_refused()
    {
        Assert.Equal("expired", Stage(Offer(expired: true)).Failure);
    }

    [Theory]
    [InlineData("0.3.0")]
    [InlineData("0.2.9")]
    public void Only_a_newer_version_than_the_one_running_is_taken_over(string offered)
    {
        Assert.Equal("not-newer", Stage(Offer(version: offered)).Failure);
    }

    [Fact]
    public void A_per_user_install_has_no_elevated_stage()
    {
        var user = _install with { Scope = InstallScope.User };

        Assert.Equal("not-machine-install", Stage(Offer(), user).Failure);
    }

    [Fact]
    public void An_install_folder_that_users_can_change_is_never_elevated()
    {
        var outcome = Stage(Offer(), trust: _ => "Users may write files here");

        Assert.Equal("install-folder-not-protected", outcome.Failure);
        Assert.Empty(_folders.Made);
    }

    [Fact]
    public void The_package_staged_is_the_one_in_the_language_the_install_was_made_in()
    {
        var russian = RandomNumberGenerator.GetBytes(150_000);
        var manifest = new UpdateManifest(1, "stable", "0.3.1", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(9), new Dictionary<string, string> { ["en"] = "n" },
        [
            new UpdatePackage("msi", "x64", "https://example.org/en.msi", Convert.ToHexStringLower(SHA256.HashData(_package)), _package.Length, "en"),
            new UpdatePackage("msi", "x64", "https://example.org/ru.msi", Convert.ToHexStringLower(SHA256.HashData(russian)), russian.Length, "ru"),
        ]);
        var bytes = manifest.ToJson();
        File.WriteAllBytes(Path.Combine(_user, "m.json"), bytes);
        File.WriteAllText(Path.Combine(_user, "m.sig"), UpdateSignature.Sign(bytes, Keys.Private));
        File.WriteAllBytes(Path.Combine(_user, "p.msi"), russian);
        var request = new StageRequest(Path.Combine(_user, "p.msi"), Path.Combine(_user, "m.json"), Path.Combine(_user, "m.sig"));

        Assert.Equal(russian, File.ReadAllBytes(Stage(request, _install with { Language = "ru" }).Staged!.Package));
        Assert.Equal("package-hash", Stage(request, _install with { Language = "en" }).Failure); // the English hash is what an English install expects
    }

    [Fact]
    public void A_manifest_that_is_far_too_large_is_not_read()
    {
        var request = Offer();
        File.WriteAllBytes(request.ManifestPath, new byte[300_000]);

        Assert.Equal("staged-files-unreadable", Stage(request).Failure);
    }

    [Fact]
    public void Users_may_read_the_protected_folder_but_only_the_system_and_administrators_may_write()
    {
        var grants = ProgramDataProtectedFolders.Grants();
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var writes = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.CreateFiles | FileSystemRights.Delete | FileSystemRights.ChangePermissions;

        Assert.Equal(FileSystemRights.ReadAndExecute, grants.Single(g => g.Who.Equals(users)).Rights);
        Assert.All(grants.Where(g => !g.Who.Equals(users)), g => Assert.Equal(FileSystemRights.FullControl, g.Rights));
        Assert.All(grants.Where(g => g.Who.Equals(users)), g => Assert.Equal(0, (int)(g.Rights & writes)));
        Assert.Equal(3, grants.Count);
    }

    [Fact]
    public void Someone_who_is_not_an_administrator_cannot_make_a_protected_folder_at_all()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            return; // the same call succeeds elevated: that run is the tester checklist's
        }

        var programData = Path.Combine(_root, "ProgramData");
        Directory.CreateDirectory(programData);

        // The folder it makes is owned by the caller, so it isn't trusted afterwards: the result is checked, not assumed.
        Assert.Throws<InvalidOperationException>(() => new ProgramDataProtectedFolders(programData).Create("run-test"));
        foreach (var made in Directory.EnumerateDirectories(programData, "*", SearchOption.AllDirectories))
        {
            var security = new DirectoryInfo(made).GetAccessControl();
            security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            new DirectoryInfo(made).SetAccessControl(security);
        }
    }
}
