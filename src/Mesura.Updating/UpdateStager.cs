using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Mesura.Core.Support;
using Mesura.Core.Update;

namespace Mesura.Updating;

/// <summary>Where the elevated updater keeps what it runs: a folder only administrators can change (or a test's stand-in).</summary>
public interface IProtectedFolders
{
    /// <summary>A new, empty folder named <paramref name="name"/> that no one but administrators and the system can write to.</summary>
    string Create(string name);
}

/// <summary>What the elevated stage was asked to take over (all of it comes from the user's own, unprotected update folder).</summary>
public sealed record StageRequest(string PackagePath, string ManifestPath, string SignaturePath);

/// <summary>The update as the elevated stage left it in the protected folder: nothing there can be changed by a user.</summary>
public sealed record StagedUpdate(string Folder, string Executable, string Package, string Manifest, string Signature, string Log, string ToVersion);

public sealed record StageOutcome(StagedUpdate? Staged, string? Failure)
{
    public static StageOutcome Fail(string reason) => new(null, reason);
}

/// <summary>
/// The first, elevated half of a machine-wide update (0.3.0-rc2, docs/security.md). Until rc1 the app copied the command line into its own
/// per-user data folder and started that copy elevated: anything running as the user could replace the copy between the copy and the
/// start, and would then run with administrator rights. Now the elevated process is the installed command line (in Program Files, which
/// only administrators can change) and it does this: it checks the manifest's signature, copies the package into a folder only
/// administrators can write <i>while hashing what it writes</i> (so nothing can change between the check and the use), and copies itself
/// there. The updater that runs the installer is that protected copy, started with the verified files. The version it is updated from,
/// the install folder and the executable it restarts all come from what the installer registered (HKLM), never from the arguments.
/// </summary>
#pragma warning disable RS0030 // The update component's staging writer: only inside the protected updates folder (class remarks).
public static class UpdateStager
{
    private const int MaxManifestBytes = 256 * 1024;
    private const int MaxSignatureBytes = 4096;
    private const long MaxPackageBytes = 1L << 30;

    [SupportedOSPlatform("windows")]
    public static StageOutcome Stage(
        StageRequest request,
        InstallInfo install,
        string runningVersion,
        IReadOnlyList<string> trustedKeys,
        DateTimeOffset now,
        IProtectedFolders folders,
        Func<string, string?>? folderTrust = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(folders);
        folderTrust ??= ProgramFolderTrust.Why;

        if (install.Scope != InstallScope.Machine)
        {
            return StageOutcome.Fail("not-machine-install");
        }

        if (folderTrust(install.Folder) is not null)
        {
            return StageOutcome.Fail("install-folder-not-protected"); // the program that would run elevated lives where users can change it
        }

        byte[] manifestBytes;
        string signature;
        try
        {
            manifestBytes = ReadAtMost(request.ManifestPath, MaxManifestBytes);
            signature = System.Text.Encoding.UTF8.GetString(ReadAtMost(request.SignaturePath, MaxSignatureBytes));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return StageOutcome.Fail("staged-files-unreadable");
        }

        if (!UpdateSignature.Verify(manifestBytes, signature, trustedKeys))
        {
            return StageOutcome.Fail("signature");
        }

        if (UpdateManifest.TryParse(manifestBytes) is not { } manifest || manifest.SchemaVersion != UpdateManifest.CurrentSchema || manifest.PackageFor("x64", install.Language) is not { } package)
        {
            return StageOutcome.Fail("manifest-mismatch");
        }

        if (manifest.ExpiresUtc <= now)
        {
            return StageOutcome.Fail("expired");
        }

        if (!(AppVersion.TryParse(manifest.Version) is { } to && AppVersion.TryParse(runningVersion) is { } from && to > from))
        {
            return StageOutcome.Fail("not-newer");
        }

        if (package.Size <= 0 || package.Size > MaxPackageBytes || package.Sha256.Length != 64 || !package.Sha256.All(Uri.IsHexDigit))
        {
            return StageOutcome.Fail("package-description");
        }

        if (!File.Exists(install.CommandLine))
        {
            return StageOutcome.Fail("command-line-missing");
        }

        string folder;
        try
        {
            folder = folders.Create("run-" + Guid.NewGuid().ToString("N")[..8]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return StageOutcome.Fail("protected-folder:" + e.Message);
        }

        var staged = new StagedUpdate(folder, Path.Combine(folder, "mesura.exe"), Path.Combine(folder, "Mesura.msi"), Path.Combine(folder, UpdateStore.ManifestFile),
            Path.Combine(folder, UpdateStore.SignatureFile), Path.Combine(folder, "update-install.log"), manifest.Version);
        try
        {
            // The hash is taken over the bytes written into the protected folder, so it is the hash of what the installer will read.
            using (var source = new FileStream(request.PackagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var target = new FileStream(staged.Package, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[1 << 16];
                long total = 0;
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > package.Size)
                    {
                        break;
                    }

                    hash.AppendData(buffer, 0, read);
                    target.Write(buffer, 0, read);
                }

                if (total != package.Size || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), package.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    target.Close();
                    Discard(folder);
                    return StageOutcome.Fail("package-hash");
                }
            }

            File.WriteAllBytes(staged.Manifest, manifestBytes);
            File.WriteAllText(staged.Signature, signature);
            File.Copy(install.CommandLine, staged.Executable, overwrite: false);
            return new StageOutcome(staged, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Discard(folder);
            return StageOutcome.Fail("staging-failed:" + e.Message);
        }
    }

    private static byte[] ReadAtMost(string path, int limit)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > limit)
        {
            throw new InvalidDataException("too large");
        }

        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void Discard(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left for the next stage to clear.
        }
    }
}

/// <summary>
/// The protected folders on a real PC: <c>%ProgramData%\MesuraUpdates\run-*</c>, created with an access list that gives only the system and
/// administrators write access (users may read, so the install log can be shown). The result is checked after it is made, not trusted:
/// if someone made the parent first, or it can otherwise be changed, a fresh unpredictable parent is made instead.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProgramDataProtectedFolders(string? programData = null) : IProtectedFolders
{
    private readonly string _programData = programData ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    /// <summary>The rights a protected updates folder grants: the system and administrators everything, users only to read.</summary>
    public static IReadOnlyList<(SecurityIdentifier Who, FileSystemRights Rights)> Grants() =>
    [
        (new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl),
        (new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl),
        (new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute),
    ];

    public string Create(string name)
    {
        var root = Protected(Path.Combine(_programData, "MesuraUpdates")) ?? Protected(Path.Combine(_programData, "MesuraUpdates-" + Guid.NewGuid().ToString("N")))
            ?? throw new InvalidOperationException("no protected folder could be made under " + _programData);
        ClearOlder(root);
        return Protected(Path.Combine(root, name)) ?? throw new InvalidOperationException("the updates folder could not be protected");
    }

    /// <returns>The folder once it exists with the protected access list, else <c>null</c> (and nothing of it is trusted).</returns>
    private static string? Protected(string path)
    {
        if (!Directory.Exists(path))
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var (who, rights) in Grants())
            {
                security.AddAccessRule(new FileSystemAccessRule(who, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            }

            new DirectoryInfo(path).Create(security);
        }

        return ProgramFolderTrust.Why(path) is null ? path : null;
    }

    private static void ClearOlder(string root)
    {
        foreach (var old in Directory.EnumerateDirectories(root, "run-*"))
        {
            try
            {
                Directory.Delete(old, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Still in use by an updater that is running.
            }
        }
    }
}
