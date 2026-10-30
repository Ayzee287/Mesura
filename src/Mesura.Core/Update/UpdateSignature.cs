using System.Security.Cryptography;

namespace Mesura.Core.Update;

/// <summary>
/// The trust model (docs/updates.md): the update manifest is signed with an ECDSA P-256 key whose private half never leaves the
/// release machine; the app carries the public half(s). A manifest is believed only if its detached signature verifies against
/// one of the trusted keys, so a hijacked download host, a rewritten release or a man in the middle can change nothing the
/// app will act on. The signature covers the manifest's exact bytes (IEEE P1363, base64).
/// </summary>
public static class UpdateSignature
{
    /// <summary>A signature in base64 over <paramref name="manifest"/>, from a PKCS#8 private key.</summary>
    public static string Sign(byte[] manifest, byte[] pkcs8PrivateKey)
    {
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(pkcs8PrivateKey, out _);
        return Convert.ToBase64String(key.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    /// <summary>Whether <paramref name="signature"/> is a valid signature of <paramref name="manifest"/> by any of the trusted public keys (SubjectPublicKeyInfo, base64).</summary>
    public static bool Verify(byte[] manifest, string signature, IEnumerable<string> trustedPublicKeys)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(trustedPublicKeys);
        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(signature.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        if (raw.Length != 64)
        {
            return false;
        }

        foreach (var trusted in trustedPublicKeys)
        {
            try
            {
                using var key = ECDsa.Create();
                key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(trusted), out _);
                if (key.VerifyData(manifest, raw, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                {
                    return true;
                }
            }
            catch (Exception e) when (e is CryptographicException or FormatException)
            {
                // A malformed key in the list never makes a signature valid; try the next.
            }
        }

        return false;
    }

    /// <summary>A new key pair: (PKCS#8 private key, SubjectPublicKeyInfo public key), both as bytes.</summary>
    public static (byte[] Private, byte[] Public) NewKeyPair()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKey(), key.ExportSubjectPublicKeyInfo());
    }

    /// <summary>The SHA-256 of a file's content as lower-case hex, read in one pass.</summary>
    public static string Sha256Hex(Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Convert.ToHexStringLower(SHA256.HashData(content));
    }
}
