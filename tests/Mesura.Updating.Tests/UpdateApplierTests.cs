using System.Security.Cryptography;
using System.Text;
using Mesura.Core.Update;
using Mesura.Updating;

namespace Mesura.Updating.Tests;

public sealed class UpdateApplierTests : IDisposable
{
    private static readonly (byte[] Private, byte[] Public) Keys = UpdateSignature.NewKeyPair();
    private static readonly string[] Trusted = [Convert.ToBase64String(Keys.Public)];

    private sealed class FakeRunner : IInstallerRunner
    {
        public int ExitCode { get; set; }

        public bool AppCloses { get; set; } = true;

        public List<string> Arguments { get; } = [];

        public List<string> Restarted { get; } = [];

        public bool Ran { get; private set; }

        public int Waited { get; private set; }

        public void WaitUntilReplaceable(ApplyRequest request, TimeSpan timeout) => Waited++;

        public int Run(ApplyRequest request, IReadOnlyList<string> arguments)
        {
            Ran = true;
            Arguments.AddRange(arguments);
            return ExitCode;
        }

        public bool WaitForExit(int processId, TimeSpan timeout) => AppCloses;

        public void Restart(string executable, string arguments) => Restarted.Add(arguments);
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mesura-apply-" + Guid.NewGuid().ToString("N")[..8], "Папка с пробелом");
    private readonly byte[] _package = RandomNumberGenerator.GetBytes(50_000);
    private readonly UpdateStore _store;
    private readonly FakeRunner _runner = new();

    public UpdateApplierTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new UpdateStore(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_dir)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private ApplyRequest Stage(string version = "0.3.1", string from = "0.3.0", string? sha = null, InstallScope scope = InstallScope.User, bool tamperManifest = false, string? folder = null, bool expired = false)
    {
        var manifest = new UpdateManifest(1, "stable", version, 1, DateTimeOffset.UtcNow, expired ? DateTimeOffset.UtcNow.AddDays(-1) : DateTimeOffset.UtcNow.AddDays(9), new Dictionary<string, string> { ["en"] = "n" },
            [new UpdatePackage("msi", "x64", "https://example.org/a.msi", sha ?? Convert.ToHexStringLower(SHA256.HashData(_package)), _package.Length)]);
        var bytes = manifest.ToJson();
        var signature = UpdateSignature.Sign(bytes, Keys.Private);
        if (tamperManifest)
        {
            bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("example.org", "evil.example", StringComparison.Ordinal));
        }

        _store.Write(UpdateStore.ManifestFile, bytes);
        _store.WriteText(UpdateStore.SignatureFile, signature);
        _store.Write("pkg.msi", _package);
        return new ApplyRequest(_store.PathOf("pkg.msi"), _store.PathOf(UpdateStore.ManifestFile), _store.PathOf(UpdateStore.SignatureFile), 1234,
            Path.Combine(_dir, "Mesura.exe"), from, version, _store.PathOf("install.log"), scope, Trusted, folder);
    }

    [Fact]
    public void A_good_update_runs_the_installer_records_success_and_restarts_the_new_version()
    {
        var result = new UpdateApplier(_store, _runner).Apply(Stage());

        Assert.Equal(UpdateResult.Succeeded, result.Status);
        Assert.True(_runner.Ran);
        Assert.Equal(["--updated-from 0.3.0"], _runner.Restarted);
        Assert.Equal(UpdateResult.Succeeded, UpdateResult.TryRead(_store.Read(UpdateStore.ResultFile))!.Status);
    }

    [Fact]
    public void An_expired_manifest_is_not_applied_even_if_its_signature_is_good()
    {
        var result = new UpdateApplier(_store, _runner).Apply(Stage(expired: true));

        Assert.Equal((UpdateResult.Failed, "expired"), (result.Status, result.Reason));
        Assert.False(_runner.Ran);
    }

    [Fact]
    public void A_per_user_install_is_updated_per_user_and_a_machine_install_for_all_users()
    {
        new UpdateApplier(_store, _runner).Apply(Stage(scope: InstallScope.User));
        Assert.Contains("MSIINSTALLPERUSER=1", _runner.Arguments);
        Assert.DoesNotContain("ALLUSERS=1", _runner.Arguments);

        var machine = new FakeRunner();
        new UpdateApplier(_store, machine).Apply(Stage(scope: InstallScope.Machine));
        Assert.Contains("ALLUSERS=1", machine.Arguments);
        Assert.DoesNotContain("MSIINSTALLPERUSER=1", machine.Arguments);
    }

    [Fact]
    public void The_install_folder_is_kept_even_with_spaces_and_cyrillic()
    {
        var folder = @"C:\Программы\Мой Mesura";

        new UpdateApplier(_store, _runner).Apply(Stage(folder: folder));

        Assert.Contains("INSTALLFOLDER=" + folder, _runner.Arguments);
    }

    [Fact]
    public void The_installer_waits_for_the_files_to_be_free_before_it_runs()
    {
        new UpdateApplier(_store, _runner).Apply(Stage());

        Assert.Equal(1, _runner.Waited);
    }

    [Fact]
    public void A_restart_that_was_needed_is_recorded_so_the_app_can_say_so()
    {
        _runner.ExitCode = 3010;

        var result = new UpdateApplier(_store, _runner).Apply(Stage());

        Assert.Equal((UpdateResult.Succeeded, "restart-needed"), (result.Status, result.Reason));
    }

    [Theory]
    [InlineData(3010)]
    [InlineData(1641)]
    public void A_restart_needed_is_still_a_success(int code)
    {
        _runner.ExitCode = code;

        Assert.Equal(UpdateResult.Succeeded, new UpdateApplier(_store, _runner).Apply(Stage()).Status);
    }

    [Theory]
    [InlineData(1603, "installer-failed-rolled-back")]
    [InlineData(1602, "cancelled")]
    [InlineData(1618, "installer-busy")]
    [InlineData(1925, "needs-administrator")]
    [InlineData(1, "installer-error")]
    public void A_failed_install_is_recorded_with_its_reason_and_the_old_version_is_restarted(int code, string reason)
    {
        _runner.ExitCode = code;

        var result = new UpdateApplier(_store, _runner).Apply(Stage());

        Assert.Equal((UpdateResult.Failed, reason, code), (result.Status, result.Reason, result.ExitCode));
        Assert.Equal(["--update-failed"], _runner.Restarted);
    }

    [Fact]
    public void A_manifest_changed_after_it_was_downloaded_stops_everything_before_the_installer()
    {
        var result = new UpdateApplier(_store, _runner).Apply(Stage(tamperManifest: true));

        Assert.Equal("signature", result.Reason);
        Assert.False(_runner.Ran);
    }

    [Fact]
    public void A_package_changed_after_it_was_downloaded_is_not_run()
    {
        var request = Stage();
        _store.Write("pkg.msi", [.. _package, 1]);

        var result = new UpdateApplier(_store, _runner).Apply(request);

        Assert.Equal("package-hash", result.Reason);
        Assert.False(_runner.Ran);
        Assert.Equal(["--update-failed"], _runner.Restarted);
    }

    [Fact]
    public void A_package_that_is_not_newer_is_not_run()
    {
        var result = new UpdateApplier(_store, _runner).Apply(Stage(version: "0.3.0", from: "0.3.0"));

        Assert.Equal("not-newer", result.Reason);
        Assert.False(_runner.Ran);
    }

    [Fact]
    public void A_manifest_for_another_version_than_asked_is_not_run()
    {
        var request = Stage(version: "0.3.1") with { ToVersion = "0.9.0" };

        Assert.Equal("manifest-mismatch", new UpdateApplier(_store, _runner).Apply(request).Reason);
        Assert.False(_runner.Ran);
    }

    [Fact]
    public void While_the_app_is_still_open_nothing_is_touched_and_nothing_is_restarted()
    {
        _runner.AppCloses = false;

        var result = new UpdateApplier(_store, _runner).Apply(Stage());

        Assert.Equal("app-did-not-close", result.Reason);
        Assert.False(_runner.Ran);
        Assert.Empty(_runner.Restarted);
    }

    [Fact]
    public void Missing_staged_files_are_a_failure_not_a_crash()
    {
        var request = Stage() with { PackagePath = Path.Combine(_dir, "gone.msi") };

        Assert.Equal("staged-files-unreadable", new UpdateApplier(_store, _runner).Apply(request).Reason);
        request = Stage() with { ManifestPath = Path.Combine(_dir, "gone.json") };
        Assert.Equal("staged-files-unreadable", new UpdateApplier(_store, _runner).Apply(request).Reason);
    }

    [Fact]
    public void Cleaning_the_store_keeps_the_result_and_the_logs()
    {
        Stage();
        _store.WriteText(UpdateStore.ResultFile, "{}");
        _store.WriteText("install.log", "log");

        _store.CleanExcept();

        Assert.False(_store.Exists("pkg.msi"));
        Assert.True(_store.Exists(UpdateStore.ResultFile));
        Assert.True(_store.Exists("install.log"));
    }
}
