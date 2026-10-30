using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mesura.Core.Update;

/// <summary>One downloadable file of a release: the installer package for one architecture.</summary>
/// <param name="Kind"><c>msi</c> (the Windows Installer package every update is, installed or fetched for a first install).</param>
/// <param name="Url">Where to fetch it. The host is not trusted: the hash and the signed manifest are what is.</param>
/// <param name="Language">The installer's own language (<c>en</c>, <c>ru</c>): the update keeps the language of the installed package (the wizard, the shortcut text); <c>null</c> is the English package an older manifest carries.</param>
/// <param name="Sha256">Lower-case hex of the file's SHA-256; the manifest it sits in is signed, so the hash is as trustworthy as the signature.</param>
public sealed record UpdatePackage(string Kind, string Architecture, string Url, string Sha256, long Size, string? Language = null);

/// <summary>
/// What the update server publishes for one channel (docs/updates.md): the newest version, its notes, its packages, and the two
/// guards against replay: <see cref="ExpiresUtc"/> (an old manifest stops being believed) and <see cref="Sequence"/> (never goes
/// down). The file is signed as it is (<see cref="UpdateSignature"/>); nothing in it is read before the signature checks.
/// </summary>
public sealed record UpdateManifest(
    int SchemaVersion,
    string Channel,
    string Version,
    long Sequence,
    DateTimeOffset PublishedUtc,
    DateTimeOffset ExpiresUtc,
    IReadOnlyDictionary<string, string> Notes,
    IReadOnlyList<UpdatePackage> Packages)
{
    public const int CurrentSchema = 1;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static UpdateManifest? TryParse(byte[] json)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, Json);
            return manifest is { Version.Length: > 0, Channel.Length: > 0 } && manifest.Packages is not null && manifest.Notes is not null ? manifest : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public byte[] ToJson() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    /// <summary>The notes in the user's language, else English, else whatever there is.</summary>
    public string NotesFor(string language) =>
        Notes.TryGetValue(language, out var text) ? text : Notes.TryGetValue("en", out var en) ? en : Notes.Values.FirstOrDefault() ?? "";

    /// <summary>The installer for this architecture in the installed language, else the English one (an older manifest has only that), else the first.</summary>
    public UpdatePackage? PackageFor(string architecture, string? language = null)
    {
        var fits = Packages.Where(p => p.Kind == "msi" && string.Equals(p.Architecture, architecture, StringComparison.OrdinalIgnoreCase)).ToList();
        return (language is not null ? fits.FirstOrDefault(p => string.Equals(p.Language, language, StringComparison.OrdinalIgnoreCase)) : null)
            ?? fits.FirstOrDefault(p => p.Language is null || string.Equals(p.Language, "en", StringComparison.OrdinalIgnoreCase))
            ?? fits.FirstOrDefault();
    }
}
