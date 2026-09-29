using System.Text.Json.Serialization;
using FpsUtility.Core.Raw.Serialization;

namespace FpsUtility.Core.Facts;

/// <summary>Whether Steam is registered for the scanning user.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<SteamRegistration>))]
public enum SteamRegistration
{
    /// <summary>Steam registered its install path for this user, and the Steam source read it.</summary>
    Registered,

    /// <summary>
    /// The platform reports no Steam registration for this user. Evidence of absence for this user only: a portable
    /// copy, or Steam installed for another account, is not seen.
    /// </summary>
    NotRegistered,
}

/// <summary>Steam, as its registration and its own files describe it (raw <c>steam</c>).</summary>
/// <param name="Registration">Missing when the Steam source failed: a failure is never read as "not registered".</param>
/// <param name="InstallPath">Where Steam is installed, user profile rewritten.</param>
/// <param name="Libraries">The folders read as Steam libraries.</param>
public sealed record SteamFacts(
    Fact<SteamRegistration> Registration,
    Fact<string> InstallPath,
    Fact<IReadOnlyList<SteamLibraryFacts>> Libraries);

/// <param name="Id">Snapshot-scoped, in the raw snapshot's order of libraries (path).</param>
/// <param name="ListedBySteam">Whether Steam's library list names the folder.</param>
/// <param name="IsInstallFolder">Whether it is Steam's install folder, a library even when older Steam doesn't list it.</param>
/// <param name="Apps">The apps whose manifests the library holds.</param>
public sealed record SteamLibraryFacts(
    string Id,
    Fact<string> Path,
    Fact<bool> ListedBySteam,
    Fact<bool> IsInstallFolder,
    Fact<IReadOnlyList<SteamAppFacts>> Apps);

/// <summary>An installed app, as its Steam manifest declares it. A manifest that can't be read has every field missing.</summary>
/// <param name="InstallDirectory">The folder name under the library's <c>steamapps\common</c>.</param>
/// <param name="SizeOnDiskBytes">What Steam records the app occupies, in bytes.</param>
public sealed record SteamAppFacts(
    Fact<int> AppId,
    Fact<string> Name,
    Fact<string> InstallDirectory,
    Fact<ulong> SizeOnDiskBytes);
