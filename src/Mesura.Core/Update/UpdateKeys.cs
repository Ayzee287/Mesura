namespace Mesura.Core.Update;

/// <summary>
/// The public keys the app trusts to sign update manifests (SubjectPublicKeyInfo, base64). The private halves are files kept by the
/// owner outside the repository (docs/updates.md). To change keys without stranding installed copies, ship a release signed by the
/// old key whose app also lists the new one, then retire the old one a release later. A public key is not a secret.
/// </summary>
public static class UpdateKeys
{
    public static readonly IReadOnlyList<string> Trusted =
    [
        // 2026-10-29: the first release key.
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEvM6ylYEeJHTyrgGT8VQhpyX6wc4h7vzyOQBEnA3Vf8z/5MGhLg/XCAtjnmX4b1fsZ7MFBECQNOo4tOFyFpoZZg==",
    ];
}
