using System.Net.Http;
using Mesura.Core.Nvidia;

namespace Mesura.Setup.Windows.Nvidia;

/// <summary>The answer of one driver lookup: the newest release, or why there is none.</summary>
public sealed record DriverLookupResult(NvidiaDriverRelease? Release, string? Error);

/// <summary>
/// Asks NVIDIA's driver lookup service for the newest driver for a GPU (one GET; <see cref="NvidiaDriver"/> builds the
/// query and reads the answer). It sends the GPU's PCI device id and the Windows version, as NVIDIA's own download page
/// does, and nothing else. The setup asks it when it shows the NVIDIA card; a failure is reported, never guessed around.
/// </summary>
public static class NvidiaDriverLookup
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static async Task<DriverLookupResult> LatestAsync(NvidiaGpu gpu, int build, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, NvidiaDriver.LookupUri(gpu, build));
            request.Headers.UserAgent.ParseAdd("Mesura");
            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new DriverLookupResult(null, $"NVIDIA's driver service answered {(int)response.StatusCode}.");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return NvidiaDriver.ParseLookup(json) is { } release
                ? new DriverLookupResult(release, null)
                : new DriverLookupResult(null, "NVIDIA's driver service has no driver listed for this GPU on this Windows version.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return new DriverLookupResult(null, $"NVIDIA's driver service couldn't be reached: {e.Message}");
        }
    }
}
