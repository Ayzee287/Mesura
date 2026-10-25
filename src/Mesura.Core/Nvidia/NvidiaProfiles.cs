using System.Security.Cryptography;

namespace Mesura.Core.Nvidia;

/// <summary>A built-in NVIDIA profile: its id, name, where it comes from and its content (rendered by Mesura, not stored).</summary>
/// <param name="Source">Where the profile's settings come from.</param>
/// <param name="Content">Renders the file's exact bytes; the same bytes every time, so the SHA-256 below is stable.</param>
public sealed record BuiltInNvidiaProfile(string Id, string Name, string Source, Func<byte[]> Content)
{
    /// <summary>The file's exact bytes.</summary>
    public byte[] Bytes() => Content();

    /// <summary>The SHA-256 of <see cref="Bytes"/>, which a setup configuration names the profile by.</summary>
    public string Sha256 => Convert.ToHexString(SHA256.HashData(Bytes()));

    /// <summary>The file name the profile is kept under in a run's folder.</summary>
    public string FileName => $"nvidia-profile-{Id}.nip";

    /// <summary>The file name of the copy adjusted for the PC (<see cref="NvidiaProfilePortability"/>), in a run's folder.</summary>
    public string AdjustedFileName => $"nvidia-profile-{Id}.adjusted.nip";
}

/// <summary>
/// The NVIDIA profiles Mesura can import after a driver install: a setup configuration names one by id and SHA-256, so
/// the file imported is always the one the configuration was made for. The one profile is Mesura's own
/// (<see cref="MesuraNvidiaProfile"/>, D036), written from NVIDIA's documented driver settings.
/// </summary>
public static class NvidiaProfiles
{
    public static BuiltInNvidiaProfile Designated { get; } = new(
        MesuraNvidiaProfile.ProfileId, "Mesura responsive gaming profile",
        "Written for Mesura from NVIDIA's documented driver settings (NvApiDriverSettings.h and the NVIDIA Control Panel help); docs/nvidia-profile.md",
        MesuraNvidiaProfile.Render);

    public static IReadOnlyList<BuiltInNvidiaProfile> All { get; } = [Designated];

    public static BuiltInNvidiaProfile? ById(string id) => All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The file a reference names, checked: the built-in profile with that id whose bytes still have the recorded SHA-256.
    /// </summary>
    /// <returns>The bytes, or why there are none.</returns>
    public static (byte[]? Bytes, string? Error) Load(NvidiaProfileRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (ById(reference.Id) is not { } profile)
        {
            return (null, $"The NVIDIA profile '{reference.Id}' isn't one this build carries.");
        }

        var bytes = profile.Bytes();
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        return hash.Equals(reference.Sha256, StringComparison.OrdinalIgnoreCase)
            ? (bytes, null)
            : (null, $"The NVIDIA profile '{reference.Id}' isn't the file the configuration names (SHA-256 {hash[..12]}…, expected {reference.Sha256[..Math.Min(12, reference.Sha256.Length)]}…).");
    }
}
