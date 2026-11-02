using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Mesura.Setup.Windows.Nvidia;

/// <summary>Whether an executable is byte for byte the one a project published in its release for a version (the real check against GitHub, or a test's stand-in).</summary>
public interface IReleaseVerifier
{
    /// <returns><c>true</c> when the SHA-256 of the executable is the one inside the published release of <paramref name="version"/>; otherwise <c>false</c> with the reason in plain words.</returns>
    bool Verify(Version version, string executableSha256, CancellationToken cancellationToken, out string reason);
}

/// <summary>
/// How a NVIDIA Profile Inspector build Mesura has no hash for is still trusted without anyone adding one: the build's own version names its GitHub release (tag <c>v3.0.2.4</c>), the release's zip
/// is downloaded over https from github.com/Orbmu2k/nvidiaProfileInspector, checked against the digest GitHub lists for that file, and the executable inside is hashed and compared with the one on
/// this PC. A match means the file is what the project published; anything else (no such release, a different hash, no connection) means it is not trusted. The only thing trusted is the
/// project's own GitHub release page, reached over TLS; nothing is run from the download, it is only read in memory.
/// </summary>
public sealed class GitHubReleaseVerifier(HttpClient? http = null) : IReleaseVerifier
{
    private const string Repository = "Orbmu2k/nvidiaProfileInspector";
    private const string DownloadPrefix = "https://github.com/" + Repository + "/releases/download/";
    private const long MaxZipBytes = 8L << 20;
    private static readonly HttpClient Shared = Create();
    private readonly HttpClient _http = http ?? Shared;

    private static HttpClient Create()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mesura");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public bool Verify(Version version, string executableSha256, CancellationToken cancellationToken, out string reason)
    {
        ArgumentNullException.ThrowIfNull(version);
        try
        {
            using var release = JsonDocument.Parse(_http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/tags/v{version}", cancellationToken).GetAwaiter().GetResult());
            foreach (var asset in release.RootElement.GetProperty("assets").EnumerateArray())
            {
                var url = asset.GetProperty("browser_download_url").GetString() ?? "";
                if (!url.StartsWith(DownloadPrefix, StringComparison.Ordinal) || !url.EndsWith("/nvidiaProfileInspector.zip", StringComparison.Ordinal))
                {
                    continue;
                }

                var zip = _http.GetByteArrayAsync(url, cancellationToken).GetAwaiter().GetResult();
                if (zip.Length > MaxZipBytes)
                {
                    reason = "the published release is larger than expected";
                    return false;
                }

                if (asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } listed
                    && !listed.Equals("sha256:" + Convert.ToHexString(SHA256.HashData(zip)), StringComparison.OrdinalIgnoreCase))
                {
                    reason = "the downloaded release does not match the digest GitHub lists for it";
                    return false;
                }

                using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
                var entry = archive.GetEntry("nvidiaProfileInspector.exe");
                if (entry is null || entry.Length > 32L << 20)
                {
                    reason = "the published release has no nvidiaProfileInspector.exe";
                    return false;
                }

                using var stream = entry.Open();
                if (Convert.ToHexString(SHA256.HashData(stream)).Equals(executableSha256, StringComparison.OrdinalIgnoreCase))
                {
                    reason = "";
                    return true;
                }

                reason = $"it is not the file published in release {version}";
                return false;
            }

            reason = $"release {version} has no download";
            return false;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
        {
            reason = e is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } ? $"there is no published release {version}" : "GitHub could not be reached to check it";
            return false;
        }
    }
}
