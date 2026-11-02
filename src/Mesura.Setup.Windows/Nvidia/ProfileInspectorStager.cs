using System.Security.Cryptography;

namespace Mesura.Setup.Windows.Nvidia;

/// <summary>What staging NVIDIA Profile Inspector gave: the copy to run, or why none was made.</summary>
public sealed record StagedInspector(ProfileInspector? Inspector, string? Refusal);

/// <summary>
/// How an elevated Mesura runs a NVIDIA Profile Inspector that sits where programs running as the user can change it (a per-user winget package, which is where winget puts it unless asked
/// for <c>--scope machine</c>). It does not run that file. It reads it once, through a handle that stays open (so nothing can replace it or change it while it is read), and writes the
/// bytes it reads into a folder only administrators can write to while hashing them; the copy is run only if that hash is one of the builds listed here. NVIDIA Profile Inspector is not
/// Authenticode-signed (checked on 3.0.2.1, 3.0.2.2 and 3.0.2.3: "NotSigned"), so a signature and publisher cannot be the check; the hash of a published release can. The hashes are the SHA-256
/// of <c>nvidiaProfileInspector.exe</c> in the release assets <c>v3.0.2.1</c>, <c>v3.0.2.2</c> and <c>v3.0.2.3</c> of github.com/Orbmu2k/nvidiaProfileInspector (the zip's digest as GitHub lists
/// it matched the downloaded file each time). The two files beside it that the program reads (its <c>.config</c> and <c>Reference.xml</c>) are copied only when they are the release's own; any
/// other content is left out. A build not in the list is not run with administrator rights from a user-writable folder; nothing is started and the message says why and what to do.
/// </summary>
#pragma warning disable RS0030 // The NVIDIA tool owner's staging: files are written only into a protected folder made for the run (class remarks).
public sealed class ProfileInspectorStager(Func<string>? createFolder = null, IReadOnlyDictionary<string, string>? knownExecutables = null,
    IReadOnlyDictionary<string, IReadOnlySet<string>>? knownSideFiles = null, IReleaseVerifier? verifier = null)
{
    public const string Executable = "nvidiaProfileInspector.exe";
    private const long MaxExecutableBytes = 32L << 20;
    private const long MaxSideFileBytes = 8L << 20;
    private const long MaxImportBytes = 16L << 20;

    /// <summary>SHA-256 of the executable of each verified release, with the version it is.</summary>
    public static readonly IReadOnlyDictionary<string, string> KnownReleases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["1EBD8129B3C564BF226291FB3344819FD59668066F0C5E03334A69A04A62859E"] = "3.0.2.1",
        ["071F38BFEEC4FAB0A33261F7FA010940E2C3C96FB21F03D18D490110DDB90B03"] = "3.0.2.2",
        ["355E491AA6C9956BED62204533E6B2FD750D4283D118713826F724EA6BC5CD9D"] = "3.0.2.3",
    };

    /// <summary>SHA-256 of the files beside the executable that the release ships and the program reads.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> KnownSideFilesOfReleases = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
    {
        ["nvidiaProfileInspector.exe.config"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "051099983B896673909E01A1F631B6652ABB88DA95C9F06F3EFEF4BE033091FA" },
        ["Reference.xml"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "0EA7B055AEE5C543047243D2DD7ABDD1B8C6D96F5D2B7BB5FE17BE8130E005EF" },
    };

    private readonly Func<string> _createFolder = createFolder ?? (() => ProtectedToolFolder.Fresh("nvpi"));
    private readonly IReadOnlyDictionary<string, string> _known = knownExecutables ?? KnownReleases;
    private readonly IReadOnlyDictionary<string, IReadOnlySet<string>> _side = knownSideFiles ?? KnownSideFilesOfReleases;
    private readonly IReleaseVerifier _verifier = verifier ?? new GitHubReleaseVerifier();

    /// <summary>A new folder only administrators can write to (see <see cref="ProtectedToolFolder"/>), or the refusal in words.</summary>
    public (string? Folder, string? Refusal) NewFolder()
    {
        try
        {
            return (_createFolder(), null);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return (null, $"Mesura couldn't prepare a safe place to run NVIDIA Profile Inspector as administrator ({e.Message}).");
        }
    }

    /// <summary>
    /// Copies <paramref name="source"/> (and the files beside it that are the release's own) into a new protected folder, checking the hash of the bytes written. The result's inspector
    /// lives in that folder; <see cref="Cleanup"/> removes it afterwards.
    /// </summary>
    public StagedInspector Stage(ProfileInspector source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var (folder, refusal) = NewFolder();
        if (folder is null)
        {
            return new StagedInspector(null, refusal);
        }

        var sourceFolder = Path.GetDirectoryName(source.Path)!;
        try
        {
            var staged = Path.Combine(folder, Executable);
            var sha = CopyHashed(source.Path, staged, MaxExecutableBytes);
            // A build on the list needs no network. A newer one is trusted when it is, byte for byte, what the project published for its own version: nobody has to add its hash.
            if (!_known.ContainsKey(sha) && !_verifier.Verify(source.Version, sha, cancellationToken, out var why))
            {
                Remove(folder);
                return new StagedInspector(null, NotConfirmed(source, why));
            }

            // Whatever was read is what runs: the copy is hashed once more where it sits, so a failed or partial write cannot pass.
            if (!string.Equals(HashOf(staged), sha, StringComparison.OrdinalIgnoreCase))
            {
                Remove(folder);
                return new StagedInspector(null, "The verified copy of NVIDIA Profile Inspector did not read back as written, so it was not run.");
            }

            foreach (var (name, allowed) in _side)
            {
                var from = Path.Combine(sourceFolder, name);
                if (!File.Exists(from))
                {
                    continue;
                }

                var to = Path.Combine(folder, name);
                if (!allowed.Contains(CopyHashed(from, to, MaxSideFileBytes)))
                {
                    File.Delete(to); // not the release's own: left out, never run beside the program
                }
            }

            return new StagedInspector(new ProfileInspector(staged, source.Version), null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Remove(folder);
            return new StagedInspector(null, $"NVIDIA Profile Inspector at {source.Path} couldn't be read to make a verified copy ({e.Message}).");
        }
    }

    /// <summary>
    /// Copies the profile file to import into <paramref name="folder"/> (one only administrators can write to), reading the user's file once through a handle that blocks writers, so what the
    /// elevated tool imports is what was read here and not whatever the file is a moment later.
    /// </summary>
    /// <returns>The copy's path, or the reason it couldn't be made.</returns>
    public (string? Path, string? Error) StageImportFile(string folder, string nipPath)
    {
        try
        {
            var target = Path.Combine(folder, "import.nip");
            CopyHashed(nipPath, target, MaxImportBytes);
            return (target, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, $"The profile file {nipPath} couldn't be read for the import ({e.Message}).");
        }
    }

    /// <summary>Removes what a run staged. Best effort: a folder in use is left for the next run to clear.</summary>
    public void Cleanup(string folder) => Remove(folder);

    private static string NotConfirmed(ProfileInspector source, string why) =>
        $"This copy of NVIDIA Profile Inspector (version {source.Version}) can't be confirmed as the official release ({why}), so Mesura doesn't run it with administrator rights: "
        + "a program you run could have replaced it. Reading your profiles still works. Update it (winget upgrade --id Orbmu2k.nvidiaProfileInspector), or connect to the internet, and run the setup again.";

    private static string CopyHashed(string from, string to, long limit)
    {
        // FileShare.Read: while this handle is open nobody can write to, delete or rename the file, so the bytes hashed are the bytes copied and nothing else.
        using var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > limit)
        {
            throw new IOException($"{from} is larger than {limit >> 20} MB.");
        }

        using var output = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            output.Write(buffer, 0, read);
        }

        output.Flush(flushToDisk: true);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string HashOf(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void Remove(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // In use by a program that is still running: the next run clears it.
        }
    }
}
