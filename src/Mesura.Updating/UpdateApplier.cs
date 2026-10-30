using System.Text.Json;
using Mesura.Core.Update;

namespace Mesura.Updating;

/// <summary>What an update left behind for the app to say on its next start (the file <see cref="UpdateStore.ResultFile"/>).</summary>
public sealed record UpdateResult(string Status, string From, string To, string Reason, int ExitCode, string Log, DateTimeOffset At)
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static UpdateResult? TryRead(byte[]? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<UpdateResult>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>What the updater was asked to apply.</summary>
public sealed record ApplyRequest(
    string PackagePath,
    string ManifestPath,
    string SignaturePath,
    int AppProcessId,
    string RestartExecutable,
    string FromVersion,
    string ToVersion,
    string LogPath,
    InstallScope Scope,
    IReadOnlyList<string> TrustedKeys,
    string? InstallFolder = null,
    bool NoElevate = false,
    string? Language = null);

/// <summary>Runs the Windows Installer (a stand-in in tests).</summary>
public interface IInstallerRunner
{
    /// <returns>msiexec's exit code.</returns>
    int Run(ApplyRequest request, IReadOnlyList<string> arguments);

    /// <summary>
    /// Waits (up to <paramref name="timeout"/>) until the installed files can be replaced. Right after Mesura has run, Windows' own Inventory and
    /// Compatibility Appraisal service reads the program for a while; the installer then asks for a restart ("critical application holds files") and
    /// waits on a box nobody may see. Waiting for the files to be released avoids it; after the timeout the installer runs anyway.
    /// </summary>
    void WaitUntilReplaceable(ApplyRequest request, TimeSpan timeout);

    /// <summary>Waits for the app to exit; <c>false</c> when it is still running after <paramref name="timeout"/>.</summary>
    bool WaitForExit(int processId, TimeSpan timeout);

    /// <summary>Starts the app again (the new version, or the old one after a failure) with the arguments given.</summary>
    void Restart(string executable, string arguments);
}

/// <summary>
/// The update itself, run by the updater (a copy of the command line outside the program folder, so the installer may replace
/// the program folder whole). Order matters and every step can stop it without harm:
/// 1) wait for the app to close (nothing is touched while it runs);
/// 2) verify again what was staged: the manifest's signature, the package's size and SHA-256, the version it brings;
/// 3) let the Windows Installer replace the installed version. It is transactional: if it fails part-way it puts the old
///    version back, which is the rollback;
/// 4) record the result for the app and start Mesura again: the new version on success, the old one on failure.
/// </summary>
public sealed class UpdateApplier(UpdateStore store, IInstallerRunner runner)
{
    public static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan ReplaceableTimeout = TimeSpan.FromSeconds(90);

    private static readonly HashSet<int> Success = [0, 1641, 3010];

    public UpdateResult Apply(ApplyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = Run(request);
        try
        {
            store.WriteText(UpdateStore.ResultFile, JsonSerializer.Serialize(result, UpdateResult.Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The result is a courtesy to the next start; the update itself has already been decided.
        }

        if (result.Reason != "app-did-not-close")
        {
            try
            {
                runner.Restart(request.RestartExecutable, (result.Status == UpdateResult.Succeeded ? $"--updated-from {request.FromVersion}" : "--update-failed") + (request.NoElevate ? " --no-elevate" : ""));
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Mesura isn't started again; the user starts it. The result file still says what happened.
            }
        }

        return result;
    }

    private UpdateResult Run(ApplyRequest r)
    {
        UpdateResult Fail(string reason, int code = 0) => new(UpdateResult.Failed, r.FromVersion, r.ToVersion, reason, code, r.LogPath, DateTimeOffset.Now);

        if (!runner.WaitForExit(r.AppProcessId, CloseTimeout))
        {
            return Fail("app-did-not-close");
        }

        // What is run is what was signed: the manifest again, then the package against the manifest.
        byte[] manifestBytes;
        string signature;
        try
        {
            manifestBytes = File.ReadAllBytes(r.ManifestPath);
            signature = File.ReadAllText(r.SignaturePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Fail("staged-files-unreadable");
        }

        if (!UpdateSignature.Verify(manifestBytes, signature, r.TrustedKeys))
        {
            return Fail("signature");
        }

        var manifest = UpdateManifest.TryParse(manifestBytes);
        var package = manifest?.PackageFor("x64", r.Language);
        if (manifest is null || package is null || manifest.Version != r.ToVersion)
        {
            return Fail("manifest-mismatch");
        }

        if (manifest.ExpiresUtc <= DateTimeOffset.UtcNow)
        {
            return Fail("expired");
        }

        if (!(AppVersion.TryParse(manifest.Version) is { } to && AppVersion.TryParse(r.FromVersion) is { } from && to > from))
        {
            return Fail("not-newer");
        }

        try
        {
            using var stream = File.OpenRead(r.PackagePath);
            if (stream.Length != package.Size || !string.Equals(UpdateSignature.Sha256Hex(stream), package.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return Fail("package-hash");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Fail("staged-files-unreadable");
        }

        runner.WaitUntilReplaceable(r, ReplaceableTimeout);

        var arguments = new List<string>
        {
            "/i", r.PackagePath, "/qb!", "/norestart", "/l*v", r.LogPath,
        };
        arguments.AddRange(r.Scope == InstallScope.Machine ? ["ALLUSERS=1"] : ["ALLUSERS=2", "MSIINSTALLPERUSER=1"]);
        if (r.InstallFolder is { Length: > 0 } folder)
        {
            arguments.Add("INSTALLFOLDER=" + Path.TrimEndingDirectorySeparator(folder));
        }

        int code;
        try
        {
            code = runner.Run(r, arguments);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return Fail("installer-not-started");
        }

        return Success.Contains(code)
            ? new UpdateResult(UpdateResult.Succeeded, r.FromVersion, r.ToVersion, code is 3010 or 1641 ? "restart-needed" : "", code, r.LogPath, DateTimeOffset.Now)
            : Fail(code switch { 1602 => "cancelled", 1618 => "installer-busy", 1925 => "needs-administrator", 1603 => "installer-failed-rolled-back", _ => "installer-error" }, code);
    }
}
