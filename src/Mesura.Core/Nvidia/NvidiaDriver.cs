using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Mesura.Core.Nvidia;

/// <summary>
/// An NVIDIA graphics adapter as Windows reports it: its name, its PCI device id and the driver version Windows shows
/// (<c>32.0.15.6094</c>), which NVIDIA calls <c>560.94</c> (<see cref="NvidiaDriver.ToRelease"/>).
/// </summary>
public sealed record NvidiaGpu(string Name, int DeviceId, string DriverVersion)
{
    /// <summary>The driver as NVIDIA numbers it, or <c>null</c> when Windows' version can't be read that way.</summary>
    public string? Release => NvidiaDriver.ToRelease(DriverVersion);

    /// <summary>The PCI device id as NVIDIA's lookup takes it: four upper-case hex digits, e.g. <c>2786</c>.</summary>
    public string DeviceHex => DeviceId.ToString("X4", CultureInfo.InvariantCulture);
}

/// <summary>The latest driver NVIDIA's lookup service offers for a GPU, as it reported it.</summary>
public sealed record NvidiaDriverRelease(string Version, string Name, string ReleaseDate, string DetailsUrl, string DownloadUrl, string Size);

/// <summary>
/// Reading NVIDIA driver versions and NVIDIA's driver lookup service, without any I/O: the query is built here and the
/// response parsed here; <c>NvidiaDriverLookup</c> in the setup layer makes the one request.
/// <para>
/// The lookup is NVIDIA's own public endpoint (the one its download page and several open-source updaters call), but it
/// is not documented. It is asked by PCI device id for the newest WHQL Game Ready DCH driver for the Windows version, so
/// no driver version is ever written into Mesura. Read on 2026-10-23: device 2786 on Windows 11 returned 621.01.
/// </para>
/// </summary>
public static class NvidiaDriver
{
    /// <summary>NVIDIA's operating-system ids: Windows 11, and Windows 10 64-bit.</summary>
    public const int Windows11 = 135;

    public const int Windows10x64 = 57;

    /// <summary>The page to send the user to when the lookup can't answer.</summary>
    public const string DownloadPage = "https://www.nvidia.com/Download/index.aspx";

    /// <summary>
    /// The driver version as NVIDIA numbers it: the last digit of Windows' third field and the four digits of the fourth
    /// (<c>32.0.15.6094</c> is <c>560.94</c>, <c>31.0.15.5222</c> is <c>552.22</c>). <c>null</c> for anything else.
    /// </summary>
    public static string? ToRelease(string? windowsVersion)
    {
        var parts = (windowsVersion ?? "").Trim().Split('.');
        if (parts.Length != 4 || !parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit)) || parts[3].Length > 4)
        {
            return null;
        }

        var digits = parts[2][^1] + parts[3].PadLeft(4, '0');
        return $"{digits[..3]}.{digits[3..]}";
    }

    /// <summary>Whether <paramref name="release"/> (e.g. <c>621.01</c>) is newer than <paramref name="installed"/>.</summary>
    public static bool IsNewer(string? release, string? installed) =>
        Parse(release) is { } r && (Parse(installed) is not { } i || r > i);

    private static decimal? Parse(string? release) =>
        decimal.TryParse(release, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>NVIDIA's operating-system id for a Windows build.</summary>
    public static int OsId(int build) => build >= 22000 ? Windows11 : Windows10x64;

    /// <summary>The lookup query for the newest WHQL Game Ready DCH driver for this GPU on this Windows.</summary>
    public static Uri LookupUri(NvidiaGpu gpu, int build)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        return new Uri("https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php"
            + $"?func=DriverManualLookup&deviceID={gpu.DeviceHex}&osID={OsId(build)}&languageCode=1033&isWHQL=1&dch=1&sort1=0&numberOfResults=1");
    }

    /// <summary>The release in a lookup response, or <c>null</c> when NVIDIA found none (or the response isn't one).</summary>
    public static NvidiaDriverRelease? ParseLookup(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (Text(root, "Success") != "1"
                || !root.TryGetProperty("IDS", out var ids) || ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() == 0
                || !ids[0].TryGetProperty("downloadInfo", out var info) || Text(info, "Success") != "1")
            {
                return null;
            }

            var version = Text(info, "Version");
            return !IsRelease(version)
                ? null
                : new NvidiaDriverRelease(version, WebUtility.UrlDecode(Text(info, "Name")), Text(info, "ReleaseDateTime"),
                    Https(Text(info, "DetailsURL")), Https(Text(info, "DownloadURL")), Text(info, "DownloadURLFileSize"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A release number as NVIDIA writes it: three (or, one day, four) digits, a dot, two digits.</summary>
    private static bool IsRelease(string version) =>
        version.Length is 6 or 7 && version[^3] == '.' && version.Remove(version.Length - 3, 1).All(char.IsAsciiDigit);

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    /// <summary>Only an https link to NVIDIA is kept; anything else is dropped rather than opened.</summary>
    private static string Https(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            && (uri.Host.Equals("nvidia.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".nvidia.com", StringComparison.OrdinalIgnoreCase))
            ? uri.ToString()
            : "";
}
