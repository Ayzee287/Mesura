using System.Globalization;
using System.Security.Cryptography;
using Mesura.Core.Update;

namespace Mesura.ReleaseTool;

/// <summary>
/// <c>mesura-release keygen --dir &lt;folder&gt;</c> makes the update signing key pair (once; the folder must be outside the repository).
/// <c>mesura-release manifest ...</c> writes <c>update-&lt;channel&gt;.json</c> and its <c>.sig</c> for a release's installer.
/// <c>mesura-release verify ...</c> checks a manifest and signature against a public key, as the app would.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                return Usage();
            }

            var options = Options(args.Skip(1));
            return args[0] switch
            {
                "keygen" => KeyGen(options),
                "manifest" => Manifest(options),
                "verify" => Verify(options),
                "keycheck" => KeyCheck(options),
                _ => Usage(),
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or FormatException or InvalidOperationException)
        {
            Console.Error.WriteLine("mesura-release: " + e.Message);
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("""
            mesura-release keygen   --dir <folder outside the repository>
            mesura-release manifest --channel stable|preview --version <x.y.z[-rcN]> --msi <file> --url <https url of the msi>
                                    [--msi-ru <file> --url-ru <https url of the Russian msi>]
                                    --notes-en <file> [--notes-ru <file>] --key <update-signing.pk8> --out <folder>
                                    [--days 180] [--arch x64]
            mesura-release keycheck --key <update-signing.pk8>      (does this private key belong to a public key the app trusts? prints no secret)
            mesura-release verify   --manifest <file> --sig <file> --pub <base64 public key or file>
            """);
        return 2;
    }

    private static Dictionary<string, string> Options(IEnumerable<string> args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            if (!list[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= list.Count)
            {
                throw new ArgumentException($"Unexpected argument '{list[i]}'.");
            }

            map[list[i][2..]] = list[++i];
        }

        return map;
    }

    private static string Need(Dictionary<string, string> o, string name) =>
        o.TryGetValue(name, out var value) && value.Length > 0 ? value : throw new ArgumentException($"--{name} is required.");

    private static int KeyGen(Dictionary<string, string> o)
    {
        var dir = Path.GetFullPath(Need(o, "dir"));
        var repo = AppContext.BaseDirectory;
        if (Directory.Exists(Path.Combine(dir, ".git")) || IsInsideGit(dir))
        {
            throw new ArgumentException("The signing key must be kept outside any git repository.");
        }

        Directory.CreateDirectory(dir);
        var privatePath = Path.Combine(dir, "update-signing.pk8");
        if (File.Exists(privatePath))
        {
            throw new InvalidOperationException($"{privatePath} already exists: a signing key is made once. Delete it by hand if you mean to replace it (every installed app then needs the new public key).");
        }

        var (priv, pub) = UpdateSignature.NewKeyPair();
        File.WriteAllBytes(privatePath, priv);
        File.WriteAllText(Path.Combine(dir, "update-signing.pub"), Convert.ToBase64String(pub));
        Console.WriteLine("Private key written to " + privatePath + " (keep it secret and backed up; it is never printed).");
        Console.WriteLine("Public key (put it in Mesura.Core/Update/UpdateKeys.cs):");
        Console.WriteLine(Convert.ToBase64String(pub));
        _ = repo;
        return 0;
    }

    private static bool IsInsideGit(string dir)
    {
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            if (Directory.Exists(Path.Combine(d.FullName, ".git")))
            {
                return true;
            }
        }

        return false;
    }

    private static int Manifest(Dictionary<string, string> o)
    {
        var channel = Need(o, "channel").ToLowerInvariant();
        if (channel is not ("stable" or "preview"))
        {
            throw new ArgumentException("--channel is stable or preview.");
        }

        var version = Need(o, "version");
        var parsed = AppVersion.TryParse(version) ?? throw new ArgumentException($"'{version}' is not a version.");
        if (channel == "stable" && parsed.IsPrerelease)
        {
            throw new ArgumentException("A pre-release version is published on the preview channel only.");
        }

        var msi = Path.GetFullPath(Need(o, "msi"));
        var url = Need(o, "url");
        if (!url.StartsWith("https://", StringComparison.Ordinal) && !url.StartsWith("http://127.0.0.1", StringComparison.Ordinal) && !url.StartsWith("http://localhost", StringComparison.Ordinal))
        {
            throw new ArgumentException("--url must be https (or loopback http for a local test).");
        }

        var notes = new Dictionary<string, string> { ["en"] = File.ReadAllText(Need(o, "notes-en")).Trim() };
        if (o.TryGetValue("notes-ru", out var ru))
        {
            notes["ru"] = File.ReadAllText(ru).Trim();
        }

        var days = o.TryGetValue("days", out var d) ? int.Parse(d, CultureInfo.InvariantCulture) : 180;
        var arch = o.TryGetValue("arch", out var a) ? a : "x64";
        using var stream = File.OpenRead(msi);
        var size = stream.Length;
        var sha = Convert.ToHexStringLower(SHA256.HashData(stream));
        var now = DateTimeOffset.UtcNow;
        var packages = new List<UpdatePackage> { new("msi", arch, url, sha, size, "en") }; // English first: an older app takes the first package and keeps working
        if (o.TryGetValue("msi-ru", out var msiRu))
        {
            var urlRu = Need(o, "url-ru");
            if (!urlRu.StartsWith("https://", StringComparison.Ordinal) && !urlRu.StartsWith("http://127.0.0.1", StringComparison.Ordinal) && !urlRu.StartsWith("http://localhost", StringComparison.Ordinal))
            {
                throw new ArgumentException("--url-ru must be https (or loopback http for a local test).");
            }

            using var streamRu = File.OpenRead(Path.GetFullPath(msiRu));
            packages.Add(new UpdatePackage("msi", arch, urlRu, Convert.ToHexStringLower(SHA256.HashData(streamRu)), streamRu.Length, "ru"));
        }

        var manifest = new UpdateManifest(UpdateManifest.CurrentSchema, channel, version, now.ToUnixTimeSeconds(), now, now.AddDays(days), notes, packages);
        var bytes = manifest.ToJson();
        var signature = UpdateSignature.Sign(bytes, File.ReadAllBytes(Need(o, "key")));

        var outDir = Directory.CreateDirectory(Need(o, "out")).FullName;
        File.WriteAllBytes(Path.Combine(outDir, $"update-{channel}.json"), bytes);
        File.WriteAllText(Path.Combine(outDir, $"update-{channel}.json.sig"), signature);
        Console.WriteLine($"update-{channel}.json for {version} ({packages.Count} package(s); English {size} bytes, sha256 {sha}) signed; valid for {days} days; sequence {manifest.Sequence}.");
        return 0;
    }

    /// <summary>Used after a restore from backup or before a release: proves the key file is intact and is the one the app trusts, without printing any of it.</summary>
    private static int KeyCheck(Dictionary<string, string> o)
    {
        var path = Path.GetFullPath(Need(o, "key"));
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(File.ReadAllBytes(path), out _);
        var publicKey = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(publicKey)))[..16];
        var index = Mesura.Core.Update.UpdateKeys.Trusted.ToList().IndexOf(publicKey);
        var probe = System.Text.Encoding.UTF8.GetBytes("mesura-keycheck");
        var signed = UpdateSignature.Verify(probe, UpdateSignature.Sign(probe, File.ReadAllBytes(path)), [publicKey]);
        Console.WriteLine($"The key file is a valid P-256 private key (public fingerprint sha256:{fingerprint}); a test signature {(signed ? "verifies" : "does NOT verify")}.");
        Console.WriteLine(index >= 0 ? $"It belongs to the trusted key number {index + 1} listed in UpdateKeys.cs." : "It is NOT among the keys the app trusts (UpdateKeys.cs): updates signed with it would be refused.");
        return signed && index >= 0 ? 0 : 3;
    }

    private static int Verify(Dictionary<string, string> o)
    {
        var pub = Need(o, "pub");
        if (File.Exists(pub))
        {
            pub = File.ReadAllText(pub).Trim();
        }

        var bytes = File.ReadAllBytes(Need(o, "manifest"));
        var ok = UpdateSignature.Verify(bytes, File.ReadAllText(Need(o, "sig")), [pub]);
        Console.WriteLine(ok ? "The signature is valid." : "The signature is NOT valid.");
        return ok ? 0 : 3;
    }
}
