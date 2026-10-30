using System.Net;
using System.Net.Http.Headers;
using Mesura.Core.Update;

namespace Mesura.Updating;

/// <summary>Why a check or a download didn't finish, in terms the app can say plainly (the raw message goes to the technical details).</summary>
public enum UpdateFailure
{
    None,
    Offline,
    Timeout,
    ServerError,
    NotFound,
    Rejected,
    DiskFull,
    AccessDenied,
    Corrupt,
    Cancelled,
    Other,
}

/// <summary>What the app wants checked: the installed version, the channel, what the user has said so far, and whom to trust.</summary>
public sealed record UpdateRequest(string InstalledVersion, string Channel, UpdateState State, IReadOnlyList<string> TrustedKeys, string Architecture = "x64", string? BaseUrl = null, string? Language = null);

/// <summary>The outcome of a check. <see cref="ManifestBytes"/> and <see cref="Signature"/> are kept so a download is verified against exactly what was judged.</summary>
public sealed record UpdateCheckResult(UpdateCheck? Check, UpdateFailure Failure, string Detail, byte[]? ManifestBytes = null, string? Signature = null)
{
    public bool Reached => Failure == UpdateFailure.None;
}

/// <summary>
/// Asks the update server what is new (docs/updates.md). Two small files per channel: <c>update-&lt;channel&gt;.json</c> and its
/// <c>.sig</c>. Short timeouts, no retries (the next launch asks again), never throws: a server that is down, slow, missing or
/// lying leaves the installed version running and gives a <see cref="UpdateFailure"/> the app can say in a sentence.
/// </summary>
public sealed class UpdateClient : IDisposable
{
    public const string DefaultBase = "https://github.com/Ayzee287/Mesura/releases";
    public const string BaseVariable = "MESURA_UPDATE_BASE";
    private const int MaxManifestBytes = 256 * 1024;
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(12);

    private readonly HttpClient _http;

    public UpdateClient(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Mesura", typeof(UpdateClient).Assembly.GetName().Version?.ToString(3) ?? "0"));
    }

    /// <summary>The place a channel's manifest is published: stable on the latest release, preview on a rolling pre-release.</summary>
    public static string ManifestUrl(string channel, string? baseUrl = null)
    {
        var root = (baseUrl ?? Environment.GetEnvironmentVariable(BaseVariable) ?? DefaultBase).TrimEnd('/');
        var name = $"update-{channel.ToLowerInvariant()}.json";
        return baseUrl is null && Environment.GetEnvironmentVariable(BaseVariable) is null
            ? channel.Equals("preview", StringComparison.OrdinalIgnoreCase) ? $"{root}/download/preview/{name}" : $"{root}/latest/download/{name}"
            : $"{root}/{name}"; // a custom base (a test server, a mirror) serves the files flat
    }

    public async Task<UpdateCheckResult> CheckAsync(UpdateRequest request, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(CheckTimeout);
        var url = ManifestUrl(request.Channel, request.BaseUrl);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || !(parsed.Scheme == Uri.UriSchemeHttps || (parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback)))
        {
            return new UpdateCheckResult(null, UpdateFailure.Rejected, "insecure-url"); // an update source is https, or this PC itself (a local test server)
        }

        try
        {
            var manifest = await GetAsync(url, MaxManifestBytes, limit.Token);
            var signature = System.Text.Encoding.ASCII.GetString(await GetAsync(url + ".sig", 4096, limit.Token));
            var check = UpdatePolicy.Evaluate(manifest, signature, request.TrustedKeys, request.InstalledVersion, request.Channel, request.Architecture, DateTimeOffset.UtcNow, request.State, request.Language);
            return check.Outcome == UpdateOutcome.Rejected
                ? new UpdateCheckResult(check, UpdateFailure.Rejected, check.Reason)
                : new UpdateCheckResult(check, UpdateFailure.None, "", manifest, signature.Trim());
        }
        catch (Exception e)
        {
            return Classify(e, cancel);
        }
    }

    private async Task<byte[]> GetAsync(string url, int maxBytes, CancellationToken cancel)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 0 and var length && length > maxBytes)
        {
            throw new InvalidDataException($"The response is {length} bytes; at most {maxBytes} are read.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancel);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancel)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > maxBytes)
            {
                throw new InvalidDataException($"The response is longer than {maxBytes} bytes.");
            }
        }

        return buffer.ToArray();
    }

    internal static UpdateCheckResult Classify(Exception e, CancellationToken userCancel)
    {
        var (failure, detail) = e switch
        {
            OperationCanceledException when userCancel.IsCancellationRequested => (UpdateFailure.Cancelled, "cancelled"),
            OperationCanceledException or TimeoutException => (UpdateFailure.Timeout, "timed out"),
            HttpRequestException { StatusCode: HttpStatusCode.NotFound } => (UpdateFailure.NotFound, "404"),
            HttpRequestException { StatusCode: { } code } => (UpdateFailure.ServerError, ((int)code).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            HttpRequestException h => (UpdateFailure.Offline, h.Message),
            InvalidDataException => (UpdateFailure.Rejected, e.Message),
            _ => (UpdateFailure.Other, e.Message),
        };
        return new UpdateCheckResult(null, failure, detail);
    }

    public HttpClient Http => _http;

    public void Dispose() => _http.Dispose();
}
